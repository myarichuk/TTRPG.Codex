namespace Codex.Core.Consistency;

/// <summary>What the secret-block checks share: the blocks of every text field, each with the text of its opening line and a key that names it.</summary>
internal static class SecretScan
{
    /// <summary>The longest slug of a block's first line a key holds.</summary>
    private const int KeyLength = 40;

    /// <summary>
    /// The blocks of every text field that <paramref name="reported"/> keeps, each with a key for its issue id: the word it was opened with and the slug of its
    /// first line of content (bounded; <c>empty</c> for a block with none), <c>dm:the-keeper-is-a-smuggler</c>; for an unclosed block the first line alone,
    /// as the word is what another issue of the same block (an unknown audience) fixes. The key is made only from what a fix of the
    /// block's own issue, or of another issue of the same block, does not touch: the rest of the opening line (the characters a <c>:::for</c> block names, the reveal stamp) is left out, so taking a
    /// character off the line or closing the block leaves the key alone, and changing an unknown word changes the key because it fixes the issue. Never the
    /// block's position, and never another block's: a block added, fixed or removed elsewhere in the text moves no other key. <c>.2</c>, <c>.3</c> tell
    /// apart only blocks that <paramref name="reported"/> keeps and that open with the same word and the same first line (the one limit of the scheme: two such
    /// blocks cannot be told apart, so fixing the first gives its key to the second).
    /// </summary>
    /// <param name="withWord">False for a check whose issue a change of the opener word does not fix (an unclosed block): its key is the first line alone,
    /// so fixing the block's unknown word (another issue) leaves it as it was.</param>
    public static IEnumerable<(TextField Field, SecretBlock Block, string Opening, string Key)> Blocks(WorldSnapshot s, Func<SecretBlock, bool> reported, bool withWord = true)
    {
        // The blocks of every field are read once per snapshot (the three secret checks share them); which are reported and how they are numbered is each check's.
        foreach (var (field, blocks) in s.Derived.SecretBlocks)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var read in blocks)
            {
                if (!reported(read.Block)) continue;
                var identity = withWord ? $"{read.Word}:{read.Content}" : read.Content;
                seen[identity] = seen.GetValueOrDefault(identity) + 1;
                yield return (field, read.Block, read.Opening, seen[identity] == 1 ? identity : $"{identity}.{seen[identity]}");
            }
        }
    }

    /// <summary>One block as the keys are made from it: its opening line, the slug of the word it was opened with and of its first line of content.</summary>
    internal sealed record ReadBlock(SecretBlock Block, string Opening, string Word, string Content);

    /// <summary>The blocks of every text field that has a marker, in field order, each read as <see cref="ReadBlock"/>.</summary>
    internal static List<(TextField Field, IReadOnlyList<ReadBlock> Blocks)> Read(WorldSnapshot s)
    {
        var all = new List<(TextField, IReadOnlyList<ReadBlock>)>();
        foreach (var field in TextFields.Of(s))
        {
            if (!SecretText.HasMarkers(field.Text)) continue;
            // The same line splitting the parser uses, so SecretBlock.Line is the same line here.
            var lines = field.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            all.Add((field, SecretText.Blocks(field.Text)
                .Select(block => new ReadBlock(block, block.Line < lines.Length ? lines[block.Line] : string.Empty, Slug(block.OpenerWord, "block"), Slug(FirstContentLine(block), "empty")))
                .ToList()));
        }

        return all;
    }

    /// <summary>The block's first line that has something in it and is not itself a marker line (a nested block's opener or closer).</summary>
    private static string FirstContentLine(SecretBlock block) =>
        block.Inner.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !SecretText.IsMarkerLine(l)) ?? string.Empty;

    private static string Slug(string text, string none)
    {
        if (!text.Any(char.IsLetterOrDigit)) return none;
        var slug = WikiText.Slugify(text);
        return slug.Length > KeyLength ? slug[..KeyLength].TrimEnd('-') : slug;
    }
}

/// <summary>What the checks of one snapshot derive from the same texts, each worked out once, on first use, and then only read (<see cref="WorldSnapshot.Derived"/>).</summary>
internal sealed class SnapshotDerived(WorldSnapshot snapshot)
{
    private readonly Lazy<List<(TextField Field, IReadOnlyList<SecretScan.ReadBlock> Blocks)>> secretBlocks = new(() => SecretScan.Read(snapshot), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The secret blocks of every text field that has a marker (<see cref="SecretScan.Read"/>).</summary>
    public IReadOnlyList<(TextField Field, IReadOnlyList<SecretScan.ReadBlock> Blocks)> SecretBlocks => secretBlocks.Value;
}

/// <summary>A secret block that is never closed. It runs to the end of the text: everything after its opening line is the DM's alone.</summary>
public sealed class SecretUnclosedCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretUnclosed;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var (field, block, opening, key) in SecretScan.Blocks(s, b => b.ClosingLine == null, withWord: false))
        {
            yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, key,
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
        foreach (var (field, block, opening, key) in SecretScan.Blocks(s, IsUnknownAudience))
        {
            var word = block.OpenerWord;
            yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, key,
                "Consistency.SecretUnknownAudience", new[] { Make.Quote(word), Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) names an audience the app does not know (\":::{Make.Quote(word)}\"), so only the DM can read it.",
                "The audiences are dm, party, players and for. Change the word only if the DM's intent is clear; otherwise ask, since a wrong guess can show a secret.",
                field.Fix);
        }
    }

    /// <summary>The word the block was opened with, as the parser read it: any word that is not "dm" yet came out as DM-only is one the app does not know.
    /// (Filtered before the keys are made, so a block of a known audience never takes a <c>.2</c> from another.)</summary>
    private static bool IsUnknownAudience(SecretBlock block) =>
        block.Marked == SecretAudience.Dm && block.OpenerWord.Length > 0 && !block.OpenerWord.Equals("dm", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A <c>:::for</c> block that names no character, or one that no longer exists. A block for nobody is read by nobody.</summary>
public sealed class SecretForMissingCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretForMissing;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var actors = s.Actors.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        // Only the blocks with an issue (no character, or one who is gone), so a block for characters who exist never numbers another's key.
        foreach (var (field, block, opening, key) in SecretScan.Blocks(s, b => b.Marked == SecretAudience.Some && (b.Knowers.Count == 0 || b.Knowers.Any(id => !actors.Contains(id)))))
        {
            if (block.Knowers.Count == 0)
            {
                yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, $"{key}:none", "Consistency.SecretForNobody",
                    new[] { Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) names no character, so no player can read it.",
                    "Name the characters it is for as mentions on the opening line, or ask the DM who should read it.",
                    field.Fix);
                continue;
            }

            foreach (var id in block.Knowers.Where(id => !actors.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
            {
                yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, $"{key}:{id}", "Consistency.SecretForMissing",
                    new[] { id, Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) is for a character ({id}) that no longer exists.",
                    "Take that character off the opening line, or ask the DM who should read the block instead.",
                    field.Fix);
            }
        }
    }
}
