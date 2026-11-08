using System.Globalization;
using System.Text;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>What a hit in the party draft is (docs/BOOK_EXPORT.md 41.5, 41.19).</summary>
public enum BookLeakKind
{
    /// <summary>Words of a secret block: the campaign's (as the GM edition prints it) or one the GM typed into the GM draft.</summary>
    Secret,
    /// <summary>The name of a character the party edition does not have.</summary>
    HiddenName,
    /// <summary>The real name of a character the party knows by another (<see cref="BookLeak.From"/>).</summary>
    RealName,
    /// <summary>The title of a page, place, session or homebrew entry only the GM edition has, or the words of a fact only it has.</summary>
    GmOnly
}

/// <summary>Something only the GM edition has, looked for in the party draft. <paramref name="MinTokens"/>: shorter than that, it is not looked
/// for (a one-word secret would match every page that uses the word).</summary>
public sealed record BookLeakNeedle(BookLeakKind Kind, string Text, string? From, int MinTokens = 1);

/// <summary>A hit: in the party draft's chapter <paramref name="Section"/> (<see cref="BookLeakCheck.Css"/> for its stylesheet), the GM-only
/// <paramref name="Text"/> of <paramref name="Kind"/>; <paramref name="From"/> says where it comes from (the entry a secret is in, the name a
/// character goes by), when that is known.</summary>
public sealed record BookLeak(string Section, BookLeakKind Kind, string Text, string? From);

/// <summary>The hits, the first <see cref="BookLeakCheck.MaxHits"/> of <paramref name="Total"/>.</summary>
public sealed record BookLeakReport(IReadOnlyList<BookLeak> Hits, int Total);

/// <summary>
/// The party leak check (docs/BOOK_EXPORT.md 41.5, 41.19). Once the GM can type into the party draft, the code can no longer promise it holds
/// no secret: a GM can paste one in. So before the party book is exported, what only the GM edition has is looked for in it, and the GM is
/// warned with each hit. A warning, not a refusal: it is the GM's book.
/// <para>What only the GM edition has is found by comparing the two editions' sources, never by a visibility rule of its own (one would drift
/// from the repositories'): a character the party source does not have, or has under another name; a page, place, session, homebrew entry or
/// fact it does not have; and every secret block of the GM edition, as the generator prints it (mentions as names) and as the GM's own draft
/// has them. Anything the generated party draft itself says is allowed: the party edition is made from the party source alone, so a phrase in
/// it is not a leak, even when a secret repeats it.</para>
/// <para>Text is compared as words: letters and digits, lower case, accents and points dropped, everything else a gap. So the generator's
/// escapes, Markdown emphasis, line breaks and case do not hide a hit. A name or title matches as whole words; a secret by any run of
/// <see cref="Window"/> words, so half of one pasted is still found. Not found: a secret paraphrased, a name declined (Вера, Веру) or with a
/// Hebrew prefix letter.</para>
/// </summary>
public static class BookLeakCheck
{
    /// <summary>How many words in a row of a secret make a hit; a shorter secret matches whole.</summary>
    public const int Window = 6;
    /// <summary>A secret of fewer words is not looked for.</summary>
    public const int SecretMinTokens = 3;
    public const int MaxHits = 20;
    /// <summary>The section a hit in the party draft's stylesheet is reported under (its <c>content:</c> prints).</summary>
    public const string Css = "";

    /// <summary>A text as the check compares it: its words, lower case, without accents or points. Markup is gaps and a few words more
    /// (<c>{{secret</c> is "secret"), which hide nothing.</summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            var category = char.GetUnicodeCategory(c);
            // A mark (an accent, a Hebrew point, a vowel sign) belongs to its letter: dropped, the word goes on.
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (sb.Length > 0) tokens.Add(sb.ToString());
            sb.Clear();
        }

        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>Every run of 1 to <see cref="Window"/> words in <paramref name="tokens"/>, joined by spaces.</summary>
    private static HashSet<string> Grams(IReadOnlyList<string> tokens)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var sb = new StringBuilder();
            for (var k = 0; k < Window && i + k < tokens.Count; k++)
            {
                if (k > 0) sb.Append(' ');
                sb.Append(tokens[i + k]);
                grams.Add(sb.ToString());
            }
        }

        return grams;
    }

    /// <summary>What a needle is looked for as: itself when it is short, otherwise each run of <see cref="Window"/> of its words.</summary>
    private static IReadOnlyList<string> Keys(IReadOnlyList<string> tokens)
    {
        if (tokens.Count <= Window) return [string.Join(' ', tokens)];
        var keys = new List<string>(tokens.Count - Window + 1);
        for (var i = 0; i + Window <= tokens.Count; i++) keys.Add(string.Join(' ', tokens.Skip(i).Take(Window)));
        return keys;
    }

    /// <summary>
    /// What only the GM edition has: the characters, places, pages, sessions, homebrew and facts of <paramref name="gm"/> that
    /// <paramref name="party"/> lacks (or names otherwise), by name or title and by the words of their text; the GM's session summaries; and the
    /// secret blocks of <paramref name="gmDrafts"/> (the GM edition as generated now, and the GM's own draft of it).
    /// </summary>
    public static IReadOnlyList<BookLeakNeedle> Needles(BookSource gm, BookSource party, IEnumerable<string> gmDrafts)
    {
        var needles = new List<BookLeakNeedle>();
        var gmName = BookCompiler.Mentions(gm);
        var partyName = BookCompiler.Mentions(party);

        // The words of a text only the GM edition has, as the generator prints it (mentions as names), its secret blocks left to the secrets.
        void Prose(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                needles.Add(new BookLeakNeedle(BookLeakKind.GmOnly, Unescape(BookDraftWriter.Text(text, gmName, dm: false).Trim()), null, SecretMinTokens));
        }

        var partyActors = party.Actors.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var actor in gm.Actors)
        {
            var real = gmName("actor", actor.Id) ?? actor.Name;
            var shown = partyName("actor", actor.Id);
            if (shown == null) needles.Add(new BookLeakNeedle(BookLeakKind.HiddenName, real, null));
            // A stranger's id is in the party source under the name they go by; their own name is the GM's.
            else if (!Tokens(shown).SequenceEqual(Tokens(real))) needles.Add(new BookLeakNeedle(BookLeakKind.RealName, real, shown));
            // A hidden character's history, or a stranger's (the party has only how they look).
            if (!partyActors.TryGetValue(actor.Id, out var seen) || seen.Biography != actor.Biography && string.IsNullOrWhiteSpace(seen.Biography)) Prose(actor.Biography);
        }

        foreach (var region in gm.Regions)
        {
            if (partyName("place", region.Id) == null) needles.Add(new BookLeakNeedle(BookLeakKind.GmOnly, region.Name, null));
            foreach (var place in region.Locations.Where(l => partyName("place", l.Id) == null))
            {
                needles.Add(new BookLeakNeedle(BookLeakKind.GmOnly, place.Name, null));
                Prose(place.Description);
            }
        }

        static HashSet<string> Ids<T>(IEnumerable<T> items, Func<T, string> id) => items.Select(id).ToHashSet(StringComparer.Ordinal);
        var pages = Ids(party.Wiki, p => p.Id);
        foreach (var page in gm.Wiki.Where(p => !pages.Contains(p.Id)))
        {
            needles.Add(new BookLeakNeedle(BookLeakKind.GmOnly, page.Title, null));
            Prose(page.Body);
        }

        var sessions = party.Sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var session in gm.Sessions)
        {
            if (!sessions.ContainsKey(session.Id))
            {
                needles.Add(new BookLeakNeedle(BookLeakKind.GmOnly, session.Title, null));
                Prose(session.Recap);
            }

            // The GM's summary of a session is the GM's, even of one the party has.
            Prose(session.DmSummary);
        }

        var homebrew = Ids(party.Homebrew, h => h.Id);
        foreach (var entry in gm.Homebrew.Where(h => !homebrew.Contains(h.Id)))
        {
            needles.Add(new BookLeakNeedle(BookLeakKind.GmOnly, entry.Name, null));
            Prose(entry.Description);
        }

        // A fact has no title: its summary is a sentence, looked for as a secret is.
        var facts = Ids(party.Facts, f => f.Id);
        foreach (var fact in gm.Facts.Where(f => !facts.Contains(f.Id)))
        {
            Prose(fact.Summary);
            Prose(fact.Details);
        }

        foreach (var draft in gmDrafts)
        {
            needles.AddRange(Secrets(draft).Select(s => new BookLeakNeedle(BookLeakKind.Secret, s.Text, s.From, SecretMinTokens)));
        }

        // The same thing found twice (a secret in the generated GM edition and in the GM's draft) is one needle.
        return needles.Where(n => !string.IsNullOrWhiteSpace(n.Text))
            .GroupBy(n => (n.Kind, string.Join(' ', Tokens(n.Text))))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>A draft's <c>{{secret</c> blocks: each one's text (unescaped, for the GM to read), and the heading of the entry it is in.</summary>
    public static IReadOnlyList<(string Text, string? From)> Secrets(string markdown)
    {
        var found = new List<(string, string?)>();
        string? heading = null;
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('#')) heading = Unescape(t.TrimStart('#').Trim());
            if (!t.StartsWith("{{secret", StringComparison.Ordinal) || t.EndsWith("}}", StringComparison.Ordinal)) continue;
            // To the line that closes it, counting the blocks opened inside.
            var depth = 1;
            var text = new StringBuilder();
            for (i++; i < lines.Length; i++)
            {
                var inner = lines[i].Trim();
                if (inner.StartsWith("{{", StringComparison.Ordinal) && !inner.EndsWith("}}", StringComparison.Ordinal)) depth++;
                else if (inner == "}}" && --depth == 0) break;
                text.Append(lines[i]).Append('\n');
            }

            if (text.Length > 0) found.Add((Unescape(text.ToString().Trim()), heading));
        }

        return found;
    }

    private static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) i++;
            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The needles in the party draft's <paramref name="chapters"/> (section and text; the stylesheet under <see cref="Css"/>), less what
    /// <paramref name="allowed"/> (the party draft as generated now) says itself. One hit per needle and chapter, in the chapters' order.
    /// </summary>
    public static BookLeakReport Find(IReadOnlyList<BookLeakNeedle> needles, IEnumerable<string> allowed, IEnumerable<(string Section, string Text)> chapters)
    {
        var permitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in allowed) permitted.UnionWith(Grams(Tokens(text)));

        var looked = new List<(BookLeakNeedle Needle, IReadOnlyList<string> Keys)>();
        foreach (var needle in needles)
        {
            var tokens = Tokens(needle.Text);
            // A name of one or two letters would match half the book.
            if (tokens.Count < needle.MinTokens || tokens.Sum(t => t.Length) < 3) continue;
            var keys = Keys(tokens).Where(k => !permitted.Contains(k)).ToList();
            if (keys.Count > 0) looked.Add((needle, keys));
        }

        var hits = new List<BookLeak>();
        var total = 0;
        foreach (var (section, text) in chapters)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var grams = Grams(Tokens(text));
            foreach (var (needle, keys) in looked)
            {
                if (!keys.Any(grams.Contains)) continue;
                total++;
                if (hits.Count < MaxHits) hits.Add(new BookLeak(section, needle.Kind, needle.Text, needle.From));
            }
        }

        return new BookLeakReport(hits, total);
    }
}
