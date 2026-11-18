using System.Text;

namespace Codex.ContentImport.Degrees;

/// <summary>One ability of a list as the blocks hold it: the bold name that opens it (its colon dropped) and its text.</summary>
public sealed record AbilityScan(string Name, string Text);

public static partial class EntriesStage
{
    /// <summary>
    /// Reads the text of a section that lists abilities: a line that opens with a bold name and a colon starts one, and every other line goes on the one before it. The lines before the first ability are the section's own
    /// sentences. A block of another role than text fails the run.
    /// </summary>
    internal static (string Intro, List<AbilityScan> Abilities) ReadAbilityList(HeadedUnit unit, HashSet<string> compounds)
    {
        var intro = new StringBuilder();
        var found = new List<(string Name, StringBuilder Text)>();
        StringBuilder open = intro;
        foreach (var block in unit.Blocks)
        {
            if (block.Role is not ("rules" or "body"))
            {
                throw new DegreesException($"A list of abilities holds a {block.Role} block on page {block.Page}, column {block.Column}.");
            }

            for (var i = 0; i < block.Lines.Count; i++)
            {
                var info = block.Info?[i] ?? new LineInfo(block.Left, block.Right, string.Empty);
                var line = block.Lines[i];
                if (info.Label.EndsWith(':') && line.StartsWith(info.Label, StringComparison.Ordinal))
                {
                    open = new StringBuilder();
                    found.Add((SpanText.Normalize(info.Label).TrimEnd(':', ' '), open));
                    JoinLine(open, line[info.Label.Length..], compounds);
                }
                else
                {
                    JoinLine(open, line, compounds);
                }
            }
        }

        return (Clean(intro.ToString()), found.Select(f => new AbilityScan(f.Name, Clean(f.Text.ToString()))).ToList());
    }

    /// <summary>
    /// The <c>familiars</c> parser: a section the row's <c>abilityKinds</c> names is a list of abilities, each an <c>familiar-ability</c> entry of that kind (its own sentences become a rule); every other section is a rule,
    /// and so is the text before the first heading (named by the path's last title).
    /// </summary>
    private static List<ParsedEntry> ParseFamiliars(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
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

            var title = HeadingName(unit.Header);
            if (!range.Row.AbilityKinds.TryGetValue(title, out var kind))
            {
                foreach (var (name, blocks) in RuleSubunits(unit))
                {
                    result.Add(RuleEntry(range, book, name, blocks, compounds, name));
                }

                continue;
            }

            var (intro, abilities) = ReadAbilityList(unit, compounds);
            if (abilities.Count == 0)
            {
                throw new DegreesException($"A section of abilities (page {unit.Header.Page}) holds none.");
            }

            if (intro.Length > 0)
            {
                var rule = NewEntry(range, book, range.Row.EntryNames.TryGetValue(title, out var renamed) ? renamed : title, compounds, "rule", "rules", range.Row.Category, title);
                rule.Parts.Add(new LabelledText(string.Empty, intro));
                result.Add(rule);
            }

            foreach (var a in abilities)
            {
                var entry = NewEntry(range, book, a.Name, compounds, "familiar-ability", "familiar-abilities", null, title);
                entry.Parts.Add(new LabelledText(string.Empty, a.Text));
                entry.Raw.Add(new LabelledText("kind", kind));
                result.Add(entry);
            }
        }

        return result;
    }
}
