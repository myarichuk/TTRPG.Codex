using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>Where a name in the campaign's text may point in the draft: the key of the entry a mention (<paramref name="Entry"/>: kind and
/// id) or a page link (<paramref name="Page"/>: the title as written) is about, or null when this edition's book has no such entry.
/// <paramref name="Self"/> is the entry the text is in, which it never links to.</summary>
public sealed record BookDraftLinks(Func<string, string, string?> Entry, Func<string, string?> Page, string? Self = null);


/// <summary>
/// The generator of the editable book (docs/BOOK_EXPORT.md 41.2): a masked source, compiled as the book is compiled today, written out as
/// Homebrewery-flavoured Markdown for <c>marked-hbfm</c>. It writes only a fixed set of blocks, and every theme styles each one. Where
/// Homebrewery has a block for the thing, it is Homebrewery's (so CSS written for Homebrewery styles it here too); the rest are ours:
/// <list type="bullet">
/// <item>Homebrewery's <c>{{frontCover}}</c> on the title page, with our <c>{{cover-art</c> for its picture, and Homebrewery's <c>{{toc,wide</c>;</item>
/// <item><c>{{chapter,#ch-ID</c> around a chapter's <c># title</c> (the <c>.chapter</c> block of Homebrewery's chapter templates), and our
/// <c>{{chapter-art</c> under it;</item>
/// <item><c>{{entry,#e-SLOT}}</c> before an entry's heading (a Cast member, a place, a page, a session), <c>{{portrait</c> or <c>{{place</c> under
/// it, and <c>{{spot</c> at its end when its spot holds a picture;</item>
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
    public const string Version = "book-draft/v4";

    public const string Front = "front";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Flags = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    // The renderer's own grammar for a stored mention, and for one that never closes (BookMarkdownRenderer).
    private static readonly Regex Mention = new(@"\[\[\s*(?<kind>actor|place|item)\s*:(?<rest>.{0,400}?)\]\]", Flags | RegexOptions.IgnoreCase | RegexOptions.Singleline, Timeout);
    private static readonly Regex OpenMention = new(@"\[\[\s*(?:actor|place|item)\s*:[^\n]*", Flags | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex PageLink = new(@"\[\[(?<inner>[^\[\]\n]{0,300}?)\]\]", Flags, Timeout);
    // An ATX heading as Markdown reads one: up to three spaces, one to six "#", then a space or the line's end.
    private static readonly Regex Heading = new(@"^( {0,3})(#{1,6})(?=[ \t]|$)", Flags, Timeout);
    private static readonly Regex Picture = new(@"!\[(?<alt>[^\]\n]{0,300})\]\([^)\n]{0,500}\)", Flags, Timeout);

    /// <summary>Every chapter of the edition's draft as the campaign makes it now: the front pages first, then the book's chapters.</summary>
    /// <para><paramref name="system"/>: the campaign's game system, for the stat blocks and tables of its homebrew (<see cref="BookStatBlocks"/>);
    /// without it, an entry is its text only.</para>
    /// <para><paramref name="own"/>: the chapters the GM added to this draft (docs/BOOK_EXPORT.md 51), in the draft's order, which the contents
    /// list where they stand. They are the one thing here that is not the campaign's: a title the GM typed.</para>
    public static IReadOnlyList<BookDraftGenerated> Generate(BookSource source, string language, BookArtDocument? stored,
        Codex.Plugin.Abstractions.SystemDefinition? system = null, IReadOnlyList<BookDraftOwn>? own = null)
    {
        var compiled = BookComposer.Compose(source, language, out var entries, system);
        var slots = BookSlots.For(source, compiled, entries);
        // The edition's own book pictures (the party takes only the party's), and the entries' own pictures as this source flags them.
        var pictured = BookSlots.Choose(slots, stored).Select(c => c.Slot.Key).ToHashSet(StringComparer.Ordinal);
        var mention = BookCompiler.Mentions(source);
        var dm = source.Audience == BookAudience.Dm;
        var homebrew = source.Homebrew.ToDictionary(h => h.Id, StringComparer.Ordinal);
        string? Numbers(BookEntryPosition entry) =>
            entry.Kind == "homebrew" && homebrew.TryGetValue(entry.Owner, out var h) ? BookStatBlocks.For(h, system, language, entry.SlotKey, mention, dm) : null;

        var chapters = new List<BookDraftGenerated>();
        var links = Links(source, entries);
        var front = FrontPages(compiled, slots, pictured, source.Campaign.HasCover, entries, own ?? []);
        chapters.Add(Made(Front, compiled.Labels.Contents, front));
        foreach (var section in compiled.Sections)
        {
            var header = slots.First(s => s.Kind == BookSlots.Header && s.Section == section.Id);
            var mine = entries.Where(e => e.Section == section.Id).ToDictionary(e => e.Line);
            chapters.Add(Made(section.Id, header.Label, Chapter(section, header, mine, pictured, mention, dm, Numbers, links)));
        }

        return chapters;
    }

    private static BookDraftGenerated Made(string section, string title, string markdown) => new(section, title, markdown, KeyOf(markdown));

    /// <summary>What a chapter's generated text is keyed by: the generator's version and the text itself. The generator reads nothing but the
    /// masked source, the edition's pictures and the system's description of its homebrew types, so the same campaign makes the same key, and any change to what it would write moves it.</summary>
    public static string KeyOf(string markdown) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Version + "\n" + markdown)), 0, 16).ToLowerInvariant();

    /// <summary>
    /// Where a mention or a page link may point (docs/BOOK_EXPORT.md 51): the entries this edition's book has, by what they are about. Built from
    /// the entries the compiler wrote for this source and from its own pages, so the party edition can name no entry, and no page, its source
    /// lacks: a hidden character has no entry here, and a stranger's is the mask's.
    /// </summary>
    public static BookDraftLinks Links(BookSource source, IReadOnlyList<BookEntryPosition> entries)
    {
        var keys = new Dictionary<(string Kind, string Id), string>();
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case "portrait": keys.TryAdd(("actor", e.Owner), e.SlotKey); break;
                case "location": keys.TryAdd(("place", e.Sub), e.SlotKey); break;
                case BookEntryPosition.Region: keys.TryAdd(("place", e.Owner), e.SlotKey); break;
                case "wiki": keys.TryAdd(("wiki", e.Owner), e.SlotKey); break;
            }
        }

        var pages = source.Wiki;
        return new BookDraftLinks(
            (kind, id) => keys.GetValueOrDefault((kind, id)),
            title => WikiLinks.TargetOfTitle(title, pages) is { } page ? keys.GetValueOrDefault(("wiki", page.Id)) : null);
    }

    private static string FrontPages(BookDocument compiled, IReadOnlyList<BookSlot> slots, IReadOnlySet<string> pictured, bool hasCover,
        IReadOnlyList<BookEntryPosition> entries, IReadOnlyList<BookDraftOwn> own)
    {
        var sb = new StringBuilder();
        // Homebrewery's cover marker: an empty block that tells a theme this page is the front cover. A theme lays the page out from it, and
        // the renderer prints no page number on it.
        sb.Append("{{frontCover}}\n\n# ").Append(BookCompiler.Escape(compiled.Title)).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(compiled.Tagline)) sb.Append('*').Append(BookCompiler.Escape(compiled.Tagline)).Append("*\n\n");
        var cover = slots.First(s => s.Kind == BookSlots.Cover);
        if (hasCover || pictured.Contains(cover.Key)) Art(sb, "cover-art", cover.Label, cover.Key);
        sb.Append("\\page\n\n{{toc,wide\n# ").Append(BookCompiler.Escape(compiled.Labels.Contents)).Append("\n\n");

        // Two levels: each chapter, then what is in it (its entries; in the gazetteer its regions, whose places are not listed). A chapter of
        // the GM's own stands where the draft has it: after the chapter it follows there.
        var order = compiled.Sections.Select(s => (s.Id, Title: slots.First(h => h.Kind == BookSlots.Header && h.Section == s.Id).Label, Own: false)).ToList();
        foreach (var chapter in own)
        {
            if (order.Any(o => o.Id == chapter.Section)) continue;
            var after = chapter.After is "" or Front ? -1 : order.FindIndex(o => o.Id == chapter.After);
            order.Insert(after < 0 && chapter.After is not ("" or Front) ? order.Count : after + 1, (chapter.Section, chapter.Title, true));
        }

        foreach (var (id, title, mine) in order)
        {
            sb.Append("- [").Append(BookCompiler.Escape(title)).Append("](#ch-").Append(id).Append(")\n");
            if (mine) continue;
            foreach (var entry in entries.Where(e => e.Section == id && e.InContents))
            {
                sb.Append("  - [").Append(BookCompiler.Escape(entry.Alt)).Append("](#e-").Append(entry.SlotKey).Append(")\n");
            }
        }

        return sb.Append("}}\n").ToString();
    }

    private static string Chapter(BookSection section, BookSlot header, IReadOnlyDictionary<int, BookEntryPosition> entries, IReadOnlySet<string> pictured,
        Func<string, string, string?> mention, bool dm, Func<BookEntryPosition, string?> numbers, BookDraftLinks links)
    {
        var lines = section.Markdown.ReplaceLineEndings("\n").Split('\n');
        var sb = new StringBuilder();
        var text = new StringBuilder();
        BookEntryPosition? open = null;
        void Flush()
        {
            if (text.Length == 0) return;
            // A heading the campaign's text has of its own goes under the heading it is written under: the entry's, or the chapter's title
            // before the first entry. Text never links to the entry it is in.
            var converted = Text(text.ToString(), mention, dm, open?.Level ?? 1, links with { Self = open?.SlotKey }).Trim('\n');
            if (converted.Length > 0) sb.Append(converted).Append("\n\n");
            text.Clear();
        }

        // The compiler writes the title first; the chapter's id and its art come with it.
        sb.Append("{{chapter,#ch-").Append(section.Id).Append('\n').Append(lines[0]).Append("\n}}\n\n");
        if (pictured.Contains(header.Key)) Art(sb, "chapter-art", header.Label, header.Key);
        // An entry's numbers (its stat block or table, 46) follow its text; its spot (28.3, step 7) closes it, before the next entry's marker.
        void Close()
        {
            if (open != null && numbers(open) is { } block) sb.Append(block).Append("\n\n");
            if (open is { IsGroup: false } && pictured.Contains(BookSlots.SpotKey(open))) Art(sb, "spot", open.Alt, BookSlots.SpotKey(open));
            open = null;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            // An entry's heading is found by the line the compiler recorded when it wrote it, never by its text: a body line that reads like a heading, in an earlier entry or a code fence, cannot take the entry.
            if (entries.TryGetValue(i, out var entry) && lines[i] == entry.Heading)
            {
                Flush();
                Close();
                open = entry;
                sb.Append("{{entry,#e-").Append(entry.SlotKey).Append("}}\n").Append(lines[i]).Append("\n\n");
                if (!entry.IsGroup && (entry.HasArt || pictured.Contains(entry.SlotKey)))
                {
                    Art(sb, ArtBlockName(entry), entry.Alt, entry.SlotKey);
                }

                continue;
            }

            text.Append(lines[i]).Append('\n');
        }

        Flush();
        Close();
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static void Art(StringBuilder sb, string block, string alt, string key) => sb.Append(ArtBlock(block, alt, key)).Append("\n\n");

    /// <summary>A picture block as the generator writes it, without the blank line after it: the editor puts the same text under an entry's
    /// heading when a picture is drawn or uploaded from a chip (docs/BOOK_EXPORT.md 53), so the chapter then reads as the generator's own.</summary>
    public static string ArtBlock(string block, string alt, string key) => "{{" + block + "\n![" + BookCompiler.Escape(alt) + "](art:" + key + ")\n}}";

    /// <summary>The block an entry's own picture goes in: a portrait beside a character or a homebrew entry, a wide picture for a place.</summary>
    public static string ArtBlockName(BookEntryPosition entry) => entry.Placement == BookArtPlacement.Portrait ? "portrait" : "place";

    /// <summary>The campaign's own Markdown as draft Markdown (see the class). Public for its tests.</summary>
    /// <para><paramref name="level"/>: the level of the heading the text is written under (docs/BOOK_EXPORT.md 51). A heading in the text of
    /// level n is written as level <c>min(6, level + n)</c>, so a "# Rumours" in a biography sits under the character and never reads as a
    /// chapter; 0 leaves headings as they are. Only ATX headings ("# ..."), never inside a code fence, and no line is added or removed.</para>
    /// <para><paramref name="links"/>: where a mention or a page link may point; without it every name is plain words.</para>
    public static string Text(string markdown, Func<string, string, string?> mention, bool dm, int level = 0, BookDraftLinks? links = null)
    {
        var sb = new StringBuilder();
        // A code fence may run from one part of the text into the next (around a secret block): what is inside it stays as written.
        var fenced = false;
        // The secret grammar first: a ":::for" line names its characters with mentions, and those must be read as markers, not names.
        foreach (var segment in SecretText.Parse(markdown.ReplaceLineEndings("\n")))
        {
            if (segment.Audience is SecretAudience.Dm or SecretAudience.Some)
            {
                // The party source has none; if one were there, it is the party's book that would show it, so it is dropped all the same.
                if (!dm) continue;
                var inner = Plain(segment.Text, mention, level, links, ref fenced).Trim('\n');
                if (inner.Length > 0) sb.Append("\n{{secret\n").Append(inner).Append("\n}}\n\n");
                continue;
            }

            sb.Append(Plain(segment.Text, mention, level, links, ref fenced));
            if (!segment.Text.EndsWith('\n')) sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string Plain(string text, Func<string, string, string?> mention, int level, BookDraftLinks? links, ref bool fenced)
    {
        // A name that has an entry in this edition's book points at it; any other is plain words, as before.
        string Linked(string words, string? key) =>
            key == null || key == links?.Self ? BookCompiler.Escape(words) : "[" + BookCompiler.Escape(words) + "](#e-" + key + ")";
        try
        {
            text = Mention.Replace(text, m =>
            {
                var rest = m.Groups["rest"].Value;
                var pipe = rest.IndexOf('|');
                var id = (pipe < 0 ? rest : rest[..pipe]).Trim();
                // The name this edition may be given, never the mention's own |label: that is where a hidden character's name would ride along.
                var kind = m.Groups["kind"].Value.ToLowerInvariant();
                var name = id.Length == 0 || id.Any(char.IsWhiteSpace) ? null : mention(kind, id);
                // The key is looked up only for a name this edition gives: a mention it resolves to nothing has no link either.
                return string.IsNullOrWhiteSpace(name) ? string.Empty : Linked(name, links?.Entry(kind, id));
            });
            text = OpenMention.Replace(text, string.Empty);
            text = PageLink.Replace(text, m =>
            {
                var inner = m.Groups["inner"].Value;
                var pipe = inner.IndexOf('|');
                var shown = (pipe < 0 ? inner : inner[(pipe + 1)..]).Trim();
                var target = (pipe < 0 ? inner : inner[..pipe]).Trim();
                return Linked(shown.Length > 0 ? shown : inner.Trim(), target.Length == 0 ? null : links?.Page(target));
            });
            text = Picture.Replace(text, m => BookCompiler.Escape(m.Groups["alt"].Value));
        }
        catch (RegexMatchTimeoutException)
        {
            // Hostile input costs its own text, never a leak: it is dropped, and the GM sees an empty paragraph to write into.
            return string.Empty;
        }

        var lines = text.Split('\n');
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
            if (level > 0 && Heading.Match(lines[i]) is { Success: true } h)
            {
                lines[i] = h.Groups[1].Value + new string('#', Math.Min(6, level + h.Groups[2].Length)) + lines[i][h.Length..];
            }

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
