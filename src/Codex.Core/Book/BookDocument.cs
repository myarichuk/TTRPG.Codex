namespace Codex.Core.Book;

/// <summary>One chapter of a book: Markdown in the book's dialect. Its heading is part of the Markdown.</summary>
public sealed record BookSection(string Id, string Markdown);

/// <summary>The words a book prints that are not the campaign's own. Defaults are English; the app passes the reader's language.</summary>
public sealed record BookLabels(
    string Contents = "Contents",
    string Overview = "Overview",
    string Cast = "Cast",
    string Gazetteer = "Gazetteer",
    string Chronicle = "Chronicle",
    string Lore = "Lore",
    string Pages = "Pages",
    string Homebrew = "Homebrew");

/// <summary>A compiled book (<c>BookCompiler</c>): its chapters in order, which the draft's generator writes out as the editable book.</summary>
public sealed record BookDocument(string Title, string Tagline, string Language, IReadOnlyList<BookSection> Sections, BookLabels Labels);

/// <summary>What a book page rendered to. <paramref name="Html"/> is null when the theme does not support the language
/// (<paramref name="Refused"/> says so), which is the language gate.</summary>
public sealed record BookHtmlResult(string? Html, string? Refused, IReadOnlyList<string> Warnings, IReadOnlyList<string> MissingFonts);
