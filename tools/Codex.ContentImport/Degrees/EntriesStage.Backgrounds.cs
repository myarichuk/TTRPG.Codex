using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>A background as the blocks hold it: the name, and its text as one run (the story, then the rules).</summary>
public sealed class BackgroundScan
{
    public string Name { get; set; } = string.Empty;
    public List<Block> Blocks { get; } = new();
}

public static partial class EntriesStage
{
    private static readonly Regex BackgroundHeader = new(@"^(?<name>.+?)\s+BACKGROUND$", RegexOptions.CultureInvariant);

    /// <summary>The first words of the sentence that opens a background's rules (a shape, not the sentence).</summary>
    internal const string BackgroundRulesStart = "Choose two attribute";

    private static readonly string[] SkillNames =
    {
        "Acrobatics", "Arcana", "Athletics", "Crafting", "Deception", "Diplomacy", "Intimidation", "Medicine", "Nature", "Occultism", "Performance", "Religion", "Society", "Stealth", "Survival", "Thievery",
    };

    /// <summary>Sorts a backgrounds range (after its title block): the blocks before the first header are the section's introduction, each header in capitals that ends in the word is a background and its blocks are its text.</summary>
    public static (List<Block> Intro, List<BackgroundScan> Entries) ScanBackgrounds(List<Block> slice)
    {
        var intro = new List<Block>();
        var entries = new List<BackgroundScan>();
        foreach (var b in slice)
        {
            if (b.Role == "block-header")
            {
                var m = BackgroundHeader.Match(BlockText(b));
                if (!m.Success)
                {
                    throw new DegreesException($"A backgrounds range holds a heading on page {b.Page}, column {b.Column}, that does not end in the background word.");
                }

                entries.Add(new BackgroundScan { Name = TitleCase(m.Groups["name"].Value) });
            }
            else if (b.Role is "rules" or "body" or "icon")
            {
                (entries.Count == 0 ? intro : entries[^1].Blocks).Add(b);
            }
            else
            {
                throw new DegreesException($"A backgrounds range holds a {b.Role} block on page {b.Page}, column {b.Column}, where only headings and text are.");
            }
        }

        return (intro, entries);
    }

    /// <summary>The text of one background as the entry reader joins it, and where its rules begin (<see cref="BackgroundRulesStart"/> opens them).</summary>
    private static (ParsedEntry Entry, string Story, string Rules) ReadBackground(TakenRange range, BackgroundScan scan, string book, HashSet<string> compounds)
    {
        var entry = NewEntry(range, book, scan.Name, compounds, "background", "backgrounds", null, null);
        var reader = new BodyReader(entry, compounds, true);
        foreach (var block in scan.Blocks)
        {
            reader.Read(block);
        }

        reader.Finish();
        var text = string.Join(' ', entry.Parts.Select(p => p.Label.Length == 0 ? p.Text : p.Label + " " + p.Text)).Trim();
        var at = text.IndexOf(BackgroundRulesStart, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(BackgroundRulesStart, at + 1, StringComparison.Ordinal) >= 0)
        {
            throw new DegreesException($"A background (page {scan.Blocks.FirstOrDefault()?.Page}) does not hold the sentence that opens its rules exactly once.");
        }

        return (entry, text[..at].Trim(), text[at..].Trim());
    }

    /// <summary>The story of each background (the text before its rules), as the blocks the denylist hashes with the other left-out text.</summary>
    public static List<Block> BackgroundFlavor(List<Block> slice)
    {
        var (_, entries) = ScanBackgrounds(slice);
        return entries.Where(e => e.Blocks.Count > 0).Select(e =>
        {
            var joined = string.Join(' ', e.Blocks.SelectMany(b => b.Lines).Select(l => SpanText.Normalize(l).Trim()));
            var at = joined.IndexOf(BackgroundRulesStart, StringComparison.Ordinal);
            return e.Blocks[0] with { Lines = new List<string> { at < 0 ? joined : joined[..at].Trim() }, Info = null, Rows = null };
        }).ToList();
    }

    private static string SkillKey(string name) => name.ToLowerInvariant();

    /// <summary>
    /// The <c>backgrounds</c> parser: the introduction before the first header is one <c>rule</c> entry; each background is a <c>background</c> entry. Its story is read past; its description is the book's rules
    /// sentences (the boosts, the trained skills, the skill feat). The boosts, the trained skills, the Lore subject and the skill feat are fields, read from those sentences; a sentence of another shape fails the run.
    /// </summary>
    private static List<ParsedEntry> ParseBackgrounds(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var (introBlocks, scans) = ScanBackgrounds(slice);
        var result = new List<ParsedEntry>();
        if (introBlocks.Count > 0)
        {
            var rule = NewEntry(range, book, Clean(range.Row.PathOf(range)[^1]), compounds, "rule", "rules", range.Row.Category);
            var reader = new BodyReader(rule, compounds, false);
            foreach (var block in introBlocks)
            {
                reader.Read(block);
            }

            reader.Finish();
            result.Add(rule);
        }

        foreach (var scan in scans)
        {
            var (entry, _, rules) = ReadBackground(range, scan, book, compounds);
            entry.Parts.Clear();
            entry.Parts.Add(new LabelledText(string.Empty, rules));

            // The boosts, by shape: a second sentence that names two attributes joined by "or" and says one more is free, or one that names an attribute of the deity instead.
            var boosts = new List<string>();
            if (Regex.Match(rules, @"^Choose two attribute[^.]*\. One must be to (?<a>\w+) or (?<b>\w+),[^.]*\bfree\b", RegexOptions.CultureInvariant) is { Success: true } pair
                && AttributeKeys.TryGetValue(pair.Groups["a"].Value, out var first) && AttributeKeys.TryGetValue(pair.Groups["b"].Value, out var second))
            {
                boosts.Add(first + "|" + second);
                boosts.Add("free");
            }
            else if (Regex.IsMatch(rules, @"^Choose two attribute[^.]*\. One must be to an attribute[^.]*\bdeity\b[^.]*\bfree\b", RegexOptions.CultureInvariant))
            {
                // The deity's attribute is another entry's: here both are free, and the sentence in the description says which one is limited.
                boosts.Add("free");
                boosts.Add("free");
            }
            else
            {
                throw new DegreesException($"A background (page {scan.Blocks.FirstOrDefault()?.Page}) does not give its boosts in either of the two shapes the parser knows.");
            }

            // The trained skills and the Lore subject: every "trained in" sentence is read; a Lore phrase gives the subject, any skill word before it gives a skill (several are a choice).
            var skills = new List<string>();
            string? lore = null;
            foreach (Match trained in Regex.Matches(rules, @"You(?:'re| are) (?:also )?trained in (?<s>[^.]*?)\.(?= |$)", RegexOptions.CultureInvariant))
            {
                var s = trained.Groups["s"].Value;
                var cut = s.IndexOf("Lore", StringComparison.Ordinal);
                var skillPart = cut < 0 ? s : s[..cut];
                if (cut >= 0)
                {
                    if (Regex.Match(s, @"\ba Lore skill (?<rest>.*)$", RegexOptions.CultureInvariant) is { Success: true } variable)
                    {
                        // "a Lore skill related to ...": the phrase after the word is the subject, as printed.
                        lore = variable.Groups["rest"].Value.Trim();
                        skillPart = s[..variable.Index];
                    }
                    else if (Regex.Match(s, @"(?<a>[A-Z][\w-]*) Lore(?: or (?<b>[A-Z][\w-]*) Lore)? skill", RegexOptions.CultureInvariant) is { Success: true } named)
                    {
                        lore = named.Groups["b"].Success ? named.Groups["a"].Value + " or " + named.Groups["b"].Value : named.Groups["a"].Value;
                        skillPart = s[..named.Index];
                    }
                    else
                    {
                        throw new DegreesException($"A background (page {scan.Blocks.FirstOrDefault()?.Page}) names a Lore skill in a shape the parser does not know.");
                    }
                }

                var found = SkillNames.Where(n => Regex.IsMatch(skillPart, @"\b" + n + @"\b", RegexOptions.CultureInvariant)).OrderBy(n => skillPart.IndexOf(n, StringComparison.Ordinal)).ToList();
                if (found.Count > 1 && !skillPart.Contains(" or ", StringComparison.Ordinal))
                {
                    throw new DegreesException($"A background (page {scan.Blocks.FirstOrDefault()?.Page}) trains several skills at once; the parser reads several skill words as a choice and this sentence has no 'or'.");
                }

                if (found.Count > 0)
                {
                    skills.Add(string.Join('|', found.Select(SkillKey)));
                }
            }

            // The skill feat: "You gain the X skill feat", "... the X feat with that skill", or two feats by the skill chosen.
            string? feat = null;
            if (Regex.Match(rules, @"\bskill feat: (?<a>[A-Z][A-Za-z' -]+?) if [^.]*? or (?<b>[A-Z][A-Za-z' -]+?) if ", RegexOptions.CultureInvariant) is { Success: true } two)
            {
                feat = Slug(two.Groups["a"].Value) + "|" + Slug(two.Groups["b"].Value);
            }
            else if (Regex.Match(rules, @"\bgain the (?<f>[A-Z][A-Za-z' -]*?) (?:skill )?feat\b", RegexOptions.CultureInvariant) is { Success: true } one)
            {
                feat = Slug(one.Groups["f"].Value);
            }

            if (feat == null)
            {
                throw new DegreesException($"A background (page {scan.Blocks.FirstOrDefault()?.Page}) does not name the skill feat it grants in a shape the parser knows.");
            }

            entry.Raw.Add(new LabelledText("boosts", FlowList(boosts)));
            if (skills.Count > 0)
            {
                entry.Raw.Add(new LabelledText("skills", FlowList(skills)));
            }

            if (lore != null)
            {
                entry.Extracted.Add(new LabelledText("lore", lore));
            }

            entry.Extracted.Add(new LabelledText("skillFeat", feat));
            result.Add(entry);
        }

        return result;
    }
}
