using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.TranslationCheck;

public enum Severity { Error, Warning }

/// <param name="Entry">"type:id" of the entry the finding is about.</param>
/// <param name="Code">Short stable name of the check, so reports can be grouped and counted.</param>
public sealed record Finding(Severity Severity, string Entry, string Code, string Message);

/// <summary>
/// Mechanical checks for a machine-drafted translation of one rules entry. A model can phrase text however it likes, but it
/// must not change a number, a die, the shape of the markdown, or the script of the language, and it should use the
/// project's agreed terms. Errors mean "do not accept this entry"; warnings mean "a person should look".
/// </summary>
public sealed class TranslationChecker
{
    private static readonly string[] AllowedTopLevel = { "id", "name", "description", "properties" };

    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled);
    private static readonly Regex Dice = new(@"(?<![A-Za-z\d])\d*[dD]\d+(?![A-Za-z\d])", RegexOptions.Compiled);
    private static readonly Regex Cyrillic = new(@"[Ѐ-ӿ]", RegexOptions.Compiled);
    private static readonly Regex HebrewLetters = new(@"[֐-׿]", RegexOptions.Compiled);
    private static readonly Regex MixedWord = new(@"[Ѐ-ӿ][֐-׿]|[֐-׿][Ѐ-ӿ]", RegexOptions.Compiled);
    private static readonly Regex LatinLetter = new(@"[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex AnyLetter = new(@"\p{L}", RegexOptions.Compiled);
    // Words that are notation, not prose: dice, ability and stat abbreviations, units.
    private static readonly Regex Notation = new(@"\b\d*[dD]\d+\b|\b(?:STR|DEX|CON|INT|WIS|CHA|AC|HP|XP|CR|DC|PB|GP|SP|CP|ft|mi)\b\.?", RegexOptions.Compiled);

    private readonly string _language;
    private readonly IReadOnlyDictionary<string, string> _glossary;

    /// <param name="language">"ru" or "he".</param>
    /// <param name="glossary">English term as the rules text writes it -> the agreed translation (conditions, damage types, schools...).</param>
    public TranslationChecker(string language, IReadOnlyDictionary<string, string>? glossary = null)
    {
        _language = language;
        _glossary = glossary ?? new Dictionary<string, string>();
    }

    /// <summary>The agreed terms of a system's translation: shared values, conditions, attribute labels, skill labels.</summary>
    public static Dictionary<string, string> GlossaryOf(SystemDefinition system, string language)
    {
        var glossary = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!system.Translations.TryGetValue(language, out var t)) return glossary;

        foreach (var (english, shown) in t.Values) glossary[english] = shown;
        foreach (var (english, shown) in t.Conditions) glossary[english] = shown;
        foreach (var a in system.Attributes)
            if (t.Attributes.TryGetValue(a.Key, out var label) && label.Label != null) glossary[a.Label] = label.Label;
        foreach (var s in system.SheetRules?.Skills?.List ?? new List<SkillDefinition>())
            if (t.SheetRules.Skills.TryGetValue(s.Key, out var shown)) glossary[s.Label] = shown;
        return glossary;
    }

    /// <summary>Checks one translated item against the entry it translates.</summary>
    /// <param name="source">The original entry, or null when the id names nothing.</param>
    /// <param name="type">The entry type, used for the key and to pick the properties a translation may carry.</param>
    /// <param name="item">The parsed YAML mapping of the translated item.</param>
    /// <param name="textFields">Property keys a translation may carry (free text and row tables of the type).</param>
    public List<Finding> Check(ContentEntry? source, string type, IReadOnlyDictionary<string, object> item, IReadOnlySet<string> textFields)
    {
        var id = item.TryGetValue("id", out var rawId) ? rawId?.ToString() ?? "" : "";
        var key = $"{type}:{id}";
        var findings = new List<Finding>();
        void Add(Severity severity, string code, string message) => findings.Add(new Finding(severity, key, code, message));

        if (string.IsNullOrWhiteSpace(id)) { Add(Severity.Error, "no-id", "the item has no id"); return findings; }
        if (source == null) { Add(Severity.Error, "unknown-id", "no entry of this type has this id"); return findings; }

        foreach (var field in item.Keys.Where(k => !AllowedTopLevel.Contains(k) && !ContentEntry.FlavorKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
            Add(Severity.Error, "forbidden-field", $"'{field}' is not display text; a translation may only carry id, name, description, flavor text and text properties");

        var name = Text(item, "name");
        if (string.IsNullOrWhiteSpace(name)) Add(Severity.Error, "no-name", "the translated name is empty");
        else if (name == source.Name && AnyLetter.IsMatch(name)) Add(Severity.Warning, "name-unchanged", $"name '{name}' is identical to the English one");

        var description = Text(item, "description");
        if (!string.IsNullOrWhiteSpace(source.Description) && !item.Keys.Any(k => ContentEntry.FlavorKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(description)) Add(Severity.Error, "no-description", "the original has a description and the translation has none");
            else CheckProse(source.Description, description, Add);
        }

        if (item.TryGetValue("properties", out var props) && props is System.Collections.IDictionary map)
        {
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                var propKey = entry.Key?.ToString() ?? "";
                if (!textFields.Contains(propKey))
                {
                    Add(Severity.Error, "forbidden-property", $"property '{propKey}' is not translatable text (the rules read it)");
                    continue;
                }

                if (entry.Value is string translated && source.Properties.TryGetValue(propKey, out var original) && original is string originalText)
                    CheckProse(originalText, translated, (s, c, m) => Add(s, c, $"property {propKey}: {m}"), light: true);
            }
        }

        return findings;
    }

    /// <summary>Thousands separators differ by language ("1,000", "1 000", "1.000"); read them all as the plain number.</summary>
    private static readonly Regex ThousandsSeparator = new(@"(?<=\d)[,\u00a0\u202f\u2009 ](?=\d{3}(?!\d))", RegexOptions.Compiled);
    internal static string Unseparated(string text) => ThousandsSeparator.Replace(text, "");

    private void CheckProse(string original, string translated, Action<Severity, string, string> add, bool light = false)
    {
        // Numbers: a changed number changes a rule.
        var wanted = Count(Unseparated(original), Number, normalize: n => n.Replace(',', '.'));
        var got = Count(Unseparated(translated), Number, normalize: n => n.Replace(',', '.'));
        var missing = Difference(wanted, got);
        var extra = Difference(got, wanted);
        if (missing.Count > 0) add(Severity.Error, "numbers-missing", $"numbers in the original but not the translation: {string.Join(", ", missing)}");
        if (extra.Count > 0) add(Severity.Warning, "numbers-extra", $"numbers in the translation but not the original: {string.Join(", ", extra)}");

        // Dice must be reproduced exactly.
        var diceMissing = Difference(Count(original, Dice, d => d.ToLowerInvariant()), Count(translated, Dice, d => d.ToLowerInvariant()));
        var diceExtra = Difference(Count(translated, Dice, d => d.ToLowerInvariant()), Count(original, Dice, d => d.ToLowerInvariant()));
        if (diceMissing.Count > 0)
            add(Severity.Error, "dice", $"dice missing or changed (missing: {string.Join(", ", diceMissing)}; extra: {string.Join(", ", diceExtra)})");
        else if (diceExtra.Count > 0)
            add(Severity.Warning, "dice", $"dice in the translation but not the original: {string.Join(", ", diceExtra)}");

        CheckScript(translated, add);

        if (light) return;

        // Markdown shape: headings, table rows and bullets must survive; bold may move around, so only warn.
        var a = Structure(original);
        var b = Structure(translated);
        // Lost structure is an error. Gained structure is only a warning: some originals have tables and lists flattened into prose.
        foreach (var part in new[] { "headings", "table rows", "bullets", "paragraphs" })
            if (a[part] != b[part]) add(part == "paragraphs" || b[part] > a[part] ? Severity.Warning : Severity.Error, "structure", $"{part}: {a[part]} in the original, {b[part]} in the translation");
        if (a["bold"] != b["bold"]) add(Severity.Warning, "structure", $"bold markers: {a["bold"]} in the original, {b["bold"]} in the translation");

        // Length: a big shrink usually means a dropped sentence.
        var ratio = (double)translated.Length / Math.Max(1, original.Length);
        if (original.Length > 200 && (ratio < 0.55 || ratio > 2.6)) add(Severity.Warning, "length", $"the translation is {ratio:P0} of the original's length");

        // Untranslated prose.
        var prose = Notation.Replace(original.Length > 0 ? translated : "", "");
        var letters = AnyLetter.Matches(prose).Count;
        if (letters > 80)
        {
            var latin = LatinLetter.Matches(prose).Count;
            if ((double)latin / letters > 0.12) add(Severity.Warning, "untranslated", $"{(double)latin / letters:P0} of the letters are still Latin");
        }

        // Agreed terms.
        var wrongTerms = new List<string>();
        foreach (var (english, shown) in _glossary)
        {
            if (english.Length < 4 || shown.Length < 3) continue;
            // "force" is also a plain verb and noun; only the damage type is an agreed term.
            var pattern = english.Equals("force", StringComparison.OrdinalIgnoreCase) ? @"\b[Ff]orce damage\b" : $@"\b{Regex.Escape(english)}\b";
            if (!Regex.IsMatch(original, pattern)) continue;
            var stem = Stem(shown);
            if (stem.Length >= 3 && translated.IndexOf(stem, StringComparison.OrdinalIgnoreCase) < 0) wrongTerms.Add($"{english} → {shown}");
        }
        if (wrongTerms.Count > 0) add(Severity.Warning, "terms", $"agreed terms not found: {string.Join("; ", wrongTerms.Take(8))}{(wrongTerms.Count > 8 ? "…" : "")}");
    }

    private void CheckScript(string text, Action<Severity, string, string> add)
    {
        var foreign = _language == "he" ? Cyrillic : HebrewLetters;
        if (foreign.IsMatch(text)) add(Severity.Error, "script", $"contains {(_language == "he" ? "Cyrillic" : "Hebrew")} letters");
        if (MixedWord.IsMatch(text)) add(Severity.Error, "script", "a word mixes Cyrillic and Hebrew letters");
        var own = _language == "he" ? HebrewLetters : Cyrillic;
        if (AnyLetter.Matches(text).Count > 40 && !own.IsMatch(text)) add(Severity.Error, "script", $"has no {(_language == "he" ? "Hebrew" : "Russian")} letters at all");
    }

    /// <summary>Counts of the markdown features that make up an entry's layout.</summary>
    internal static Dictionary<string, int> Structure(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var trimmed = lines.Select(l => l.Trim()).ToList();
        return new Dictionary<string, int>
        {
            ["headings"] = trimmed.Count(l => l.StartsWith('#')),
            ["table rows"] = trimmed.Count(l => l.StartsWith('|')),
            ["bullets"] = trimmed.Count(l => l.StartsWith("- ") || l.StartsWith("* ")),
            ["bold"] = Regex.Matches(markdown, @"\*\*").Count / 2,
            ["paragraphs"] = Regex.Split(markdown.Trim(), @"\n\s*\n").Count(p => p.Trim().Length > 0)
        };
    }

    /// <summary>A prefix of a translated term short enough to survive inflection (Russian cases, Hebrew prefixes).</summary>
    internal static string Stem(string term)
    {
        var firstWord = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? term;
        // Drop an ending that inflection changes (Сила/Силы/Силу, existing/exist-ing); short words lose only a final vowel.
        var cut = firstWord.Length >= 7 ? 3 : firstWord.Length >= 5 ? 2 : firstWord.Length == 4 && "аяоеьйыуюи".Contains(firstWord[^1]) ? 1 : 0;
        return firstWord[..(firstWord.Length - cut)];
    }

    internal static List<string> Difference(Dictionary<string, int> bigger, Dictionary<string, int> smaller)
    {
        var result = new List<string>();
        foreach (var (token, count) in bigger)
        {
            var other = smaller.GetValueOrDefault(token);
            for (var i = other; i < count; i++) result.Add(token);
        }
        return result;
    }

    internal static Dictionary<string, int> Count(string text, Regex pattern, Func<string, string> normalize)
    {
        var counts = new Dictionary<string, int>();
        foreach (Match m in pattern.Matches(text))
        {
            var token = normalize(m.Value);
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }
        return counts;
    }

    private static string Text(IReadOnlyDictionary<string, object> item, string key) =>
        item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
}
