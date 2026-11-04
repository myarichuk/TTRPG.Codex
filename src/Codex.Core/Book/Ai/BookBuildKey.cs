using System.Security.Cryptography;
using System.Text;

namespace Codex.Core.Book;

/// <summary>
/// What a stored chapter was made from, as one hash. A stored rewrite is used only while the key recomputed from the campaign as it is
/// read now (for the same edition, so through the same masks) equals the key it was stored with. A chapter whose source changed, whose
/// secret changed, that a DM hid from the party, or that was built for another language, edition, style sheet or prompt version simply
/// has a different key and is printed from the deterministic text instead: the cache cannot serve what the reader may no longer read.
/// <para>The key hashes the prepared chapter (<see cref="BookWorkText"/>) and not the model's reply. The block bodies (the boxed secrets)
/// are part of it, because a stored reply holds the box as it was when it was built: an edited secret must not come back from the
/// cache. The model and the temperature are deliberately not in it: switching providers must not silently throw away, or silently
/// re-buy, a book. They are kept beside the stored chapter and shown, and re-running a chapter is an explicit click.</para>
/// </summary>
public static class BookBuildKey
{
    /// <summary>Bumped when the key's own recipe changes, so no old key can equal a new one.</summary>
    private const string Recipe = "book-key-1";

    /// <param name="audience">"dm" or "party": a build is never shared between the two.</param>
    public static string For(BookWorkItem item, string audience)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void Field(string? value)
        {
            // Length-prefixed, so ("ab", "c") and ("a", "bc") cannot be the same key.
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        Field(Recipe);
        Field(BookPrompts.Version);
        Field(audience);
        Field(item.Language);
        Field(item.Style.Trim());
        Field(item.Id);
        Field(item.Title);
        Field(item.Work.Text);
        Field(item.Work.Blocks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var block in item.Work.Blocks)
        {
            Field(block.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Field(block.Label);
            Field(block.Html);
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..32].ToLowerInvariant();
    }
}

/// <summary>Where a chapter stands against what was stored.</summary>
public enum BookSectionState
{
    /// <summary>Stored, and built from exactly what the book is made from now.</summary>
    Current,

    /// <summary>Stored, but the campaign (or the language, edition, style, prompt) moved since.</summary>
    Stale,

    /// <summary>Never built.</summary>
    New
}

public static class BookStaleness
{
    /// <summary>The state of each current chapter, in order. A stored chapter with no counterpart now (its source was deleted, or hidden
    /// from this edition) is simply not asked about: it is never printed, and a build drops it.</summary>
    public static IReadOnlyList<(string Id, BookSectionState State)> Classify(
        IReadOnlyList<(string Id, string Key)> current, IReadOnlyDictionary<string, string> stored) =>
        current.Select(c => (c.Id, stored.TryGetValue(c.Id, out var key)
            ? key == c.Key ? BookSectionState.Current : BookSectionState.Stale
            : BookSectionState.New)).ToList();
}
