using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

public sealed class RavenServerSettingsStore(RavenDbService dbService) : IServerSettingsStore
{
    // Read on every sign-up, page and fetch; written by the admin only, through this store, so a copy kept here stays true.
    private volatile ServerSettingsDocument? cached;

    public async Task<ServerSettingsDocument> GetAsync()
    {
        if (cached is { } hit) return hit.Copy();
        using var session = dbService.Store.OpenAsyncSession();
        var stored = await session.LoadAsync<ServerSettingsDocument>(ServerSettingsDocument.DocumentId);
        if (stored == null) return new ServerSettingsDocument();
        cached = stored;
        return stored.Copy();
    }

    public ServerSettingsDocument Get()
    {
        if (cached is { } hit) return hit.Copy();
        using var session = dbService.Store.OpenSession();
        var stored = session.Load<ServerSettingsDocument>(ServerSettingsDocument.DocumentId);
        if (stored == null) return new ServerSettingsDocument();
        cached = stored;
        return stored.Copy();
    }

    public async Task<ServerSettingsDocument> UpdateAsync(Action<ServerSettingsDocument> change)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var stored = await session.LoadAsync<ServerSettingsDocument>(ServerSettingsDocument.DocumentId);
            if (stored == null)
            {
                stored = new ServerSettingsDocument();
                await session.StoreAsync(stored, ServerSettingsDocument.DocumentId);
            }

            change(stored);
            stored.FetchSites = stored.FetchSites.Select(ServerSettingsDocument.Site).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            stored.UpdatedAt = DateTime.UtcNow;
            try
            {
                await session.SaveChangesAsync();
                cached = stored;
                return stored.Copy();
            }
            catch (ConcurrencyException) when (attempt < 8)
            {
            }
        }
    }

    public async Task LogAsync(AdminLogDocument entry)
    {
        if (string.IsNullOrEmpty(entry.Id)) entry.Id = "adminlog/" + entry.At.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(entry);
        await session.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AdminLogDocument>> RecentLogAsync(int count)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<AdminLogDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .OrderByDescending(e => e.At)
            .Take(count)
            .ToListAsync();
    }

    public async Task<bool> MoveExistingServerAsync()
    {
        using (var look = dbService.Store.OpenAsyncSession())
        {
            if (await look.LoadAsync<ServerSettingsDocument>(ServerSettingsDocument.DocumentId) != null) return false;
            if (!await look.Query<UserDocument>().Customize(x => x.WaitForNonStaleResults()).AnyAsync()) return false;
        }

        // Owners, read in pages: a server has a handful of campaigns, but nothing here should assume it.
        var owners = new HashSet<string>(StringComparer.Ordinal);
        using (var read = dbService.Store.OpenAsyncSession())
        {
            await using var stream = await read.Advanced.StreamAsync(read.Query<CampaignDocument>());
            while (await stream.MoveNextAsync())
            {
                if (!string.IsNullOrEmpty(stream.Current.Document.OwnerId)) owners.Add(stream.Current.Document.OwnerId);
            }
        }

        foreach (var owner in owners)
        {
            await RavenUpdate.RunAsync<UserDocument>(dbService, owner, u =>
            {
                if (!u.Roles.Contains(UserRoles.GameMaster)) u.Roles.Add(UserRoles.GameMaster);
            });
        }

        // Admins run campaigns too, whether or not they own one yet.
        List<string> admins;
        using (var read = dbService.Store.OpenAsyncSession())
        {
            admins = await read.Query<UserDocument>().Customize(x => x.WaitForNonStaleResults())
                .Where(u => u.Roles.Contains(UserRoles.ServerAdmin)).Select(u => u.Id).ToListAsync();
        }

        foreach (var admin in admins)
        {
            await RavenUpdate.RunAsync<UserDocument>(dbService, admin, u =>
            {
                if (!u.Roles.Contains(UserRoles.GameMaster)) u.Roles.Add(UserRoles.GameMaster);
            });
        }

        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var settings = new ServerSettingsDocument { SetupDone = true, WebFetch = WebFetchRule.AnyPublic };
        await session.StoreAsync(settings, ServerSettingsDocument.DocumentId);
        try
        {
            await session.SaveChangesAsync();
        }
        catch (ConcurrencyException)
        {
            return false;
        }

        cached = null;
        return true;
    }
}
