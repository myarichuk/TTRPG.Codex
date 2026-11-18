using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

public static partial class EntriesStage
{
    private static readonly Regex GrantedFeatSentence = new(@"^You gain the (?<f>[A-Z][A-Za-z' -]+?)(?: [a-z]+)? feat\b", RegexOptions.CultureInvariant);

    private static readonly Regex LearnedCompositionSentence = new(@"^You learn the (?<f>[a-z][a-z' -]+?) composition (?<k>spell|cantrip)\b", RegexOptions.CultureInvariant);

    /// <summary>A hex a patron's lesson gives: <c>You gain the n hex cantrip</c>, <c>You learn the n hex</c>. Which list it goes to (a hex cantrip is a cantrip, a hex a focus spell) is read from the spell's kind once the ids are final.</summary>
    private static readonly Regex GainedHexSentence = new(@"^You (?:gain|learn) the (?<n>[a-z][a-z' -]+?) hex(?: cantrip)?\b", RegexOptions.CultureInvariant);

    /// <summary>The marker a grant holds in place of an entry's id until every id is final: <c>@feat(Name)</c>, <c>@spell(Name)</c> or <c>@hex(Name)</c>, resolved by name after the run's ids are known.</summary>
    public static readonly Regex GrantToken = new(@"@(?<kind>feat|spell|hex)\((?<name>[^)]+)\)", RegexOptions.CultureInvariant);

    private static string Sentences(string text) => Regex.Replace(text.Replace('\n', ' '), @"\*\*[^*]+?\*\*\s*", string.Empty);

    /// <summary>
    /// The grants a class feature's own sentences make by name (shapes only): <c>You gain the N [class] feat</c> is a grant to the sheet's feats, and <c>You learn the n composition spell</c> (or cantrip) a
    /// grant to its focus spells (to the spells a feature grants when it is a cantrip). A sentence that starts otherwise (a condition, a choice) makes none. The name stays a marker until the ids are final (<see cref="GrantToken"/>).
    /// </summary>
    public static List<string> NamedGrants(string text)
    {
        var adds = new List<string>();
        foreach (var sentence in Regex.Split(Sentences(text), @"(?<=[.!?])\s+(?=[A-Z])"))
        {
            var s = sentence.Trim();
            if (GrantedFeatSentence.Match(s) is { Success: true } feat)
            {
                adds.Add("{ to: feats, entries: [@feat(" + feat.Groups["f"].Value + ")] }");
            }
            else if (LearnedCompositionSentence.Match(s) is { Success: true } spell)
            {
                // A composition cantrip is a cantrip (its kind), so the sheet's list of focus spells, which holds the kind `focus`, would not take it: it goes to the spells a feature grants.
                adds.Add("{ to: " + (spell.Groups["k"].Value == "spell" ? "focusSpells" : "grantedSpells") + ", entries: [@spell(" + spell.Groups["f"].Value + ")] }");
            }
            else if (GainedHexSentence.Match(s) is { Success: true } hex)
            {
                adds.Add("{ to: @hex, entries: [@hex(" + hex.Groups["n"].Value + ")] }");
            }
        }

        return adds;
    }

    /// <summary>
    /// The grants of a subclass's labelled lines: the skill an order trains, the class feat and the spell a choice gives. A label the reader does not know makes none (it stays in the text);
    /// a skill that is no single word fails nothing and makes none (the player picks a skill instead).
    /// </summary>
    public static List<string> LabelledGrants(IEnumerable<LabelledText> parts)
    {
        var adds = new List<string>();
        foreach (var part in parts)
        {
            var text = part.Text.Trim().TrimEnd('.');
            switch (part.Label.Trim())
            {
                case "Order Skill" or "Patron Skill" when Regex.IsMatch(text, @"^[A-Z][a-z]+$", RegexOptions.CultureInvariant):
                    adds.Add("{ to: skills, entries: [" + Slug(text) + "], rank: trained }");
                    break;
                case "Druid Feat" or "Muse Feat" when text.Length > 0:
                    adds.Add("{ to: feats, entries: [@feat(" + text + ")] }");
                    break;
                case "Order Spell" when text.Length > 0:
                    adds.Add("{ to: focusSpells, entries: [@spell(" + text + ")] }");
                    break;
                case "Muse Spell" when text.Length > 0:
                    adds.Add("{ to: grantedSpells, entries: [@spell(" + text + ")] }");
                    break;
                case "School Spells" when SchoolSpellsLine.Match(text) is { Success: true } school:
                    // The school's initial focus spell is its own; the advanced one comes with a feat, so it is text.
                    adds.Add("{ to: focusSpells, entries: [@spell(" + school.Groups["i"].Value + ")] }");
                    break;
            }
        }

        return adds;
    }

    private static readonly Regex SchoolSpellsLine = new(@"^initial: (?<i>[^;]+?)(?:;|$)", RegexOptions.CultureInvariant);

    private static readonly Regex TraditionLine = new(@"^(?<t>arcane|divine|occult|primal)(?:;|$)", RegexOptions.CultureInvariant);

    /// <summary>A lone capital U that the layout sets in a list of names (the book's mark of an uncommon spell, set as a superscript): not a word.</summary>
    private static readonly Regex UncommonMark = new(@"(?<=\s|^)U(?=\s|,|;|$)\s*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Tidies the labelled lines of a choice that sits in a section of its own (a patron, a school): a line whose text holds the next label after a semicolon (<c>Spell List divine; Patron Skill Religion</c>)
    /// is two lines; a list of names continued in a block of its own (a column break) goes on the line before; the marks of uncommon spells are dropped from a curriculum, and the line that runs
    /// into it with the school's own spells (a label after the list's last name) is cut there.
    /// </summary>
    public static List<LabelledText> TidySubclassParts(IEnumerable<LabelledText> parts)
    {
        var result = new List<LabelledText>();
        foreach (var raw in parts)
        {
            var part = raw;
            if (part.Label.Length == 0 && result.Count > 0 && result[^1].Label == "Curriculum" && part.Text.Length > 0 && char.IsLower(part.Text[0]))
            {
                result[^1] = result[^1] with { Text = result[^1].Text + " " + part.Text };
                continue;
            }

            if (part.Label == "Spell List" && Regex.Match(part.Text, @"^(?<a>[^;]+); Patron Skill (?<b>.+)$", RegexOptions.CultureInvariant) is { Success: true } line)
            {
                // A value of one word (a tradition) is no sentence; the explanation of the label (several words) lost its full stop at the semicolon.
                var first = line.Groups["a"].Value.Trim();
                result.Add(part with { Text = first.Contains(' ') && !".!?".Contains(first[^1]) ? first + "." : first });
                result.Add(new LabelledText("Patron Skill", line.Groups["b"].Value.Trim()));
                continue;
            }

            result.Add(part);
        }

        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Label != "Curriculum")
            {
                continue;
            }

            var text = UncommonMark.Replace(result[i].Text, string.Empty).Trim();
            if (Regex.Match(text, @"^(?<c>.+?)\s+School Spells (?<s>initial: .+)$", RegexOptions.CultureInvariant) is { Success: true } run)
            {
                result[i] = result[i] with { Text = run.Groups["c"].Value };
                result.Insert(i + 1, new LabelledText("School Spells", run.Groups["s"].Value));
            }
            else
            {
                result[i] = result[i] with { Text = text };
            }
        }

        return result;
    }

    /// <summary>The tradition a patron's line names (<c>divine; Patron Skill Religion</c> once tidied: <c>divine</c>), or null.</summary>
    public static string? TraditionOf(IEnumerable<LabelledText> parts) =>
        parts.Where(p => p.Label == "Spell List").Select(p => TraditionLine.Match(p.Text.Trim())).Where(m => m.Success).Select(m => m.Groups["t"].Value).SingleOrDefault();
}
