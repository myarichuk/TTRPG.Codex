using System.Security.Cryptography;
using System.Text;
using Codex.Core.Book;

namespace Codex.Persistence;

/// <summary>
/// A place in a book where a picture can go, made by code from what the compiler wrote for one audience (docs/BOOK_EXPORT.md 28.3, item 1):
/// the cover, a header above each chapter, and one beside each entry heading that can carry a picture. A slot sits outside the chapter's
/// prose, so a rewrite can never drop or move what is in it.
/// <para><paramref name="Key"/> is a hash of what the slot shows <i>as this audience sees it</i>: an actor the party knows only as a stranger
/// has the key of the mask, not of the actor, so the GM's picture of the real character and the party's picture of the hooded figure never
/// share a key. A subject the audience cannot see has no slot at all, because the compiler wrote no entry for it.</para>
/// </summary>
public sealed record BookSlot(string Key, BookAudience Audience, string Kind, string Section, string Label, BookEntryPosition? Entry)
{
    /// <summary>The box the picture is scaled for (<see cref="BookImageScaler.BoxFor"/>).</summary>
    public string ScaleKind => Kind switch
    {
        BookSlots.Cover => "cover",
        BookSlots.Header => "header",
        BookSlots.Spot => "spot",
        _ => Entry?.Kind ?? "place"
    };
}

/// <summary>A stored book picture chosen for a slot of the book being made.</summary>
public sealed record BookSlotPicture(BookSlot Slot, BookArtPicture Picture);

public static class BookSlots
{
    public const string Cover = "cover";
    public const string Header = "header";
    public const string Entry = "entry";

    /// <summary>A small square picture at the end of an entry (docs/BOOK_EXPORT.md 28.3, step 7). Every entry has one, but it is only a place
    /// the art director may suggest a picture for: the tray shows it once it has a suggestion or a picture, and the book prints it only then.</summary>
    public const string Spot = "spot";

    /// <summary>The form of every key: "s" and 24 hex digits. Anything else posted as a key is refused before it is looked up.</summary>
    public static bool IsKey(string? key) =>
        key is { Length: 25 } && key[0] == 's' && key.AsSpan(1).IndexOfAnyExcept("0123456789abcdef") < 0;

    public static string KeyFor(string subject)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("book-slot/v1\n" + subject));
        return "s" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>What an entry's slot is about, as the audience sees it. A party reading of an actor it does not know is the mask (the
    /// repository hands the party a stranger with <see cref="ActorVisibility.Unknown"/>); every other reading is the actor itself.</summary>
    public static string ActorSubject(BookAudience audience, ActorDocument actor) =>
        (audience == BookAudience.Party && actor.Visibility == ActorVisibility.Unknown ? "mask:" : "actor:") + actor.Id;

    public static string LocationSubject(string regionId, string locationId) => "location:" + regionId + "/" + locationId;

    public static string WikiSubject(string pageId) => "wiki:" + pageId;

    public static string HomebrewSubject(string entryId) => "homebrew:" + entryId;

    public static string ChapterSubject(string sectionId) => "chapter:" + sectionId;

    /// <summary>A session of the Chronicle, whose picture is a scene from it.</summary>
    public static string SessionSubject(string sessionId) => "session:" + sessionId;

    /// <summary>The key of the small picture at the end of an entry: the entry's subject, as this audience sees it, so a stranger's spot is the
    /// mask's.</summary>
    public static string SpotKey(BookEntryPosition entry) => KeyFor("spot:" + entry.Subject);

    public const string CoverSubject = "cover";

    /// <summary>Every slot of a compiled book: the cover, a header per chapter, one per entry the compiler recorded, and a spot at the end of each
    /// entry. All of it comes from the source's own compile, so the slots of the party edition are the party's.</summary>
    public static IReadOnlyList<BookSlot> For(BookSource source, BookDocument compiled, IReadOnlyList<BookEntryPosition> entries)
    {
        var slots = new List<BookSlot> { new(KeyFor(CoverSubject), source.Audience, Cover, string.Empty, compiled.Title, null) };
        foreach (var section in compiled.Sections)
        {
            slots.Add(new BookSlot(KeyFor(ChapterSubject(section.Id)), source.Audience, Header, section.Id, ChapterTitle(section), null));
        }

        foreach (var entry in entries)
        {
            slots.Add(new BookSlot(entry.SlotKey, source.Audience, Entry, entry.Section, entry.Alt, entry));
        }

        foreach (var entry in entries)
        {
            slots.Add(new BookSlot(SpotKey(entry), source.Audience, Spot, entry.Section, entry.Alt, entry));
        }

        return slots;
    }

    /// <summary>The slots the old HTML route places pictures in (docs/BOOK_EXPORT.md 44): all but the spots, which only the editable book prints.
    /// A spot's picture is then never read there, so it costs no decode and no place under the picture limit.</summary>
    public static IReadOnlyList<BookSlot> PlacedByTheOldRoute(IEnumerable<BookSlot> slots) => [.. slots.Where(s => s.Kind != Spot)];

    /// <summary>For each slot, the stored picture this audience may have there: the party edition takes only pictures made for the party;
    /// the GM edition takes its own, and a party picture where it has none (anything the party may see, the GM may too).</summary>
    public static IReadOnlyList<BookSlotPicture> Choose(IReadOnlyList<BookSlot> slots, BookArtDocument? stored)
    {
        if (stored == null || stored.Pictures.Count == 0) return [];
        var chosen = new List<BookSlotPicture>();
        foreach (var slot in slots)
        {
            var picture = Pick(stored.Pictures, slot.Key, slot.Audience);
            if (picture != null) chosen.Add(new BookSlotPicture(slot, picture));
        }

        return chosen;
    }

    /// <summary>The edition a picture made for <paramref name="audience"/> is tagged with.</summary>
    public static string Tag(BookAudience audience) => audience == BookAudience.Dm ? BookArtDocument.Dm : BookArtDocument.Party;

    private static BookArtPicture? Pick(IEnumerable<BookArtPicture> pictures, string key, BookAudience audience)
    {
        BookArtPicture? party = null;
        foreach (var p in pictures)
        {
            if (p.Slot != key) continue;
            if (p.BuiltFrom == BookArtDocument.Party) party ??= p;
            else if (p.BuiltFrom == BookArtDocument.Dm && audience == BookAudience.Dm) return p;
        }

        return party;
    }

    private static string ChapterTitle(BookSection section)
    {
        var first = section.Markdown.AsSpan();
        var end = first.IndexOf('\n');
        var line = (end < 0 ? first : first[..end]).ToString();
        if (!line.StartsWith("# ", StringComparison.Ordinal)) return section.Id;
        var sb = new StringBuilder();
        for (var i = 2; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) i++;
            sb.Append(line[i]);
        }

        return sb.ToString().Trim();
    }
}
