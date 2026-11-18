using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

public static partial class EntriesStage
{
    private static readonly Regex GrantedFeatSentence = new(@"^You gain the (?<f>[A-Z][A-Za-z' -]+?)(?: [a-z]+)? feat\b", RegexOptions.CultureInvariant);

    private static readonly Regex LearnedCompositionSentence = new(@"^You learn the (?<f>[a-z][a-z' -]+?) composition (?<k>spell|cantrip)\b", RegexOptions.CultureInvariant);

    /// <summary>The marker a grant holds in place of an entry's id until every id is final: <c>@feat(Name)</c> or <c>@spell(Name)</c>, resolved by name after the run's ids are known.</summary>
    public static readonly Regex GrantToken = new(@"@(?<kind>feat|spell)\((?<name>[^)]+)\)", RegexOptions.CultureInvariant);

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
                case "Order Skill" when Regex.IsMatch(text, @"^[A-Z][a-z]+$", RegexOptions.CultureInvariant):
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
            }
        }

        return adds;
    }
}
