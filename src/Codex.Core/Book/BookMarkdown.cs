namespace Codex.Core.Book;

/// <summary>A heading the renderer met, for a table of contents and for the PDF outline.</summary>
public sealed record BookHeading(int Level, string Text, string Id);

/// <summary>
/// What a text rendered to. <paramref name="Warnings"/> are codes for things the renderer refused on purpose (a link, an
/// image URL, a style it does not allow, a mention it may not name), so the UI can say what it dropped instead of silently
/// losing it.
/// </summary>
public sealed record BookMarkdownResult(string Html, IReadOnlyList<BookHeading> Headings, IReadOnlyList<string> Warnings);

/// <summary>What the renderer may know beyond the text. Every default is the closed one: with no options a text renders without
/// a mention's name, without an image and without a secret.</summary>
public sealed class BookMarkdownOptions
{
    /// <summary>
    /// The name a reader of this book may be given for a stored mention (<c>[[actor:id]]</c>, <c>[[place:id]]</c>,
    /// <c>[[item:type/id]]</c>), or null when they may not be told. A mention's own <c>|label</c> is never used instead: the label
    /// is text a model or a DM typed, and it is exactly where a hidden character's name would ride along. With no resolver a
    /// mention renders as nothing.
    /// </summary>
    public Func<string, string, string?>? Mention { get; init; }

    /// <summary>
    /// A <c>data:image/…</c> URI for an image slot or attachment (<c>![caption](slot:id)</c>, <c>![caption](asset:id)</c>), or null
    /// while the slot is empty, which renders a placeholder. Any other image target is dropped: no URL survives rendering.
    /// </summary>
    public Func<string, string?>? Image { get; init; }

    /// <summary>
    /// Ready-made, trusted HTML for <c>{{block:N}}</c> on a line of its own: a stat block or a table that the code built from
    /// stored data, so that no number in it ever passed through a model's prose. This is the only way raw HTML enters a page.
    /// </summary>
    public IReadOnlyDictionary<int, string>? Blocks { get; init; }

    /// <summary>The DM edition: secret blocks (<c>:::dm</c>, <c>:::for</c>) are boxed instead of read out. Off, the renderer drops
    /// everything a player who owns no character may not read, whatever the source already did.</summary>
    public bool BoxSecrets { get; init; }

    /// <summary>Heading ids already used elsewhere in the book. Share one set across the calls that make a book, so two sections
    /// never produce the same anchor (a table of contents and the PDF outline look headings up by id).</summary>
    public ISet<string>? HeadingIds { get; init; }

    /// <summary>The longest text rendered; the rest is cut and a warning says so.</summary>
    public int MaxLength { get; init; } = 500_000;
}

/// <summary>
/// The Markdown subset of a campaign book, rendered to HTML by our own code (no JavaScript in the print page, one parser for the
/// preview and the PDF, and nothing to keep in step with an upstream's version). It reads Homebrewery-compatible markup so a
/// downloaded <c>.md</c> opens elsewhere, and it does not claim to be Homebrewery.
/// <para>Supported: headings, paragraphs (two trailing spaces or a backslash end a line), bold, italic, strike-through, code,
/// bulleted, numbered and nested lists, quotes, pipe tables, fenced code, rules, <c>\page</c> and <c>\column</c>, <c>{{classes,
/// #id, property:value}}</c> as a block (opening line alone, closing line <c>}}</c>) and as an inline span, <c>{{block:N}}</c>,
/// image slots and mentions.</para>
/// <para>Everything is escaped by default. There is no raw HTML, no link (its text stays), no URL anywhere, only a short list of
/// CSS properties with values that cannot load anything, and every output tag is written by this class.</para>
/// </summary>
public static class BookMarkdown
{
    public static BookMarkdownResult Render(string? text, BookMarkdownOptions? options = null)
    {
        var renderer = new BookMarkdownRenderer(options ?? new BookMarkdownOptions());
        string html;
        try
        {
            html = renderer.Document(text ?? string.Empty);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // Hostile input must cost one section, not the book: the text is shown as it is, escaped, with a warning.
            html = renderer.Fallback(text ?? string.Empty);
        }

        return new BookMarkdownResult(html, renderer.Headings, renderer.Warnings);
    }
}
