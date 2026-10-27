using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Codex.Core;

namespace Codex.Persistence;

public enum MentionKind { Actor, Place, Item }

/// <summary>An item a note can mention: a library entry (pack or homebrew), keyed "type/id".</summary>
/// <param name="PlayerVisible">False for an item the players can't see in their library (unpublished homebrew).</param>
public sealed record MentionItem(string Key, string Name, bool PlayerVisible = true);

/// <summary>Something a writer can mention, under the name they know it by.</summary>
public sealed record MentionCandidate(MentionKind Kind, string Id, string Name)
{
    public char Trigger => SessionMentions.TriggerOf(Kind);
}

/// <summary>One piece of a note as read: plain text, or a mention already named for the viewer.</summary>
public sealed record MentionSegment(string Text, MentionKind? Kind = null, string? Id = null, bool Known = true);

/// <summary>
/// Mentions in table notes: "@Aria" for a character, "#Greyharbour Docks" for a place, "$Sunblade" for an item.
/// The writer types names; the note stores ids (<c>[[actor:id]]</c>, <c>[[place:id]]</c>, <c>[[item:type/id]]</c>),
/// and every reader turns them back into the name that viewer knows. A player reads an unidentified NPC's public
/// name, and "someone", "somewhere" or "something" for what they can't see, so the DM can mention a hidden NPC in a
/// note the party reads without giving it away.
/// </summary>
public static partial class SessionMentions
{
    public const string UnknownActor = "someone";
    public const string UnknownPlace = "somewhere";
    public const string UnknownItem = "something";

    public static char TriggerOf(MentionKind kind) => kind switch { MentionKind.Actor => '@', MentionKind.Place => '#', _ => '$' };

    [GeneratedRegex(@"\[\[(actor|place|item):([^\]\s]+)\]\]")]
    internal static partial Regex TokenPattern();

    public static bool HasMentions(string text) => text.Contains("[[", StringComparison.Ordinal);

    /// <summary>Every table note in these sessions that mentions <paramref name="id"/> and the viewer may read, newest
    /// session first and in the order written within one.</summary>
    public static IReadOnlyList<(SessionDocument Session, SessionNote Note)> NotesMentioning(IEnumerable<SessionDocument> sessions, MentionKind kind, string id, bool isDm)
    {
        var token = Token(kind, id);
        return sessions
            .OrderByDescending(s => s.Date)
            .SelectMany(s => s.Notes
                .Where(n => (isDm || !n.IsSecret) && n.Text.Contains(token, StringComparison.Ordinal))
                .OrderBy(n => n.Timestamp)
                .Select(n => (Session: s, Note: n)))
            .ToList();
    }

    /// <summary>The ids of every <paramref name="kind"/> a note mentions.</summary>
    public static IReadOnlyList<string> MentionedIds(string text, MentionKind kind) =>
        TokenPattern().Matches(text)
            .Where(m => m.Groups[1].Value == KindName(kind))
            .Select(m => m.Groups[2].Value)
            .Distinct()
            .ToList();

    /// <summary>The line of <paramref name="text"/> that mentions <paramref name="token"/>, without its Markdown markers
    /// and cut to about <paramref name="max"/> characters around the mention, never through another mention.</summary>
    public static string Excerpt(string text, string token, int max = 280)
    {
        var line = text.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Contains(token, StringComparison.Ordinal)) ?? text;
        line = line.Trim().TrimStart('#', '-', '*', '•', ' ').Replace("**", string.Empty);
        if (line.Length <= max) return line;

        var at = Math.Max(0, line.IndexOf(token, StringComparison.Ordinal));
        var start = OutsideToken(line, Math.Max(0, Math.Min(at - max / 3, line.Length - max)), back: true);
        var end = OutsideToken(line, Math.Min(line.Length, start + max), back: false);
        return (start > 0 ? "…" : "") + line[start..end].Trim() + (end < line.Length ? "…" : "");
    }

    private static int OutsideToken(string text, int pos, bool back)
    {
        foreach (Match m in TokenPattern().Matches(text))
        {
            if (m.Index < pos && pos < m.Index + m.Length) return back ? m.Index : m.Index + m.Length;
        }

        return pos;
    }

    internal static string KindName(MentionKind kind) => kind switch { MentionKind.Actor => "actor", MentionKind.Place => "place", _ => "item" };

    internal static string Token(MentionKind kind, string id) => $"[[{KindName(kind)}:{id}]]";
}

/// <summary>
/// What one viewer can mention and read: the characters, places and items they can see, by the names they know.
/// Built from the full lists; the visibility rules are applied here, so one directory serves one audience.
/// </summary>
public sealed class MentionDirectory
{
    private readonly bool _isDm;
    private readonly Dictionary<string, ActorDocument> _actors;
    private readonly Dictionary<string, LocationDocument> _places;
    private readonly Dictionary<string, MentionItem> _items;

    public static MentionDirectory Empty { get; } = new(Array.Empty<ActorDocument>(), Array.Empty<LocationDocument>(), Array.Empty<MentionItem>(), isDm: false);

    /// <param name="knownActorsOnly">For a player recap: name only fully known actors, as the player event log does.
    /// Without it a player reads an unidentified NPC by their public name.</param>
    public MentionDirectory(IEnumerable<ActorDocument> actors, IEnumerable<LocationDocument> places, IEnumerable<MentionItem> items, bool isDm, bool knownActorsOnly = false)
    {
        _isDm = isDm;
        _actors = actors
            .Where(a => isDm || (knownActorsOnly ? a.Visibility == ActorVisibility.Known : a.Visibility != ActorVisibility.Hidden))
            .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        _places = places.Where(p => isDm || p.Visibility == ActorVisibility.Known).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        _items = items.Where(i => isDm || i.PlayerVisible).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        Candidates = _actors.Values.Select(a => new MentionCandidate(MentionKind.Actor, a.Id, a.NameFor(isDm)))
            .Concat(_places.Values.Select(p => new MentionCandidate(MentionKind.Place, p.Id, p.Name)))
            .Concat(_items.Values.Select(i => new MentionCandidate(MentionKind.Item, i.Key, i.Name)))
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .ToList();
        _byLength = Candidates.OrderByDescending(c => c.Name.Length).ToList();
    }

    private readonly List<MentionCandidate> _byLength;

    /// <summary>Everything this viewer may mention.</summary>
    public IReadOnlyList<MentionCandidate> Candidates { get; }

    /// <summary>Turns typed mentions ("@Mara of the Tides") into stored ids. The longest name that follows a trigger
    /// wins, so "@Mara of the Tides" isn't read as "@Mara". Text that names nothing known is left as typed.</summary>
    public string Encode(string text)
    {
        if (string.IsNullOrEmpty(text) || _byLength.Count == 0) return text;
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if ((ch == '@' || ch == '#' || ch == '$') && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var match = _byLength.FirstOrDefault(c => c.Trigger == ch && NameAt(text, i + 1, c.Name));
                if (match != null)
                {
                    result.Append(SessionMentions.Token(match.Kind, match.Id));
                    i += 1 + match.Name.Length;
                    continue;
                }
            }

            result.Append(ch);
            i++;
        }

        return result.ToString();
    }

    /// <summary>The note as this viewer reads it, in pieces, so a page can style or link the mentions.</summary>
    public IReadOnlyList<MentionSegment> Read(string text)
    {
        var segments = new List<MentionSegment>();
        var last = 0;
        foreach (Match m in SessionMentions.TokenPattern().Matches(text))
        {
            if (m.Index > last) segments.Add(new MentionSegment(text[last..m.Index]));
            var id = m.Groups[2].Value;
            segments.Add(m.Groups[1].Value switch
            {
                "actor" => _actors.TryGetValue(id, out var actor)
                    ? new MentionSegment(actor.NameFor(_isDm), MentionKind.Actor, id)
                    : new MentionSegment(SessionMentions.UnknownActor, MentionKind.Actor, id, Known: false),
                "place" => _places.TryGetValue(id, out var place)
                    ? new MentionSegment(place.Name, MentionKind.Place, id)
                    : new MentionSegment(SessionMentions.UnknownPlace, MentionKind.Place, id, Known: false),
                _ => _items.TryGetValue(id, out var item)
                    ? new MentionSegment(item.Name, MentionKind.Item, id)
                    : new MentionSegment(SessionMentions.UnknownItem, MentionKind.Item, id, Known: false)
            });
            last = m.Index + m.Length;
        }

        if (last < text.Length) segments.Add(new MentionSegment(text[last..]));
        return segments;
    }

    /// <summary>Formatted text (paragraphs, bold, lists, as <see cref="TextFormatting"/> renders them) with each mention
    /// named for this viewer, linked through <paramref name="href"/> when they can see it. The result is markup, so the
    /// names and links put back here are HTML-encoded.</summary>
    public string ToHtml(string? text, Func<MentionKind, string, string> href)
    {
        if (string.IsNullOrWhiteSpace(text) || !SessionMentions.HasMentions(text)) return TextFormatting.ToHtml(text);

        var mentions = new List<MentionSegment>();
        var marked = SessionMentions.TokenPattern().Replace(text, m =>
        {
            mentions.Add(Read(m.Value)[0]);
            return $"\u0001{mentions.Count - 1}\u0002";
        });

        return Placeholder.Replace(TextFormatting.ToHtml(marked), m => Anchor(mentions[int.Parse(m.Groups[1].Value)], href));
    }

    /// <summary>One stored mention (<paramref name="kind"/> "actor", "place" or "item", and its id) as markup for this viewer:
    /// a link when they can see it, "someone", "somewhere" or "something" when they can't.</summary>
    public string Html(string kind, string id, Func<MentionKind, string, string> href) =>
        Anchor(Read($"[[{kind}:{id}]]")[0], href);

    private static string Anchor(MentionSegment mention, Func<MentionKind, string, string> href)
    {
        var name = WebUtility.HtmlEncode(mention.Text);
        return mention.Known
            ? $"<a class=\"mention mention-{SessionMentions.KindName(mention.Kind!.Value)}\" href=\"{WebUtility.HtmlEncode(href(mention.Kind!.Value, mention.Id!))}\">{name}</a>"
            : $"<span class=\"mention mention-unknown\">{name}</span>";
    }

    private static readonly Regex Placeholder = new("\u0001(\\d+)\u0002", RegexOptions.Compiled);

    /// <summary>The note as plain text for this viewer: mentions become names, without their @, # or $.</summary>
    public string Plain(string text) =>
        SessionMentions.HasMentions(text) ? string.Concat(Read(text).Select(s => s.Text)) : text;

    /// <summary>The note back as its writer typed it, for correcting: "@Name" for each mention they can still see.
    /// One they no longer can stays a stored id, so saving the correction doesn't lose it.</summary>
    public string ForEditing(string text) =>
        !SessionMentions.HasMentions(text)
            ? text
            : string.Concat(Read(text).Select(s => s.Kind switch
            {
                null => s.Text,
                { } kind when !s.Known => SessionMentions.Token(kind, s.Id!),
                { } kind => SessionMentions.TriggerOf(kind) + s.Text
            }));

    /// <summary>Whether <paramref name="name"/> is at <paramref name="start"/>, ignoring case, and ends a word there.</summary>
    private static bool NameAt(string text, int start, string name) =>
        start + name.Length <= text.Length
        && string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (start + name.Length == text.Length || !char.IsLetterOrDigit(text[start + name.Length]));
}
