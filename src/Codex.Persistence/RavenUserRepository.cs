using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

public class RavenUserRepository(RavenDbService dbService) : IUserRepository
{
    public async Task<UserDocument?> GetUserByUsernameAsync(string username)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<UserDocument>().FirstOrDefaultAsync(u => u.Username == username);
    }

    public async Task<UserDocument?> GetUserByEmailAsync(string email)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<UserDocument>().FirstOrDefaultAsync(u => u.Email == email);
    }

    public async Task<UserDocument?> GetUserByExternalLoginAsync(string provider, string providerKey)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<UserDocument>().FirstOrDefaultAsync(u => u.ExternalLogins.Any(e => e.Provider == provider && e.ProviderKey == providerKey));
    }

    public async Task<UserDocument?> GetUserByIdAsync(string id)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<UserDocument>(id);
    }

    public async Task CreateUserAsync(UserDocument user)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(user);
        await session.SaveChangesAsync();
    }

    public Task<UserDocument?> UpdateUserAsync(string userId, Action<UserDocument> mutate) =>
        RavenUpdate.RunAsync(dbService, userId, mutate);

    public async Task<bool> AnyUsersExistAsync()
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<UserDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .AnyAsync();
    }

    public async Task<bool> TryClaimFirstAdminAsync(string userId)
    {
        using (var lookup = dbService.Store.OpenAsyncSession())
        {
            var adminExists = await lookup.Query<UserDocument>()
                .Customize(x => x.WaitForNonStaleResults())
                .AnyAsync(u => u.Roles.Contains(UserRoles.ServerAdmin));
            if (adminExists)
            {
                return false;
            }
        }

        using var session = dbService.Store.OpenAsyncSession(new SessionOptions
        {
            TransactionMode = TransactionMode.ClusterWide
        });

        session.Advanced.ClusterTransaction.CreateCompareExchangeValue("server/first-admin", userId);
        try
        {
            await session.SaveChangesAsync();
            return true;
        }
        catch (ConcurrencyException)
        {
            // Another sign-up claimed it first.
            return false;
        }
    }

    public async Task<bool> TryReserveUsernameAsync(string username, string userId)
    {
        var normalized = username.Trim().ToLowerInvariant();

        using var session = dbService.Store.OpenAsyncSession(new SessionOptions
        {
            TransactionMode = TransactionMode.ClusterWide
        });

        session.Advanced.ClusterTransaction.CreateCompareExchangeValue($"usernames/{normalized}", userId);

        try
        {
            await session.SaveChangesAsync();
            return true;
        }
        catch (ConcurrencyException)
        {
            // Someone else already holds the compare-exchange key for this username.
            return false;
        }
    }

    public async Task<IReadOnlyList<UserDocument>> ListAsync()
    {
        using var session = dbService.Store.OpenAsyncSession();
        var all = new List<UserDocument>();
        await using var stream = await session.Advanced.StreamAsync(session.Query<UserDocument>());
        while (await stream.MoveNextAsync()) all.Add(stream.Current.Document);
        return all
            .OrderBy(u => u.Status == AccountStatus.Pending ? 0 : 1)
            .ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<int> CountPendingAsync()
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<UserDocument>().CountAsync(u => u.Status == AccountStatus.Pending);
    }

    public async Task<bool> DeleteAsync(string userId)
    {
        string username;
        using (var session = dbService.Store.OpenAsyncSession())
        {
            var user = await session.LoadAsync<UserDocument>(userId);
            if (user == null) return false;
            username = user.Username;
            session.Delete(user);
            await session.SaveChangesAsync();
        }

        // The name is held by a compare-exchange value (TryReserveUsernameAsync); without this it stays taken for good.
        var key = $"usernames/{username.Trim().ToLowerInvariant()}";
        var held = await dbService.Store.Operations.SendAsync(new Raven.Client.Documents.Operations.CompareExchange.GetCompareExchangeValueOperation<string>(key));
        if (held != null && held.Value == userId)
        {
            await dbService.Store.Operations.SendAsync(new Raven.Client.Documents.Operations.CompareExchange.DeleteCompareExchangeValueOperation<string>(key, held.Index));
        }

        return true;
    }
}
