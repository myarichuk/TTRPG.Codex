using Raven.Client.Documents;

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

    public async Task UpdateUserAsync(UserDocument user)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(user);
        await session.SaveChangesAsync();
    }
}
