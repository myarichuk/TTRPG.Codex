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
    private readonly HashSet<string> _reservedUsernames = new();
    private string? _firstAdmin;

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

    public Task<UserDocument?> UpdateUserAsync(string userId, Action<UserDocument> mutate)
    {
        if (!_usersById.TryGetValue(userId, out var user))
        {
            return Task.FromResult<UserDocument?>(null);
        }

        mutate(user);
        // Sync external logins index
        _usersByExternalLogin.Clear();
        foreach (var userData in _usersById.Values)
        {
            foreach (var login in userData.ExternalLogins)
                _usersByExternalLogin[(login.Provider, login.ProviderKey)] = userData;
        }

        return Task.FromResult<UserDocument?>(user);
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

    public Task<bool> AnyUsersExistAsync() => Task.FromResult(_usersById.Count > 0);

    public Task<bool> TryReserveUsernameAsync(string username, string userId) =>
        Task.FromResult(_reservedUsernames.Add(username.Trim().ToLowerInvariant()));

    public Task<bool> TryClaimFirstAdminAsync(string userId)
    {
        if (_firstAdmin != null || _usersById.Values.Any(u => u.Roles.Contains("ServerAdmin"))) return Task.FromResult(false);
        _firstAdmin = userId;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<UserDocument>> ListAsync() =>
        Task.FromResult<IReadOnlyList<UserDocument>>(_usersById.Values.OrderBy(u => u.Status == AccountStatus.Pending ? 0 : 1).ThenBy(u => u.Username).ToList());

    public Task<int> CountPendingAsync() => Task.FromResult(_usersById.Values.Count(u => u.Status == AccountStatus.Pending));

    public async Task<bool> DeleteAsync(string userId)
    {
        if (!_usersById.TryGetValue(userId, out var user)) return false;
        await DeleteUserAsync(userId);
        _reservedUsernames.Remove(user.Username.Trim().ToLowerInvariant());
        return true;
    }

    // Clear state between tests
    public void Reset()
    {
        _usersById.Clear();
        _usersByUsername.Clear();
        _usersByEmail.Clear();
        _usersByExternalLogin.Clear();
        _reservedUsernames.Clear();
        _firstAdmin = null;
    }
}
