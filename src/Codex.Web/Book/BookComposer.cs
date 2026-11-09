using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>
/// The one place a book is composed from a source, so the draft's generator, the art director, the leak check and a player's own book all
/// see the same chapters, with the labels in the book's language.
/// </summary>
public static class BookComposer
{
    public static BookLabels Labels(string language)
    {
        var words = Localizer.For(language);
        return new BookLabels(
            Contents: words["Campaign.Book.Label.Contents"], Overview: words["Campaign.Book.Label.Overview"], Cast: words["Campaign.Book.Label.Cast"],
            Gazetteer: words["Campaign.Book.Label.Gazetteer"], Chronicle: words["Campaign.Book.Label.Chronicle"], Lore: words["Campaign.Book.Label.Lore"],
            Pages: words["Campaign.Book.Label.Pages"], Homebrew: words["Campaign.Book.Label.Homebrew"]);
    }

    public static BookDocument Compose(BookSource source, string language) => BookCompiler.Compile(source, language, Labels(language));

    /// <summary>As <see cref="Compose(BookSource, string)"/>, with the positions of the headings that can carry a picture: the draft's
    /// generator finds an entry's heading by the line recorded here, never by its text (<see cref="BookDraftWriter"/>).</summary>
    public static BookDocument Compose(BookSource source, string language, out IReadOnlyList<BookEntryPosition> entries) =>
        BookCompiler.Compile(source, language, Labels(language), out entries);
}
