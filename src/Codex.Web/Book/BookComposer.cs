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
    /// <para><paramref name="system"/>: the campaign's game system, for the names of its homebrew types (the groups of the homebrew appendix).
    /// Only the headings' words depend on it: the entries, their lines and their keys are the same without it.</para>
    public static BookLabels Labels(string language, Codex.Plugin.Abstractions.SystemDefinition? system = null)
    {
        var words = Localizer.For(language);
        var types = system?.Localize(language)?.EntityTypes;
        return new BookLabels(
            Contents: words["Campaign.Book.Label.Contents"], Overview: words["Campaign.Book.Label.Overview"], Cast: words["Campaign.Book.Label.Cast"],
            Gazetteer: words["Campaign.Book.Label.Gazetteer"], Chronicle: words["Campaign.Book.Label.Chronicle"], Lore: words["Campaign.Book.Label.Lore"],
            Pages: words["Campaign.Book.Label.Pages"], Homebrew: words["Campaign.Book.Label.Homebrew"],
            Appendix: words["Campaign.Book.Label.Appendix"], AppendixLetters: words["Campaign.Book.Label.AppendixLetters"],
            Quests: words["Campaign.Book.Label.Quests"], Factions: words["Campaign.Book.Label.Factions"], People: words["Campaign.Book.Label.People"],
            Places: words["Campaign.Book.Label.Places"], OtherPages: words["Campaign.Book.Label.OtherPages"])
        {
            HomebrewType = type => types?.FirstOrDefault(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase)) is { } found
                ? string.IsNullOrWhiteSpace(found.PluralLabel) ? found.Label : found.PluralLabel
                : null
        };
    }

    public static BookDocument Compose(BookSource source, string language) => BookCompiler.Compile(source, language, Labels(language));

    /// <summary>As <see cref="Compose(BookSource, string)"/>, with the positions of the headings that can carry a picture: the draft's
    /// generator finds an entry's heading by the line recorded here, never by its text (<see cref="BookDraftWriter"/>).</summary>
    public static BookDocument Compose(BookSource source, string language, out IReadOnlyList<BookEntryPosition> entries,
        Codex.Plugin.Abstractions.SystemDefinition? system = null) =>
        BookCompiler.Compile(source, language, Labels(language, system), out entries);
}
