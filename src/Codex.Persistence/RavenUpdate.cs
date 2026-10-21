using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>
/// The one write pattern for a document more than one writer touches (audit O1): load the stored
/// document, apply a small mutation to it, save under optimistic concurrency and, when another
/// writer got in between, reload and apply the mutation again. Callers never hand back a copy they
/// loaded earlier, so a save can only change the fields it names.
/// </summary>
internal static class RavenUpdate
{
    private const int MaxAttempts = 8;

    /// <summary>Applies <paramref name="mutate"/> to the stored document and returns it, or null when it does not exist.
    /// <paramref name="mutate"/> may run more than once (once per attempt), always against a fresh load.</summary>
    public static async Task<T?> RunAsync<T>(RavenDbService dbService, string id, Action<T> mutate) where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;

            var document = await session.LoadAsync<T>(id);
            if (document == null)
            {
                return null;
            }

            mutate(document);
            try
            {
                await session.SaveChangesAsync();
                return document;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                // Someone else saved this document between our load and our save: go again.
            }
        }
    }
}
