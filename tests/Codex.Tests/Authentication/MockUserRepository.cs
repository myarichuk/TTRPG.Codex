using Codex.Persistence;

namespace Codex.Tests.Authentication;

/// <summary>
/// In-memory user repository for testing OAuth flows.
/// </summary>
public class MockUserRepository : IUserRepository
{
    private readonly Dictionary<string, UserDocument> _usersById = new();
    private readonly Dictionary<string, UserDocument> _usersByUsername = new();
    private readonly Dictionary<string, UserDocument> _usersByEmail = new();
    private readonly Dictionary<(string provider, string key), UserDocument> _usersByExternalLogin = new();

    public Task<UserDocument?> GetUserByIdAsync(string id) =>
        Task.FromResult(_usersById.TryGetValue(id, out var user) ? user : null);

    public Task<UserDocument?> GetUserByUsernameAsync(string username) =>
        Task.FromResult(_usersByUsername.TryGetValue(username, out var user) ? user : null);

    public Task<UserDocument?> GetUserByEmailAsync(string email) =>
        Task.FromResult(_usersByEmail.TryGetValue(email, out var user) ? user : null);

    public Task<UserDocument?> GetUserByExternalLoginAsync(string provider, string providerKey) =>
        Task.FromResult(
            _usersByExternalLogin.TryGetValue((provider, providerKey), out var user) ? user : null);

    public Task CreateUserAsync(UserDocument user)
    {
        _usersById[user.Id] = user;
        _usersByUsername[user.Username] = user;
        if (!string.IsNullOrEmpty(user.Email))
            _usersByEmail[user.Email] = user;

        foreach (var login in user.ExternalLogins)
            _usersByExternalLogin[(login.Provider, login.ProviderKey)] = user;

        return Task.CompletedTask;
    }

    public Task UpdateUserAsync(UserDocument user)
    {
        // Sync external logins index
        _usersByExternalLogin.Clear();
        foreach (var userData in _usersById.Values)
        {
            foreach (var login in userData.ExternalLogins)
                _usersByExternalLogin[(login.Provider, login.ProviderKey)] = userData;
        }

        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string id)
    {
        if (_usersById.TryGetValue(id, out var user))
        {
            _usersById.Remove(id);
            _usersByUsername.Remove(user.Username);
            if (!string.IsNullOrEmpty(user.Email))
                _usersByEmail.Remove(user.Email);

            foreach (var login in user.ExternalLogins)
                _usersByExternalLogin.Remove((login.Provider, login.ProviderKey));
        }

        return Task.CompletedTask;
    }

    public Task<IEnumerable<UserDocument>> GetAllUsersAsync() =>
        Task.FromResult(_usersById.Values.AsEnumerable());

    // Clear state between tests
    public void Reset()
    {
        _usersById.Clear();
        _usersByUsername.Clear();
        _usersByEmail.Clear();
        _usersByExternalLogin.Clear();
    }
}
