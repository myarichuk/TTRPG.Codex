using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>One choice of the style vocabulary: an id the book stores, and the words it adds to an image prompt.</summary>
public sealed record BookArtChoice(string Id, string Phrase);

/// <summary>A book's look for drawn pictures, chosen once per book from <see cref="BookArtStyles"/>: medium, palette, line weight and light.</summary>
public sealed record BookArtStyle(string Medium, string Palette, string Line, string Light)
{
    public static readonly BookArtStyle Default = new("painterly", "muted", "medium", "natural");
}

/// <summary>
/// The code-owned style vocabulary for drawn book pictures (docs/BOOK_EXPORT.md 28.3, item 2). Every word of an image prompt other than the
/// picture's short visual description comes from here, so a prompt cannot carry a name, an artist, or anything the GM did not choose from a
/// list. The phrases are English because image models are prompted in English, whatever the book's language.
/// </summary>
public static class BookArtStyles
{
    public static readonly IReadOnlyList<BookArtChoice> Media =
    [
        // The house style the app's own art slots have always used (AiPrompts.Image).
        new("painterly", "Painterly fantasy illustration"),
        new("oil", "Painterly oil painting with visible brushwork"),
        new("watercolour", "Loose watercolour wash illustration with soft bleeding edges"),
        new("ink", "Ink linework illustration with crosshatched shading"),
        new("woodcut", "Woodcut print with bold carved lines"),
        new("engraving", "Fine copperplate engraving with dense hatching"),
        new("sketch", "Graphite and sepia sketch on parchment")
    ];

    public static readonly IReadOnlyList<BookArtChoice> Palettes =
    [
        new("muted", "rich muted colours"),
        new("earth", "warm earth tones of ochre, umber and moss"),
        new("sepia", "sepia and cream tones only"),
        new("mono", "black ink on white, no colour"),
        new("jewel", "deep jewel tones"),
        new("cold", "cold greys and pale blues")
    ];

    public static readonly IReadOnlyList<BookArtChoice> Lines =
    [
        new("fine", "fine delicate lines"),
        new("medium", "clear confident lines"),
        new("bold", "heavy bold outlines")
    ];

    public static readonly IReadOnlyList<BookArtChoice> Lights =
    [
        new("natural", "dramatic natural light"),
        new("candle", "warm candlelight and deep shadow"),
        new("overcast", "soft overcast light"),
        new("moon", "cold moonlight"),
        new("dusk", "low golden dusk light")
    ];

    /// <summary>The mood a picture is drawn in. The art director picks one of these ids; it never writes the words itself.</summary>
    public static readonly IReadOnlyList<BookArtChoice> Moods =
    [
        new("ominous", "an ominous, foreboding mood"),
        new("serene", "a calm, serene mood"),
        new("tense", "a tense, watchful mood"),
        new("triumphant", "a triumphant mood"),
        new("melancholy", "a melancholy mood"),
        new("wondrous", "a sense of wonder"),
        new("eerie", "an eerie, uncanny mood"),
        new("warm", "a warm, welcoming mood"),
        new("desolate", "a bleak, desolate mood"),
        new("festive", "a lively, festive mood")
    ];

    /// <summary>The frame each slot kind is drawn for. Placement is the code's, never the model's.</summary>
    public static readonly IReadOnlyDictionary<string, string> Framing = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cover"] = "Book cover illustration, one strong central subject, tall composition with calm space at the top",
        ["header"] = "Very wide panoramic scene for the head of a chapter",
        ["portrait"] = "Head-and-shoulders portrait, face clearly visible",
        ["homebrew"] = "Full-figure study of a single subject on a plain ground",
        ["location"] = "Wide establishing view of a place",
        ["wiki"] = "Wide establishing view",
        ["spot"] = "Small vignette of a single object or moment on a plain ground",
        ["scene"] = "A single dramatic moment, wide composition"
    };

    /// <summary>Always last in a prompt: what a printed page must not have, and what image models add unasked.</summary>
    public const string Negative = "No text, no lettering, no letters or numbers, no signature, no watermark, no frame, no border, not a photograph, no photorealism, no modern objects, no real people.";

    public static BookArtChoice? Find(IReadOnlyList<BookArtChoice> list, string? id) => list.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>The style with any unknown id replaced by the default's, so a stored style from an older vocabulary still draws.</summary>
    public static BookArtStyle Normalize(BookArtStyle? style) => new(
        Find(Media, style?.Medium)?.Id ?? BookArtStyle.Default.Medium,
        Find(Palettes, style?.Palette)?.Id ?? BookArtStyle.Default.Palette,
        Find(Lines, style?.Line)?.Id ?? BookArtStyle.Default.Line,
        Find(Lights, style?.Light)?.Id ?? BookArtStyle.Default.Light);
}

/// <summary>An assembled image prompt, or why the visual description cannot be used.</summary>
public sealed record BookImagePromptResult(string? Prompt, IReadOnlyList<string> Problems);

/// <summary>
/// Builds an image prompt by code: framing for the slot, the visual description, the book's style, the fixed negative. The visual description
/// (written by the art director later, or typed by the GM) is the only free text, and it is checked: no capitalised word other than at the start
/// of a sentence (an invented proper noun means nothing to an image model and a real one points it at a person or a work), no name of the
/// campaign's that the caller lists, no artist or "in the style of", no link, a length cap. A visual that fails is not repaired: it is refused
/// with the reasons, and nothing is drawn.
/// </summary>
public static partial class BookImagePrompt
{
    public const int MaxVisualChars = 400;

    /// <summary>Real artists, studios and the phrases that steer a model toward imitating someone. Matched as whole words, case-insensitively.</summary>
    public static readonly IReadOnlyList<string> DeniedWords =
    [
        "rutkowski", "frazetta", "mucha", "rackham", "doré", "dore", "dürer", "durer", "giger", "moebius", "mignola", "elmore", "brom", "caldwell",
        "parkinson", "easley", "lockwood", "whelan", "vallejo", "bisley", "tolkien", "ghibli", "miyazaki", "disney", "pixar", "marvel", "artstation",
        "deviantart", "greg", "alphonse", "beksinski", "beksiński", "hokusai", "rembrandt", "vermeer", "caravaggio", "turner", "bierstadt", "waterhouse",
        "trending", "unreal", "octane", "midjourney", "dall-e", "dalle"
    ];

    public static readonly IReadOnlyList<string> DeniedPhrases =
    [
        "in the style of", "style of", "in the manner of", "after the style", "inspired by", "by the artist", "artwork by", "painted by", "drawn by", "art by"
    ];

    /// <param name="mood">An id of <see cref="BookArtStyles.Moods"/>; anything else adds no mood.</param>
    public static BookImagePromptResult Assemble(string slotKind, string? visual, BookArtStyle? style, string? mood = null, IEnumerable<string>? names = null)
    {
        var problems = Check(visual, names);
        if (problems.Count > 0) return new BookImagePromptResult(null, problems);
        var framing = BookArtStyles.Framing.TryGetValue(slotKind, out var f) ? f : BookArtStyles.Framing["scene"];
        var s = BookArtStyles.Normalize(style);
        var description = Clean(visual!);
        var sb = new StringBuilder();
        sb.Append(framing).Append(": ").Append(description);
        if (BookArtStyles.Find(BookArtStyles.Moods, mood) is { } feeling) sb.Append(", ").Append(feeling.Phrase);
        sb.Append(". ");
        sb.Append(BookArtStyles.Find(BookArtStyles.Media, s.Medium)!.Phrase).Append(", ")
          .Append(BookArtStyles.Find(BookArtStyles.Palettes, s.Palette)!.Phrase).Append(", ")
          .Append(BookArtStyles.Find(BookArtStyles.Lines, s.Line)!.Phrase).Append(", ")
          .Append(BookArtStyles.Find(BookArtStyles.Lights, s.Light)!.Phrase).Append(". ");
        sb.Append(BookArtStyles.Negative);
        return new BookImagePromptResult(sb.ToString(), []);
    }

    /// <summary>Why a visual description cannot go into a prompt; empty when it can.</summary>
    public static IReadOnlyList<string> Check(string? visual, IEnumerable<string>? names = null)
    {
        var problems = new List<string>();
        var text = visual?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            problems.Add("empty");
            return problems;
        }

        if (text.Length > MaxVisualChars) problems.Add("too-long");
        if (Link().IsMatch(text)) problems.Add("link");
        var lower = text.ToLowerInvariant();
        foreach (var phrase in DeniedPhrases.Where(p => lower.Contains(p, StringComparison.Ordinal))) problems.Add("phrase:" + phrase);
        var words = Word().Matches(text).Select(m => m.Value).ToList();
        foreach (var word in words.Select(w => w.ToLowerInvariant()).Intersect(DeniedWords, StringComparer.Ordinal)) problems.Add("artist:" + word);
        foreach (var capital in Capitalised(text)) problems.Add("capital:" + capital);
        foreach (var name in (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                problems.Add("name:" + name);
            }
        }

        return problems;
    }

    /// <summary>Words with a capital letter that do not begin a sentence. A word that is all capitals is one too (an acronym is a name).</summary>
    private static IEnumerable<string> Capitalised(string text)
    {
        var start = true;
        foreach (Match m in Token().Matches(text))
        {
            var token = m.Value;
            if (token.Length == 1 && ".!?;:".Contains(token[0]))
            {
                start = true;
                continue;
            }

            if (!char.IsLetter(token[0])) continue;
            var hasUpper = token.Any(char.IsUpper);
            var acronym = token.Length > 1 && token.Where(char.IsLetter).All(char.IsUpper);
            if (hasUpper && (!start || acronym || token.Skip(1).Any(char.IsUpper))) yield return token;
            start = false;
        }
    }

    private static string Clean(string visual)
    {
        var flat = Regex.Replace(visual.Trim(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return flat.TrimEnd('.', ' ');
    }

    [GeneratedRegex(@"https?://|www\.|[a-z0-9-]+\.(com|net|org|io)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Link();

    [GeneratedRegex(@"[\p{L}][\p{L}\p{Mn}'’-]*", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Word();

    [GeneratedRegex(@"[\p{L}][\p{L}\p{Mn}'’-]*|[.!?;:]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Token();
}
