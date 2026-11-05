using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>
/// The generator of the editable book (docs/BOOK_EXPORT.md 41.2): a masked source, compiled as the book is compiled today, written out as
/// Homebrewery-flavoured Markdown for <c>marked-hbfm</c>. It writes only a fixed set of blocks, and every theme styles each one. Where
/// Homebrewery has a block for the thing, it is Homebrewery's (so CSS written for Homebrewery styles it here too); the rest are ours:
/// <list type="bullet">
/// <item>Homebrewery's <c>{{frontCover}}</c> on the title page, with our <c>{{cover-art</c> for its picture, and Homebrewery's <c>{{toc,wide</c>;</item>
/// <item><c>{{chapter,#ch-ID</c> around a chapter's <c># title</c> (the <c>.chapter</c> block of Homebrewery's chapter templates), and our
/// <c>{{chapter-art</c> under it;</item>
/// <item><c>{{entry,#e-SLOT}}</c> before an entry's heading (a Cast member, a place, a page), and <c>{{portrait</c> or <c>{{place</c> under it;</item>
/// <item><c>{{secret</c> for the GM's secrets, in the GM draft only.</item>
/// </list>
/// <para>An entry's id is its picture slot's key (<see cref="BookSlots.KeyFor"/>), the hash of what the slot is about as the edition sees it, never
/// a raw id: a stranger in the party draft is <c>mask:</c>, so the actor behind the mask is not in the party's text even as an id.</para>
/// <para>Pictures go in only as <c>art:</c> references to the edition's own slots, resolved when the page is drawn. The campaign's free text is
/// the app's own Markdown: its mentions become the names this edition may be given (or nothing), its wiki links their words, its pictures
/// their captions, its secret blocks boxes (GM) or nothing (party), and a <c>&lt;</c> or <c>&amp;</c> in it is escaped, because Homebrewery's
/// renderer reads HTML and this text was never written as HTML.</para>
/// </summary>
public static class BookDraftWriter
{
    /// <summary>Changes whenever what the generator writes changes: every chapter then looks moved on, and the ones nobody edited simply follow.</summary>
    public const string Version = "book-draft/v2";

    public const string Front = "front";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Flags = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    // The renderer's own grammar for a stored mention, and for one that never closes (BookMarkdownRenderer).
    private static readonly Regex Mention = new(@"\[\[\s*(?<kind>actor|place|item)\s*:(?<rest>.{0,400}?)\]\]", Flags | RegexOptions.IgnoreCase | RegexOptions.Singleline, Timeout);
    private static readonly Regex OpenMention = new(@"\[\[\s*(?:actor|place|item)\s*:[^\n]*", Flags | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex PageLink = new(@"\[\[(?<inner>[^\[\]\n]{0,300}?)\]\]", Flags, Timeout);
    private static readonly Regex Picture = new(@"!\[(?<alt>[^\]\n]{0,300})\]\([^)\n]{0,500}\)", Flags, Timeout);

    /// <summary>Every chapter of the edition's draft as the campaign makes it now: the front pages first, then the book's chapters.</summary>
    public static IReadOnlyList<BookDraftGenerated> Generate(BookSource source, string language, BookArtDocument? stored)
    {
        var compiled = BookComposer.Compose(source, language, out var entries);
        var slots = BookSlots.For(source, compiled, entries);
        // The edition's own book pictures (the party takes only the party's), and the entries' own pictures as this source flags them.
        var pictured = BookSlots.Choose(slots, stored).Select(c => c.Slot.Key).ToHashSet(StringComparer.Ordinal);
        var mention = BookCompiler.Mentions(source);
        var dm = source.Audience == BookAudience.Dm;

        var chapters = new List<BookDraftGenerated>();
        var front = FrontPages(compiled, slots, pictured, source.Campaign.HasCover);
        chapters.Add(Made(Front, compiled.Labels.Contents, front));
        foreach (var section in compiled.Sections)
        {
            var header = slots.First(s => s.Kind == BookSlots.Header && s.Section == section.Id);
            var mine = entries.Where(e => e.Section == section.Id).ToDictionary(e => e.Line);
            chapters.Add(Made(section.Id, header.Label, Chapter(section, header, mine, pictured, mention, dm)));
        }

        return chapters;
    }

    private static BookDraftGenerated Made(string section, string title, string markdown) => new(section, title, markdown, KeyOf(markdown));

    /// <summary>What a chapter's generated text is keyed by: the generator's version and the text itself. The generator reads nothing but the
    /// masked source and the edition's pictures, so the same campaign makes the same key, and any change to what it would write moves it.</summary>
    public static string KeyOf(string markdown) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Version + "\n" + markdown)), 0, 16).ToLowerInvariant();

    private static string FrontPages(BookDocument compiled, IReadOnlyList<BookSlot> slots, IReadOnlySet<string> pictured, bool hasCover)
    {
        var sb = new StringBuilder();
        // Homebrewery's cover marker: an empty block that tells a theme this page is the front cover. A theme lays the page out from it, and
        // the renderer prints no page number on it.
        sb.Append("{{frontCover}}\n\n# ").Append(BookCompiler.Escape(compiled.Title)).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(compiled.Tagline)) sb.Append('*').Append(BookCompiler.Escape(compiled.Tagline)).Append("*\n\n");
        var cover = slots.First(s => s.Kind == BookSlots.Cover);
        if (hasCover || pictured.Contains(cover.Key)) Art(sb, "cover-art", cover.Label, cover.Key);
        sb.Append("\\page\n\n{{toc,wide\n# ").Append(BookCompiler.Escape(compiled.Labels.Contents)).Append("\n\n");
        foreach (var section in compiled.Sections)
        {
            var title = slots.First(s => s.Kind == BookSlots.Header && s.Section == section.Id).Label;
            sb.Append("- [").Append(BookCompiler.Escape(title)).Append("](#ch-").Append(section.Id).Append(")\n");
        }

        return sb.Append("}}\n").ToString();
    }

    private static string Chapter(BookSection section, BookSlot header, IReadOnlyDictionary<int, BookEntryPosition> entries, IReadOnlySet<string> pictured,
        Func<string, string, string?> mention, bool dm)
    {
        var lines = section.Markdown.ReplaceLineEndings("\n").Split('\n');
        var sb = new StringBuilder();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            var converted = Text(text.ToString(), mention, dm).Trim('\n');
            if (converted.Length > 0) sb.Append(converted).Append("\n\n");
            text.Clear();
        }

        // The compiler writes the title first; the chapter's id and its art come with it.
        sb.Append("{{chapter,#ch-").Append(section.Id).Append('\n').Append(lines[0]).Append("\n}}\n\n");
        if (pictured.Contains(header.Key)) Art(sb, "chapter-art", header.Label, header.Key);
        for (var i = 1; i < lines.Length; i++)
        {
            // An entry's heading is found by the line the compiler recorded when it wrote it, never by its text (BookComposer.PlaceArt).
            if (entries.TryGetValue(i, out var entry) && lines[i] == entry.Heading)
            {
                Flush();
                sb.Append("{{entry,#e-").Append(entry.SlotKey).Append("}}\n").Append(lines[i]).Append("\n\n");
                if (entry.HasArt || pictured.Contains(entry.SlotKey))
                {
                    Art(sb, entry.Placement == BookArtPlacement.Portrait ? "portrait" : "place", entry.Alt, entry.SlotKey);
                }

                continue;
            }

            text.Append(lines[i]).Append('\n');
        }

        Flush();
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static void Art(StringBuilder sb, string block, string alt, string key) =>
        sb.Append("{{").Append(block).Append("\n![").Append(BookCompiler.Escape(alt)).Append("](art:").Append(key).Append(")\n}}\n\n");

    /// <summary>The campaign's own Markdown as draft Markdown (see the class). Public for its tests.</summary>
    public static string Text(string markdown, Func<string, string, string?> mention, bool dm)
    {
        var sb = new StringBuilder();
        // The secret grammar first: a ":::for" line names its characters with mentions, and those must be read as markers, not names.
        foreach (var segment in SecretText.Parse(markdown.ReplaceLineEndings("\n")))
        {
            if (segment.Audience is SecretAudience.Dm or SecretAudience.Some)
            {
                // The party source has none; if one were there, it is the party's book that would show it, so it is dropped all the same.
                if (!dm) continue;
                var inner = Plain(segment.Text, mention).Trim('\n');
                if (inner.Length > 0) sb.Append("\n{{secret\n").Append(inner).Append("\n}}\n\n");
                continue;
            }

            sb.Append(Plain(segment.Text, mention));
            if (!segment.Text.EndsWith('\n')) sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string Plain(string text, Func<string, string, string?> mention)
    {
        try
        {
            text = Mention.Replace(text, m =>
            {
                var rest = m.Groups["rest"].Value;
                var pipe = rest.IndexOf('|');
                var id = (pipe < 0 ? rest : rest[..pipe]).Trim();
                // The name this edition may be given, never the mention's own |label: that is where a hidden character's name would ride along.
                var name = id.Length == 0 || id.Any(char.IsWhiteSpace) ? null : mention(m.Groups["kind"].Value.ToLowerInvariant(), id);
                return string.IsNullOrWhiteSpace(name) ? string.Empty : BookCompiler.Escape(name);
            });
            text = OpenMention.Replace(text, string.Empty);
            text = PageLink.Replace(text, m =>
            {
                var inner = m.Groups["inner"].Value;
                var pipe = inner.IndexOf('|');
                var shown = (pipe < 0 ? inner : inner[(pipe + 1)..]).Trim();
                return BookCompiler.Escape(shown.Length > 0 ? shown : inner.Trim());
            });
            text = Picture.Replace(text, m => BookCompiler.Escape(m.Groups["alt"].Value));
        }
        catch (RegexMatchTimeoutException)
        {
            // Hostile input costs its own text, never a leak: it is dropped, and the GM sees an empty paragraph to write into.
            return string.Empty;
        }

        var lines = text.Split('\n');
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            // A marker after a quote mark is not one the grammar knows; it would print as text, so it goes (as the renderer does).
            if (SecretText.IsMarkerLine(t.TrimStart(' ', '>')))
            {
                lines[i] = string.Empty;
                continue;
            }

            if (fenced) continue;
            lines[i] = EscapeHtml(lines[i]);
            // A page or column break is the GM's to write, not the campaign's: "\page {display:none}" in a biography would break or restyle a
            // page. Escaped, it prints as the words.
            if (t.StartsWith(@"\page", StringComparison.Ordinal) || t.StartsWith(@"\column", StringComparison.Ordinal))
            {
                var lead = lines[i].Length - lines[i].TrimStart().Length;
                lines[i] = lines[i][..lead] + "\\" + lines[i][lead..];
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary><c>&lt;</c> and <c>&amp;</c> backslash-escaped, except where they already are and inside code spans (where Markdown shows them as
    /// they are anyway).</summary>
    private static string EscapeHtml(string line)
    {
        if (line.IndexOfAny(['<', '&']) < 0) return line;
        var sb = new StringBuilder(line.Length + 8);
        var code = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                sb.Append(c).Append(line[++i]);
                continue;
            }

            // A backtick opens a code span only when one closes it: a lone one is a literal, and what follows it is read as HTML.
            if (c == '`' && (code || line.IndexOf('`', i + 1) > i)) code = !code;
            if (!code && c is '<' or '&') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }
}
