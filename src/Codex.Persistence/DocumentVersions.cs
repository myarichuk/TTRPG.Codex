using System.Runtime.CompilerServices;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>How saving a whole document went.</summary>
public enum SaveOutcome
{
    Saved,

    /// <summary>Someone saved the document after it was handed out (an edit in another tab, a reveal, the table): nothing was
    /// written. Load it again before saving.</summary>
    Changed,

    /// <summary>Not the caller's to save, or not a valid document. Nothing was written.</summary>
    Refused
}

/// <summary>
/// The version (RavenDB change vector) each document copy was at when it was handed out, so a save of the whole copy can tell
/// whether it still replaces the latest (review D2/D3). Kept beside the copy rather than on it, so it is never stored. A copy
/// the store never handed out (a new document) carries none and is saved as it is.
/// </summary>
public static class DocumentVersions
{
    private static readonly ConditionalWeakTable<object, string> Known = new();

    /// <summary>The version <paramref name="document"/> was handed out (or last saved) at, or null when it was never loaded.</summary>
    public static string? Of(object document) => Known.TryGetValue(document, out var version) ? version : null;

    /// <summary>Makes <paramref name="copy"/> carry the version of <paramref name="original"/>, as when an editor works on a
    /// copy of a loaded document. Returns <paramref name="copy"/>.</summary>
    public static T Carry<T>(T copy, object original) where T : class
    {
        if (Of(original) is { } version)
        {
            Known.AddOrUpdate(copy, version);
        }
        else
        {
            Known.Remove(copy);
        }

        return copy;
    }

    /// <summary>Records the version <paramref name="session"/> loaded (or saved) <paramref name="document"/> at.</summary>
    internal static T Track<T>(IAsyncDocumentSession session, T document) where T : class
    {
        if (session.Advanced.GetChangeVectorFor(document) is { } version)
        {
            Known.AddOrUpdate(document, version);
        }

        return document;
    }

    internal static void Set(object document, string version) => Known.AddOrUpdate(document, version);

    /// <summary>Saves <paramref name="session"/>, in which <paramref name="stored"/> was just loaded and changed to match
    /// <paramref name="copy"/>, only if <paramref name="stored"/> is still at the version <paramref name="copy"/> was handed out
    /// at: <see cref="SaveOutcome.Changed"/> when it is not, or when another save lands in between. On success
    /// <paramref name="copy"/> carries the new version, so it can be saved again.</summary>
    internal static async Task<SaveOutcome> SaveIfUnchangedAsync(IAsyncDocumentSession session, object stored, object copy)
    {
        if (Of(copy) is { } known && session.Advanced.GetChangeVectorFor(stored) != known)
        {
            return SaveOutcome.Changed;
        }

        session.Advanced.UseOptimisticConcurrency = true;
        try
        {
            await session.SaveChangesAsync();
        }
        catch (ConcurrencyException)
        {
            return SaveOutcome.Changed;
        }

        if (session.Advanced.GetChangeVectorFor(stored) is { } saved)
        {
            Known.AddOrUpdate(copy, saved);
        }

        return SaveOutcome.Saved;
    }
}
