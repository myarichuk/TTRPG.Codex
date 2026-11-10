namespace Codex.Core.AI;

/// <summary>
/// A user's own style guide for drawn pictures (docs/BOOK_EXPORT.md 41.23): their words for how every picture should look, used as written in
/// every image prompt in place of the built-in style phrases (the app's art slots and the book's pictures alike). What keeps pictures alike is
/// the same model given the same style words every time, so the words are the user's and are never rewritten or filtered for names: they are
/// the user's own, sent to the user's own image model, and carry nothing of a campaign's. Only its shape is bounded.
/// </summary>
public static class ImageStyleGuide
{
    public const int MaxChars = 600;

    /// <summary>The guide as it goes into a prompt: one line, spaces collapsed, at most <see cref="MaxChars"/>; null when there is none.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= MaxChars ? one : one[..MaxChars].TrimEnd();
    }
}
