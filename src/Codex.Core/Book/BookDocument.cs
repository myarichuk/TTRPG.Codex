namespace Codex.Core.Book;

/// <summary>One chapter of a book: Markdown in the book's dialect. Its heading is part of the Markdown.</summary>
public sealed record BookSection(string Id, string Markdown);

/// <summary>The words a book prints that are not the campaign's own. Defaults are English; the app passes the reader's language.</summary>
/// <para>The chapters' titles and the headings of their groups (docs/BOOK_EXPORT.md 51): the wiki's kinds in Pages, and the cast and the
/// homebrew as appendices, lettered in the order they are in the book (<see cref="Appendix"/>: <c>{0}</c> the letter, <c>{1}</c> the title;
/// <see cref="AppendixLetters"/>: the letters, in order).</para>
public sealed record BookLabels(
    string Contents = "Contents",
    string Overview = "Introduction",
    string Cast = "Cast",
    string Gazetteer = "Gazetteer",
    string Chronicle = "The story so far",
    string Lore = "Lore",
    string Pages = "Pages",
    string Homebrew = "Homebrew",
    string Appendix = "Appendix {0}: {1}",
    string AppendixLetters = "AB",
    string Quests = "Quests",
    string Factions = "Factions",
    string People = "People",
    string Places = "Places",
    string OtherPages = "Other pages")
{
    /// <summary>What the campaign's game system calls a homebrew type, in the plural (its creatures, its items): the heading of that type's
    /// group in the homebrew appendix. Without it, or for a type the system does not describe, the group is headed by the type as stored.</summary>
    public Func<string, string?>? HomebrewType { get; init; }
}

/// <summary>A compiled book (<c>BookCompiler</c>): its chapters in order, which the draft's generator writes out as the editable book.</summary>
public sealed record BookDocument(string Title, string Tagline, string Language, IReadOnlyList<BookSection> Sections, BookLabels Labels);

/// <summary>What a book page rendered to. <paramref name="Html"/> is null when the theme does not support the language
/// (<paramref name="Refused"/> says so), which is the language gate.</summary>
public sealed record BookHtmlResult(string? Html, string? Refused, IReadOnlyList<string> Warnings, IReadOnlyList<string> MissingFonts);
