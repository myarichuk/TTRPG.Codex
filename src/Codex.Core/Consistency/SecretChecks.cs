namespace Codex.Core.Consistency;

/// <summary>What the secret-block checks share: the blocks of every text field, each with the text of its opening line.</summary>
internal static class SecretScan
{
    public static IEnumerable<(TextField Field, SecretBlock Block, string Opening)> Blocks(WorldSnapshot s)
    {
        foreach (var field in TextFields.Of(s))
        {
            if (!SecretText.HasMarkers(field.Text)) continue;
            // The same line splitting the parser uses, so SecretBlock.Line is the same line here.
            var lines = field.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var block in SecretText.Blocks(field.Text))
            {
                yield return (field, block, block.Line < lines.Length ? lines[block.Line] : string.Empty);
            }
        }
    }
}

/// <summary>A secret block that is never closed. It runs to the end of the text: everything after its opening line is the DM's alone.</summary>
public sealed class SecretUnclosedCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretUnclosed;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var (field, block, opening) in SecretScan.Blocks(s).Where(b => b.Block.ClosingLine == null))
        {
            yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, block.Block.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Consistency.SecretUnclosed", new[] { Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) is never closed with a line of :::, so it runs to the end of the text.",
                "Add a line with only ::: where the block should end. Do not change what the block says or who it is for.",
                field.Fix);
        }
    }
}

/// <summary>A block opened with a word the app does not know (<c>:::dm</c>, <c>:::party</c>, <c>:::players</c>, <c>:::for</c>): the block is
/// treated as DM-only, so a typo hides what the DM meant to show, never the other way round.</summary>
public sealed class SecretUnknownAudienceCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretUnknownAudience;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var (field, block, opening) in SecretScan.Blocks(s).Where(b => b.Block.Marked == SecretAudience.Dm))
        {
            // The word the block was opened with, as the parser read it: any word that is not "dm" yet came out as DM-only is one the app does not know.
            var word = block.OpenerWord;
            if (word.Length == 0 || word.Equals("dm", StringComparison.OrdinalIgnoreCase)) continue;
            yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, block.Block.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Consistency.SecretUnknownAudience", new[] { Make.Quote(word), Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) names an audience the app does not know (\":::{Make.Quote(word)}\"), so only the DM can read it.",
                "The audiences are dm, party, players and for. Change the word only if the DM's intent is clear; otherwise ask, since a wrong guess can show a secret.",
                field.Fix);
        }
    }
}

/// <summary>A <c>:::for</c> block that names no character, or one that no longer exists. A block for nobody is read by nobody.</summary>
public sealed class SecretForMissingCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretForMissing;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var actors = s.Actors.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (field, block, opening) in SecretScan.Blocks(s).Where(b => b.Block.Marked == SecretAudience.Some))
        {
            var number = block.Block.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (block.Knowers.Count == 0)
            {
                yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, $"{number}:none", "Consistency.SecretForNobody",
                    new[] { Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) names no character, so no player can read it.",
                    "Name the characters it is for as mentions on the opening line, or ask the DM who should read it.",
                    field.Fix);
                continue;
            }

            foreach (var id in block.Knowers.Where(id => !actors.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
            {
                yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, $"{number}:{id}", "Consistency.SecretForMissing",
                    new[] { id, Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) is for a character ({id}) that no longer exists.",
                    "Take that character off the opening line, or ask the DM who should read the block instead.",
                    field.Fix);
            }
        }
    }
}
