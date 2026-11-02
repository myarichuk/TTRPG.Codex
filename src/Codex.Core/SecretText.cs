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
public sealed record SecretBlock(int Block, SecretAudience Marked, bool UnderDm, string Inner, IReadOnlyList<string> Knowers, DateTime? RevealedAt, int Line)
{
    /// <summary>The 0-based line of its closing <c>:::</c>, or null when it is never closed and runs to the end of the text.</summary>
    public int? ClosingLine { get; init; }

    /// <summary>How many blocks it is inside: 0 for a block at the top level of the text.</summary>
    public int Depth { get; init; }
}

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

    private sealed record Frame(SecretAudience Marked, int Block, IReadOnlyList<string> Knowers, DateTime? RevealedAt, int Line, bool UnderDm, int Depth);

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
                open.Add(new Frame(marked, blocks.Count, knowers, StampOf(rest), i, underDm, open.Count));
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
                string.Join("\n", lines[(frame.Line + 1)..closingLine]), frame.Knowers, frame.RevealedAt, frame.Line)
            {
                ClosingLine = closingLine < lines.Length ? closingLine : null,
                Depth = frame.Depth
            };

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

    /// <summary>The blocks of the text a player owning <paramref name="ownedActorIds"/> may read only because they were
    /// revealed to one of their characters (<c>:::for</c>), as ordinary text, in order; empty when there are none. What
    /// <see cref="ForPlayers(string?, IReadOnlySet{string}?)"/> adds to <see cref="ForPlayers(string?)"/> for that player.</summary>
    public static string RevealedOnlyTo(string? text, IReadOnlySet<string>? ownedActorIds)
    {
        if (!HasMarkers(text) || ownedActorIds == null || ownedActorIds.Count == 0)
        {
            return string.Empty;
        }

        return Join(Parse(text).Where(s => s.Audience == SecretAudience.Some && CanRead(s, ownedActorIds)).Select(s => s.Text));
    }

    /// <summary>Whether a line is a block marker (<c>:::</c> or <c>:::word …</c>). Text written to go inside a block is cut of
    /// these, so it cannot close the block early or open another.</summary>
    public static bool IsMarkerLine(string line) => Marker.IsMatch(line);

    /// <summary>The players' version already beside <paramref name="block"/>: a <c>:::players</c> block outside every other
    /// block right after it, only blank lines between. Null when there is none, or the block is never closed.</summary>
    public static SecretBlock? PlayersVersionOf(string? text, SecretBlock block)
    {
        if (block.ClosingLine is not { } closing || string.IsNullOrEmpty(text)) return null;
        var next = NextFilledLine(Lines(text), closing + 1);
        return Blocks(text).FirstOrDefault(b => b.Line == next && b is { Marked: SecretAudience.Players, Depth: 0 });
    }

    private static int NextFilledLine(IReadOnlyList<string> lines, int from)
    {
        while (from < lines.Count && lines[from].Trim().Length == 0) from++;
        return from;
    }

    /// <summary>
    /// The text with <paramref name="version"/> as the players' version of block <paramref name="block"/>: a <c>:::players</c>
    /// block right after it, or, when one is there already (only blank lines between), that block's lines replaced. Marker
    /// lines in <paramref name="version"/> are dropped, so it cannot close the block early or open another. Null when there is
    /// no such block outside every other block, it is never closed, its lines are no longer <paramref name="expectedInner"/>
    /// (edited since the DM looked), or the version is empty.
    /// </summary>
    public static string? SetPlayersVersion(string? text, int block, string expectedInner, string? version)
    {
        var all = Blocks(text);
        var found = all.FirstOrDefault(b => b.Block == block);
        var body = Lines(version ?? string.Empty).Where(l => !IsMarkerLine(l)).ToList();
        while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
        while (body.Count > 0 && body[0].Trim().Length == 0) body.RemoveAt(0);
        if (found is not { Depth: 0, ClosingLine: { } closing } || found.Inner != expectedInner || body.Count == 0)
        {
            return null;
        }

        var lines = Lines(text!).ToList();
        var next = NextFilledLine(lines, closing + 1);
        var indent = Marker.Match(lines[found.Line]).Groups["indent"].Value;
        var players = new List<string> { string.Empty, indent + ":::players" };
        players.AddRange(body);
        players.Add(indent + ":::");
        if (all.FirstOrDefault(b => b.Line == next) is { Marked: SecretAudience.Players, Depth: 0, ClosingLine: { } end })
        {
            lines.RemoveRange(closing + 1, end - closing);
        }

        lines.InsertRange(closing + 1, players);
        var after = closing + 1 + players.Count;
        if (after < lines.Count && lines[after].Trim().Length > 0)
        {
            lines.Insert(after, string.Empty);
        }

        return string.Join("\n", lines);
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
    /// for single characters, which a player is never shown in a text they can edit) put back where they stood, and the
    /// revealed (<c>:::party</c>) and players' (<c>:::players</c>) blocks kept inside their opening lines, even where the player
    /// changed their words (D16). A player reads those as prose, so without this a spelling fix turned each into plain text,
    /// losing the reveal's stamp and the DM's way to hide it again. Nothing the player never saw is lost, and nothing is
    /// revealed. Where each block goes is found by lining the player's text up with the one they were given, line by line;
    /// a hidden block whose neighbours the player all rewrote goes at the end, as before. A player cannot write blocks, so
    /// any in <paramref name="edited"/> are dropped first: that also makes this idempotent, which matters because a live table
    /// applies a save twice (to its cached copy, then that copy to the stored one).
    /// </summary>
    public static string? KeepSecrets(string? stored, string? edited)
    {
        edited = HasMarkers(edited) ? ForPlayers(edited) : edited;
        if (!HasMarkers(stored))
        {
            return edited;
        }

        var (view, hidden, openers) = PlayersView(stored!);
        if (string.IsNullOrWhiteSpace(edited))
        {
            return hidden.Count == 0 ? edited : string.Join("\n\n", hidden.Select(h => h.Text));
        }

        var mine = Lines(edited);
        var match = Align(view.Select(v => v.Text).ToList(), mine);
        var claimed = new bool[mine.Length];
        foreach (var e in match.Where(e => e >= 0)) claimed[e] = true;

        // Each revealed or players' block takes the lines of the player's text that came from it, and the lines they wrote
        // in their place: unmatched lines next to it, up to a blank line or the next line that came from somewhere else.
        var spans = new List<Span>();
        var used = -1;
        foreach (var block in view.Select(v => v.Block).Where(b => b >= 0).Distinct().ToList())
        {
            var first = view.FindIndex(v => v.Block == block);
            var last = view.FindLastIndex(v => v.Block == block);
            var before = Math.Max(used, Enumerable.Range(0, first).Select(i => match[i]).Where(e => e >= 0).DefaultIfEmpty(-1).Max());
            var after = Enumerable.Range(last + 1, view.Count - last - 1).Select(i => match[i]).Where(e => e >= 0).DefaultIfEmpty(mine.Length).Min();
            var kept = Enumerable.Range(first, last - first + 1).Select(i => match[i]).Where(e => e >= 0).ToList();
            int from, to;
            if (kept.Count > 0)
            {
                (from, to) = (kept.Min(), kept.Max());
                while (from - 1 > before && !claimed[from - 1] && mine[from - 1].Trim().Length > 0) from--;
                while (to + 1 < after && !claimed[to + 1] && mine[to + 1].Trim().Length > 0) to++;
            }
            else
            {
                // Every line of it rewritten: what the player wrote between its neighbours is the block now.
                (from, to) = (before + 1, after - 1);
                while (from <= to && mine[from].Trim().Length == 0) from++;
                while (to >= from && mine[to].Trim().Length == 0) to--;
            }

            if (from <= to)
            {
                spans.Add(new Span(block, from, to));
                used = to;
            }
        }

        // A hidden block goes after the nearest line before it that the player kept (after the whole block, when that line
        // was in a revealed block), or before the nearest one after it, or at the end when the player rewrote both.
        var placed = hidden.Select(h =>
        {
            for (var i = h.At - 1; i >= 0; i--)
            {
                if (SpanOf(i) is { } span) return (h.Text, At: span.To + 1);
                if (match[i] >= 0) return (h.Text, At: match[i] + 1);
            }

            for (var i = h.At; i < view.Count; i++)
            {
                if (SpanOf(i) is { } span) return (h.Text, At: span.From);
                if (match[i] >= 0) return (h.Text, At: match[i]);
            }

            return (h.Text, At: mine.Length);
        }).ToList();

        var output = new List<string>();
        var gap = false;
        for (var p = 0; p <= mine.Length; p++)
        {
            foreach (var h in placed.Where(h => h.At == p))
            {
                Separate();
                output.Add(h.Text);
                gap = true;
            }

            if (p == mine.Length) break;
            if (spans.FirstOrDefault(s => s.From == p, new Span(-1, -1, -1)) is { Block: >= 0 } opening)
            {
                Separate();
                output.Add(openers[opening.Block]);
            }

            if (gap && mine[p].Trim().Length > 0) output.Add(string.Empty);
            gap = false;
            output.Add(mine[p]);
            if (spans.Any(s => s.To == p))
            {
                output.Add(":::");
                gap = true;
            }
        }

        return string.Join("\n", output);

        void Separate()
        {
            if (output.Count > 0 && output[^1].Trim().Length > 0) output.Add(string.Empty);
            gap = false;
        }

        Span? SpanOf(int line) => view[line].Block >= 0 && spans.FindIndex(s => s.Block == view[line].Block) is var at and >= 0 ? spans[at] : null;
    }

    private sealed record ViewLine(string Text, int Block);

    private readonly record struct Span(int Block, int From, int To);

    private sealed record HiddenBlock(string Text, int At);

    /// <summary>
    /// The text a player who owns no character is given of <paramref name="stored"/> (<see cref="ForPlayers(string?)"/>), line by
    /// line, each line with the revealed or players' block it came from (-1 for none, or one that holds other blocks and so
    /// is not put back); the hidden blocks, each whole as stored with the outermost hidden block that holds it, and how many of
    /// those lines come before it; and each such revealed block's opening line.
    /// </summary>
    private static (List<ViewLine> View, List<HiddenBlock> Hidden, Dictionary<int, string> Openers) PlayersView(string stored)
    {
        var lines = Lines(stored);
        var blocks = Blocks(stored);
        var view = new List<ViewLine>();
        var hidden = new List<HiddenBlock>();
        var openers = new Dictionary<int, string>();
        var kept = new HashSet<int>();
        foreach (var segment in Parse(stored))
        {
            if (CanRead(segment, null))
            {
                var run = segment.Text.Trim('\n');
                if (run.Trim().Length == 0) continue;
                var block = segment.Block >= 0 && blocks[segment.Block] is { Depth: 0, Marked: SecretAudience.Party or SecretAudience.Players } b && !HasMarkers(b.Inner)
                    ? segment.Block
                    : -1;
                if (block >= 0) openers[block] = lines[blocks[block].Line].Trim();
                if (view.Count > 0) view.Add(new ViewLine(string.Empty, -1));
                view.AddRange(run.Split('\n').Select(l => new ViewLine(l, block)));
                continue;
            }

            var inner = blocks[segment.Block];
            var outer = blocks.Where(o => o.Marked is SecretAudience.Dm or SecretAudience.Some && o.Line <= inner.Line && (o.ClosingLine ?? lines.Length) >= inner.Line)
                .MinBy(o => o.Depth) ?? inner;
            if (kept.Add(outer.Block))
            {
                var end = outer.ClosingLine ?? lines.Length - 1;
                var raw = string.Join("\n", lines[outer.Line..(end + 1)]);
                hidden.Add(new HiddenBlock(outer.ClosingLine == null ? raw.TrimEnd('\n') + "\n:::" : raw, view.Count));
            }
        }

        return (view, hidden, openers);
    }

    /// <summary>For each line of <paramref name="given"/>, the line of <paramref name="mine"/> it stayed as (the longest common run of
    /// lines, trailing spaces ignored), or -1 when the player changed or dropped it.</summary>
    private static int[] Align(IReadOnlyList<string> given, IReadOnlyList<string> mine)
    {
        var match = Enumerable.Repeat(-1, given.Count).ToArray();
        static bool Same(string a, string b) => string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal);

        var head = 0;
        while (head < given.Count && head < mine.Count && Same(given[head], mine[head])) { match[head] = head; head++; }
        var tail = 0;
        while (tail < given.Count - head && tail < mine.Count - head && Same(given[^(tail + 1)], mine[^(tail + 1)]))
        {
            match[given.Count - 1 - tail] = mine.Count - 1 - tail;
            tail++;
        }

        int n = given.Count - head - tail, m = mine.Count - head - tail;
        // A text this long is not a character's history; its middle is left unmatched rather than spend the memory.
        if (n == 0 || m == 0 || (long)n * m > 4_000_000) return match;

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            lcs[i, j] = Same(given[head + i], mine[head + j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (Same(given[head + i], mine[head + j])) { match[head + i] = head + j; i++; j++; }
            else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
            else j++;
        }

        return match;
    }

    /// <summary>The text of each block a player who owns no character cannot read, in order (nested ones come back as their own entries).</summary>
    public static IReadOnlyList<string> SecretBlocks(string? text) =>
        Parse(text).Where(s => !CanRead(s, null)).GroupBy(s => s.Block).Select(g => string.Join("\n", g.Select(s => s.Text))).ToList();

    /// <summary>
    /// The secret blocks of <paramref name="before"/> that <paramref name="after"/> no longer holds as they were: for each block a
    /// player who owns no character cannot read (DM blocks, blocks for single characters), the same lines must still be there
    /// for the same readers. One dropped, rewritten, moved out of its block or marked for someone else comes back as its first
    /// words. Moving a block, adding new ones and blank-line or trailing-space changes are fine. A text replaced whole (an AI
    /// edit) is checked with this before it is saved, so a secret never reaches players, or vanishes, without the DM seeing it.
    /// </summary>
    public static IReadOnlyList<string> LostSecrets(string? before, string? after)
    {
        if (!HasMarkers(before))
        {
            return Array.Empty<string>();
        }

        var left = Signatures(after).GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Count());
        var lost = new List<string>();
        foreach (var (key, text) in Signatures(before))
        {
            if (left.TryGetValue(key, out var count) && count > 0)
            {
                left[key] = count - 1;
                continue;
            }

            lost.Add(FirstWords(text));
        }

        return lost;

        static IEnumerable<(string Key, string Text)> Signatures(string? text) =>
            Parse(text).Where(s => !CanRead(s, null)).GroupBy(s => s.Block).Select(g =>
            {
                var first = g.First();
                var lines = string.Join("\n", g.SelectMany(s => s.Text.Split('\n')).Select(l => l.TrimEnd()).Where(l => l.Length > 0));
                var readers = first.Audience == SecretAudience.Some ? string.Join(" ", first.Knowers.Order(StringComparer.Ordinal)) : string.Empty;
                return (Key: $"{first.Audience}|{readers}|{lines}", Text: lines);
            }).Where(s => s.Text.Length > 0);

        static string FirstWords(string text)
        {
            var words = text.Split((char[]?)null, 7, StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 6 ? string.Join(" ", words[..6]) + "…" : string.Join(" ", words);
        }
    }

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

    // "\r\n" (Windows) and a lone "\r" (old Mac, some pastes) both end a line, so a marker after either is still a marker.
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

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
