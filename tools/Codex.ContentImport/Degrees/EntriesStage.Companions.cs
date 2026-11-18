using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>A heading of a section and the blocks under it, up to the next heading in the section or entry-header font. The unit before the first heading has no header: it is the section's own text.</summary>
public sealed class HeadedUnit
{
    public Block? Header { get; set; }
    public List<Block> Blocks { get; } = new();
}

public static partial class EntriesStage
{
    /// <summary>The bold labels a companion's statistics are set in, in the order the book prints them (an attack line repeats; a special line and an access line are optional).</summary>
    private static readonly string[] CompanionLabels = { "Access", "Size", "Melee", "Ranged", "Str", "Hit Points", "Skill", "Senses", "Speed", "Special", "Support Benefit", "Advanced Maneuver" };

    private static readonly Regex CompanionAttack = new(@"^(?<kind>Melee|Ranged) \[1 action\] (?<name>[A-Za-z' -]+?)(?: \((?<traits>[^)]+)\))?, Damage (?<dice>\d+d\d+) (?<type>[a-z]+)(?<extra>.*)$", RegexOptions.CultureInvariant);

    private static readonly Regex CompanionModifier = new(@"^(?<key>Str|Dex|Con|Int|Wis|Cha) (?<sign>[+-])(?<n>\d+)$", RegexOptions.CultureInvariant);

    private static readonly string[] ModifierKeys = { "Str", "Dex", "Con", "Int", "Wis", "Cha" };

    /// <summary>The text of a heading as a name: as printed, or in title case when the book sets it in capitals.</summary>
    internal static string HeadingName(Block header)
    {
        var text = Clean(string.Join(' ', header.Lines));
        return text.Any(char.IsLower) ? text : TitleCase(text);
    }

    private static string YamlString(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool HasIcon(string text) => Icons.Keys.Any(t => text.Contains(t, StringComparison.Ordinal));

    /// <summary>A heading in the block-header font that is neither an action's (it has an icon) nor a feat's (a name, the word feat and a level): a sub-subsection, a rule of its own.</summary>
    internal static bool IsPlainSubheading(Block b) =>
        b.Role == "block-header" && !HasIcon(BlockText(b)) && !AncestryFeatHeader.IsMatch(HeaderWithoutIcons(BlockText(b)));

    /// <summary>
    /// Sorts the blocks of a range into units: a heading in the section or entry-header font starts one, and the blocks under it are its. The blocks before the first heading are the unit with no header.
    /// The labels under a picture (a body block in capitals alone) are set apart in <paramref name="captions"/>: read past, and hashed with the left-out text.
    /// </summary>
    internal static List<HeadedUnit> SplitUnits(List<Block> slice, List<Block> captions)
    {
        var units = new List<HeadedUnit> { new() };
        foreach (var b in slice)
        {
            if (IsArtLabel(b))
            {
                captions.Add(b);
                continue;
            }

            if (b.Role is "section" or "entry-header")
            {
                units.Add(new HeadedUnit { Header = b });
                continue;
            }

            units[^1].Blocks.Add(b);
        }

        return units;
    }

    /// <summary>A unit that holds the line of attribute modifiers, which is bold at its first word (the legend of the lines, in a rule, spells that label out), is a companion's; any other unit is a rule.</summary>
    internal static bool IsCompanionUnit(HeadedUnit unit) =>
        unit.Header != null && unit.Blocks.Any(b => b.Role == "body" && b.Info != null && b.Info.Any(i => SpanText.Normalize(i.Label).Trim() == "Str"));

    /// <summary>A unit as rules: the text under its heading, then one more for each sub-heading in the block-header font (with the text under it).</summary>
    internal static List<(string Name, List<Block> Blocks)> RuleSubunits(HeadedUnit unit)
    {
        var result = new List<(string, List<Block>)>();
        var current = (Name: HeadingName(unit.Header!), Blocks: new List<Block>());
        foreach (var b in unit.Blocks)
        {
            if (IsPlainSubheading(b))
            {
                result.Add(current);
                current = (HeadingName(b), new List<Block>());
                continue;
            }

            current.Blocks.Add(b);
        }

        result.Add(current);
        return result;
    }

    /// <summary>A rule entry of the blocks under a heading: its paragraphs, lists and actions (a heading with an icon, its trait line and its labelled lines), set out as a class feature's text is.</summary>
    internal static ParsedEntry RuleEntry(TakenRange range, string book, string name, List<Block> blocks, HashSet<string> compounds, string? sourceTail)
    {
        if (blocks.Count == 0)
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}: a heading in the range holds no text, so it would make an empty rule.");
        }

        if (range.Row.EntryNames.TryGetValue(name, out var renamed))
        {
            name = renamed;
        }

        var rule = NewEntry(range, book, name, compounds, "rule", "rules", range.Row.Category, sourceTail);
        rule.Parts.AddRange(ReadFeature(name, 1, blocks, compounds, range.Row.Chapter, true).Parts);
        DashBullets(rule);
        return rule;
    }

    /// <summary>The labels under the pictures of the companions' pages: read past, so they are left-out text for the denylist.</summary>
    public static List<Block> CompanionFlavor(List<Block> slice)
    {
        var captions = new List<Block>();
        SplitUnits(slice, captions);
        return captions;
    }

    /// <summary>
    /// The <c>companions</c> parser: each unit that holds a stat line labelled Size is a <c>companion</c> entry (its labelled lines are fields, its attacks a list of rows, its advanced maneuver the entry's text);
    /// every other unit is a <c>rule</c> (a heading in the block-header font with no icon starts another). The text before the first heading is a rule named by the path's last title.
    /// </summary>
    private static List<ParsedEntry> ParseCompanions(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var captions = new List<Block>();
        var result = new List<ParsedEntry>();
        foreach (var unit in SplitUnits(slice, captions))
        {
            if (unit.Header == null)
            {
                if (unit.Blocks.Count > 0)
                {
                    result.Add(RuleEntry(range, book, Clean(range.Row.PathOf(range)[^1]), unit.Blocks, compounds, null));
                }

                continue;
            }

            if (IsCompanionUnit(unit))
            {
                result.Add(ReadCompanion(range, book, unit, compounds));
                continue;
            }

            foreach (var (name, blocks) in RuleSubunits(unit))
            {
                result.Add(RuleEntry(range, book, name, blocks, compounds, name));
            }
        }

        return result;
    }

    private static ParsedEntry ReadCompanion(TakenRange range, string book, HeadedUnit unit, HashSet<string> compounds)
    {
        var name = Clean(string.Join(' ', unit.Header!.Lines));
        var entry = NewEntry(range, book, name, compounds, "companion", "companions", null, name);
        var stats = new List<Block>();
        List<Block>? maneuver = null;
        foreach (var b in unit.Blocks)
        {
            if (maneuver != null)
            {
                maneuver.Add(b);
            }
            else if (b.Role == "block-header")
            {
                maneuver = new List<Block> { b };
            }
            else if (b.Role == "tag")
            {
                entry.Traits.AddRange(Clean(string.Join(' ', b.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));
            }
            else if (b.Role is "body" or "rules")
            {
                stats.Add(b);
            }
            else
            {
                throw new DegreesException($"A companion's blocks hold a {b.Role} block on page {b.Page}, column {b.Column}, where they have none.");
            }
        }

        if (maneuver == null)
        {
            throw new DegreesException($"A companion (page {unit.Header.Page}) has no advanced maneuver heading.");
        }

        // The statistics: a bold label at a line's start opens a field, a line set in from the edge goes on the field before it, and the lines at the edge before the statistics (and after an access line) are the
        // companion's own sentence. The first line of a block goes on whatever the block before it left open (the layout cuts a column or a page there).
        var intro = new StringBuilder();
        var fields = new List<(string Label, StringBuilder Text)>();
        StringBuilder? open = null;
        foreach (var block in stats)
        {
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var info = block.Info?[i] ?? new LineInfo(block.Left, block.Right, string.Empty);
                var line = block.Lines[i];
                var label = SpanText.Normalize(info.Label).Trim();
                if (label.Length > 0)
                {
                    if (!CompanionLabels.Contains(label, StringComparer.Ordinal))
                    {
                        throw new DegreesException($"A companion has a bold label that is none of its statistics' (page {block.Page}, column {block.Column}).");
                    }

                    var text = label == "Str" ? line : line[Math.Min(line.Length, info.Label.Length)..];
                    open = new StringBuilder();
                    fields.Add((label, open));
                    JoinLine(open, text, compounds);
                }
                else if (i == 0 && open != null)
                {
                    JoinLine(open, line, compounds);
                }
                else if (open != null && info.Left - block.Left > Indent)
                {
                    JoinLine(open, line, compounds);
                }
                else if (fields.Count == 0 || fields[^1].Label == "Access")
                {
                    open = intro;
                    JoinLine(intro, line, compounds);
                }
                else
                {
                    throw new DegreesException($"A companion has a line at the edge among its statistics (page {block.Page}, column {block.Column}).");
                }
            }
        }

        string One(string label) => fields.Where(f => f.Label == label).ToList() is { Count: 1 } one ? Clean(one[0].Text.ToString())
            : throw new DegreesException($"A companion (page {unit.Header.Page}) has {fields.Count(f => f.Label == label)} lines labelled {label}, not one.");
        string? Optional(string label) => fields.Count(f => f.Label == label) switch
        {
            0 => null,
            1 => Clean(fields.Single(f => f.Label == label).Text.ToString()),
            _ => throw new DegreesException($"A companion (page {unit.Header.Page}) has more than one line labelled {label}."),
        };

        entry.Extracted.Add(new LabelledText("size", One("Size")));
        entry.Extracted.Add(new LabelledText("senses", One("Senses")));
        entry.Extracted.Add(new LabelledText("speed", One("Speed")));
        if (Optional("Special") is { } special)
        {
            entry.Extracted.Add(new LabelledText("special", special));
        }

        if (Optional("Access") is { } access)
        {
            entry.Extracted.Add(new LabelledText("access", access));
        }

        entry.Extracted.Add(new LabelledText("supportBenefit", One("Support Benefit")));
        var maneuverName = One("Advanced Maneuver");
        entry.Extracted.Add(new LabelledText("advancedManeuver", maneuverName));

        // The six modifiers, in the book's order; a minus sign is an en dash in the book.
        var modifiers = One("Str").Replace('–', '-').Replace('−', '-').Split(", ", StringSplitOptions.None);
        if (modifiers.Length != ModifierKeys.Length)
        {
            throw new DegreesException($"A companion (page {unit.Header.Page}) has {modifiers.Length} attribute modifiers, not six.");
        }

        for (var i = 0; i < ModifierKeys.Length; i++)
        {
            var m = CompanionModifier.Match(modifiers[i]);
            if (!m.Success || m.Groups["key"].Value != ModifierKeys[i])
            {
                throw new DegreesException($"A companion (page {unit.Header.Page}) has an attribute modifier that is not in the book's order and shape.");
            }

            entry.Raw.Add(new LabelledText(ModifierKeys[i].ToLowerInvariant(), (m.Groups["sign"].Value == "-" ? "-" : string.Empty) + m.Groups["n"].Value));
        }

        var hp = Regex.Match(One("Hit Points"), @"^\d+$", RegexOptions.CultureInvariant);
        var skill = Regex.Match(One("Skill"), @"^[A-Z][a-z]+$", RegexOptions.CultureInvariant);
        if (!hp.Success || !skill.Success)
        {
            throw new DegreesException($"A companion (page {unit.Header.Page}) has hit points that are no number or a skill that is no one word.");
        }

        entry.Raw.Add(new LabelledText("hp", hp.Value));
        entry.Raw.Add(new LabelledText("skill", skill.Value.ToLowerInvariant()));

        var attacks = new StringBuilder();
        foreach (var (label, text) in fields.Where(f => f.Label is "Melee" or "Ranged"))
        {
            var m = CompanionAttack.Match(Clean(label + " " + text));
            if (!m.Success)
            {
                throw new DegreesException($"A companion (page {unit.Header.Page}) has an attack line of a shape the parser does not know.");
            }

            var traits = m.Groups["traits"].Success ? FlowStrings(m.Groups["traits"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant())) : "[]";
            attacks.Append("\n      - { kind: ").Append(m.Groups["kind"].Value.ToLowerInvariant()).Append(", name: ").Append(YamlString(m.Groups["name"].Value.Trim()))
                .Append(", traits: ").Append(traits).Append(", damage: ").Append(YamlString(m.Groups["dice"].Value)).Append(", damageType: ").Append(YamlString(m.Groups["type"].Value));
            if (m.Groups["extra"].Value.Trim() is { Length: > 0 } extra)
            {
                attacks.Append(", extra: ").Append(YamlString(extra));
            }

            attacks.Append(" }");
        }

        if (attacks.Length == 0)
        {
            throw new DegreesException($"A companion (page {unit.Header.Page}) has no attack.");
        }

        entry.Raw.Add(new LabelledText("attacks", attacks.ToString()));

        // The entry's text: its own sentence, then the advanced maneuver as an action. The action's name must be the one the line named.
        var action = ReadFeature(maneuverName, 1, maneuver, compounds, range.Row.Chapter);
        if (!FeatureLabel(maneuver[0]).StartsWith(TitleCase(maneuverName), StringComparison.OrdinalIgnoreCase))
        {
            throw new DegreesException($"A companion (page {unit.Header.Page}) names one advanced maneuver on its line and another in its heading.");
        }

        if (Clean(intro.ToString()) is { Length: > 0 } sentence)
        {
            entry.Parts.Add(new LabelledText(string.Empty, sentence));
        }

        entry.Parts.AddRange(action.Parts);
        DashBullets(entry);
        return entry;
    }
}
