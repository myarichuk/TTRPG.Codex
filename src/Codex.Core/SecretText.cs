using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core;

/// <summary>Who may read a block of a text.</summary>
public enum SecretAudience
{
    /// <summary>Text outside any block: everyone who may read the text.</summary>
    Everyone,

    /// <summary><c>:::dm</c>: the DM only.</summary>
    Dm,

    /// <summary><c>:::party</c>: a DM block the DM has revealed. Players read it as ordinary text; the DM sees it marked.</summary>
    Party,

    /// <summary><c>:::players</c>: what the players are told, written beside the truth. Players read it as ordinary text.</summary>
    Players,

    /// <summary><c>:::for @Aria @Bram</c>: revealed to those characters only. A player reads it when they own one of them.</summary>
    Some
}

/// <summary>A run of lines of a text and who may read it. <paramref name="Block"/> is the 0-based number of the innermost block
/// it is in (counting every opening line in the text), or -1 for text outside any block.</summary>
public sealed record SecretSegment(SecretAudience Audience, string Text, int Block)
{
    /// <summary>For <see cref="SecretAudience.Some"/>: the ids of the characters who may read it (when blocks for different
    /// characters nest, only those named by all of them).</summary>
    public IReadOnlyList<string> Knowers { get; init; } = Array.Empty<string>();

    /// <summary>When the innermost block was revealed, if its opening line says so (<c>:::party 2026-10-29T14:05:00Z</c>).</summary>
    public DateTime? RevealedAt { get; init; }
}

/// <summary>One block of a text, as its opening line marks it.</summary>
/// <param name="Block">Its number: the 0-based count of opening lines before it.</param>
/// <param name="Marked">What its own opening line says, whatever blocks it is in.</param>
/// <param name="UnderDm">It is inside a DM block, so it stays the DM's whatever it says, and revealing it would show nothing.</param>
/// <param name="Inner">Its lines between the opening and the closing line (nested markers included): what the DM saw when they
/// asked to change it, so a text edited since is not changed blind.</param>
/// <param name="Knowers">For <see cref="SecretAudience.Some"/>: the ids of the characters it names.</param>
/// <param name="RevealedAt">When it was revealed, if its opening line says so.</param>
/// <param name="Line">The 0-based line of its opening marker.</param>
public sealed record SecretBlock(int Block, SecretAudience Marked, bool UnderDm, string Inner, IReadOnlyList<string> Knowers, DateTime? RevealedAt, int Line);

/// <summary>
/// The secret blocks of a DM's text (a wiki page, a place's description, a character's history):
/// <code>
/// :::dm
/// The lighthouse keeper runs the smugglers.
/// :::
/// </code>
/// A line <c>:::dm</c>, <c>:::party</c>, <c>:::players</c> or <c>:::for @Aria</c> opens a block and a line <c>:::</c> closes it.
/// Blocks nest: a block inside a DM block stays the DM's whatever it says, and a block inside one for some characters is for
/// them too. Everything here fails closed, because the cost of a mistake is a spoiler: a block that is never closed runs to
/// the end of the text, an opening word this version does not know (a typo) is the DM's, a <c>:::for</c> that names no
/// character the campaign knows is no one's, and markers count inside code fences too. Players are given
/// <see cref="ForPlayers(string?, IReadOnlySet{string}?)"/>, which the repositories apply before a text leaves the database for one.
/// </summary>
public static class SecretText
{
    // ":::" alone closes; ":::word …" opens. Leading spaces are allowed so an indented marker is not mistaken for text.
    private static readonly Regex Marker = new(@"^(?<indent>\s*):::(?:\s*(?<word>[^\s:]\S*)(?<rest>.*))?\s*$", RegexOptions.Compiled);

    // A character named on a ":::for" line, as the editor stores a mention of one.
    private static readonly Regex ActorToken = new(@"\[\[actor:(?<id>[^\]\s|]+)\]\]", RegexOptions.Compiled);

    // When a block was revealed, as a reveal stamps it on the opening line.
    private static readonly Regex Stamp = new(@"(?<!\S)(?<at>\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2})?Z?)?)(?!\S)", RegexOptions.Compiled);

    /// <summary>Whether the text holds any block marker at all (cheap check before parsing).</summary>
    public static bool HasMarkers(string? text) => !string.IsNullOrEmpty(text) && text.Contains(":::", StringComparison.Ordinal);

    /// <summary>Whether a player who owns no character would read less of the text than the DM.</summary>
    public static bool HasSecrets(string? text) => Parse(text).Any(s => !CanRead(s, null));

    /// <summary>Whether a player owning the characters <paramref name="ownedActorIds"/> (none when null) may read a segment.</summary>
    public static bool CanRead(SecretSegment segment, IReadOnlySet<string>? ownedActorIds) => segment.Audience switch
    {
        SecretAudience.Dm => false,
        SecretAudience.Some => ownedActorIds != null && segment.Knowers.Any(ownedActorIds.Contains),
        _ => true
    };

    /// <summary>The text cut into runs of lines by who may read them, markers left out, in order. Adjacent lines with the
    /// same audience and block stay one segment.</summary>
    public static IReadOnlyList<SecretSegment> Parse(string? text) => Walk(text).Segments;

    /// <summary>Every block of the text, in the order their opening lines come.</summary>
    public static IReadOnlyList<SecretBlock> Blocks(string? text) => Walk(text).Blocks;

    private sealed record Frame(SecretAudience Marked, int Block, IReadOnlyList<string> Knowers, DateTime? RevealedAt, int Line, bool UnderDm);

    private static (List<SecretSegment> Segments, List<SecretBlock> Blocks) Walk(string? text)
    {
        var segments = new List<SecretSegment>();
        var blocks = new List<SecretBlock>();
        if (string.IsNullOrEmpty(text))
        {
            return (segments, blocks);
        }

        if (!HasMarkers(text))
        {
            segments.Add(new SecretSegment(SecretAudience.Everyone, text, -1));
            return (segments, blocks);
        }

        var lines = Lines(text);
        // The open blocks, innermost last.
        var open = new List<Frame>();
        var current = new StringBuilder();
        var key = Key(open);

        for (var i = 0; i < lines.Length; i++)
        {
            var m = Marker.Match(lines[i]);
            if (!m.Success)
            {
                current.Append(lines[i]).Append('\n');
                continue;
            }

            Flush();
            if (m.Groups["word"].Success)
            {
                var marked = AudienceOf(m.Groups["word"].Value);
                var rest = m.Groups["rest"].Value;
                var knowers = marked == SecretAudience.Some ? ActorToken.Matches(rest).Select(t => t.Groups["id"].Value).Distinct().ToList() : new List<string>();
                var underDm = open.Any(o => o.Marked == SecretAudience.Dm);
                open.Add(new Frame(marked, blocks.Count, knowers, StampOf(rest), i, underDm));
                blocks.Add(null!); // filled in when it closes, so Inner can hold its lines
            }
            else if (open.Count > 0)
            {
                Close(open[^1], i);
                open.RemoveAt(open.Count - 1);
            }

            // A stray ":::" with nothing open is dropped, like any marker line.
            key = Key(open);
        }

        Flush();
        // A block that is never closed runs to the end of the text.
        for (var j = open.Count - 1; j >= 0; j--)
        {
            Close(open[j], lines.Length);
        }

        return (segments, blocks);

        void Close(Frame frame, int closingLine) =>
            blocks[frame.Block] = new SecretBlock(frame.Block, frame.Marked, frame.UnderDm,
                string.Join("\n", lines[(frame.Line + 1)..closingLine]), frame.Knowers, frame.RevealedAt, frame.Line);

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var chunk = current.ToString().TrimEnd('\n');
            current.Clear();
            if (segments.Count > 0 && segments[^1].Audience == key.Audience && segments[^1].Block == key.Block)
            {
                segments[^1] = segments[^1] with { Text = segments[^1].Text + "\n" + chunk };
            }
            else
            {
                segments.Add(key with { Text = chunk });
            }
        }
    }

    // Who may read the lines at this point, from the blocks open there.
    private static SecretSegment Key(List<Frame> open)
    {
        if (open.Count == 0)
        {
            return new SecretSegment(SecretAudience.Everyone, string.Empty, -1);
        }

        var inner = open[^1];
        // Inside a DM block everything stays the DM's: a ":::party" pasted into a secret does not reveal it.
        if (open.Any(o => o.Marked == SecretAudience.Dm))
        {
            return new SecretSegment(SecretAudience.Dm, string.Empty, inner.Block);
        }

        var some = open.Where(o => o.Marked == SecretAudience.Some).ToList();
        if (some.Count > 0)
        {
            // Nested blocks for different characters: only those named by every one of them.
            IEnumerable<string> knowers = some[0].Knowers;
            foreach (var other in some.Skip(1))
            {
                knowers = knowers.Intersect(other.Knowers);
            }

            return new SecretSegment(SecretAudience.Some, string.Empty, inner.Block) { Knowers = knowers.ToList(), RevealedAt = inner.RevealedAt };
        }

        return new SecretSegment(inner.Marked, string.Empty, inner.Block) { RevealedAt = inner.RevealedAt };
    }

    /// <summary>The text a player who owns no character reads: DM blocks and blocks for single characters gone, revealed and
    /// player blocks as ordinary text, no marker lines. A text without markers comes back as it is.</summary>
    public static string ForPlayers(string? text) => ForPlayers(text, null);

    /// <summary>The text a player owning the characters <paramref name="ownedActorIds"/> reads: what <see cref="ForPlayers(string?)"/>
    /// leaves, and the blocks revealed to one of their characters as ordinary text too.</summary>
    public static string ForPlayers(string? text, IReadOnlySet<string>? ownedActorIds)
    {
        if (!HasMarkers(text))
        {
            return text ?? string.Empty;
        }

        // Blank lines between the kept runs, so a paragraph before a removed block and one after it do not run together.
        return Join(Parse(text).Where(s => CanRead(s, ownedActorIds)).Select(s => s.Text));
    }

    /// <summary>
    /// What a player is handed of a text whose revealed parts they may be shown as new: <see cref="ForPlayers(string?, IReadOnlySet{string}?)"/>,
    /// except that a revealed block that says when it was revealed keeps its markers, as <c>:::party</c> and the time. Nothing
    /// in it is more than the player may read; whatever renders it for them takes the markers out (or marks the block new).
    /// </summary>
    public static string ForPlayersMarked(string? text, IReadOnlySet<string>? ownedActorIds)
    {
        if (!HasMarkers(text))
        {
            return text ?? string.Empty;
        }

        return Join(Parse(text).Where(s => CanRead(s, ownedActorIds)).Select(s =>
            s.RevealedAt is { } at && s.Audience is SecretAudience.Party or SecretAudience.Some
                ? $":::party {StampText(at)}\n{s.Text.Trim('\n')}\n:::"
                : s.Text));
    }

    /// <summary>The whole text with the marker lines left out: for a one-line summary the DM reads.</summary>
    public static string WithoutMarkers(string? text)
    {
        if (!HasMarkers(text))
        {
            return text ?? string.Empty;
        }

        return Join(Parse(text).Select(s => s.Text));
    }

    private static string Join(IEnumerable<string> runs) =>
        string.Join("\n\n", runs.Select(t => t.Trim('\n')).Where(t => t.Trim().Length > 0));

    /// <summary>
    /// What to store when a player saves a text the DM may have put secrets in (a player character's history): the player's
    /// <paramref name="edited"/> text, with the blocks of <paramref name="stored"/> a player cannot read (DM blocks, and blocks
    /// for single characters, which a player is never shown in a text they can edit) kept at its end. Nothing the player never
    /// saw is lost, and nothing is revealed. A player cannot write such blocks, so any in <paramref name="edited"/> are dropped
    /// first: that also makes this idempotent, which matters because a live table applies a save twice (to its cached copy,
    /// then that copy to the stored one).
    /// </summary>
    public static string? KeepSecrets(string? stored, string? edited)
    {
        var kept = string.Join("\n\n", Parse(stored).Where(s => !CanRead(s, null)).GroupBy(s => s.Block).Select(g =>
        {
            var first = g.First();
            var opener = first.Audience == SecretAudience.Some ? ":::for" + string.Concat(first.Knowers.Select(id => $" [[actor:{id}]]")) : ":::dm";
            return $"{opener}\n{string.Join("\n", g.Select(s => s.Text))}\n:::";
        }));
        edited = HasMarkers(edited) ? ForPlayers(edited) : edited;
        if (kept.Length == 0)
        {
            return edited;
        }

        return string.IsNullOrWhiteSpace(edited) ? kept : edited.TrimEnd() + "\n\n" + kept;
    }

    /// <summary>The text of each block a player who owns no character cannot read, in order (nested ones come back as their own entries).</summary>
    public static IReadOnlyList<string> SecretBlocks(string? text) =>
        Parse(text).Where(s => !CanRead(s, null)).GroupBy(s => s.Block).Select(g => string.Join("\n", g.Select(s => s.Text))).ToList();

    /// <summary>
    /// The text with block <paramref name="block"/> marked for <paramref name="to"/>: <see cref="SecretAudience.Party"/> reveals it to
    /// everyone, <see cref="SecretAudience.Some"/> to the characters <paramref name="knowers"/>, <see cref="SecretAudience.Dm"/> hides
    /// it again. A reveal stamps the opening line with <paramref name="at"/>, so players can be shown what is new. Only the opening
    /// line changes. Null when there is no such block, when it is inside a DM block (revealing it would show nothing), or when its
    /// lines are no longer <paramref name="expectedInner"/>: the text was edited since the DM looked, and they should look again.
    /// </summary>
    public static string? SetAudience(string? text, int block, string expectedInner, SecretAudience to, IReadOnlyList<string>? knowers, DateTime at)
    {
        var found = Blocks(text).FirstOrDefault(b => b.Block == block);
        if (found == null || found.UnderDm || found.Inner != expectedInner || to == SecretAudience.Everyone)
        {
            return null;
        }

        var ids = (knowers ?? Array.Empty<string>()).Where(id => id.Length > 0).Distinct().ToList();
        if (to == SecretAudience.Some && ids.Count == 0)
        {
            return null;
        }

        var lines = Lines(text!);
        var indent = Marker.Match(lines[found.Line]).Groups["indent"].Value;
        lines[found.Line] = indent + to switch
        {
            SecretAudience.Party => $":::party {StampText(at)}",
            SecretAudience.Some => ":::for" + string.Concat(ids.Select(id => $" [[actor:{id}]]")) + $" {StampText(at)}",
            SecretAudience.Players => ":::players",
            _ => ":::dm"
        };
        return string.Join("\n", lines);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static string StampText(DateTime at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime? StampOf(string rest)
    {
        var m = Stamp.Match(rest);
        return m.Success && DateTime.TryParse(m.Groups["at"].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    private static SecretAudience AudienceOf(string word) => word.ToLowerInvariant() switch
    {
        "party" or "revealed" => SecretAudience.Party,
        "players" or "player" => SecretAudience.Players,
        "for" => SecretAudience.Some,
        _ => SecretAudience.Dm
    };
}
