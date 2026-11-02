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
}
