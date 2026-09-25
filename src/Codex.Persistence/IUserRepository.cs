namespace Codex.Persistence;

public interface IUserRepository
{
    Task<UserDocument?> GetUserByUsernameAsync(string username);
    Task<UserDocument?> GetUserByEmailAsync(string email);
    Task<UserDocument?> GetUserByExternalLoginAsync(string provider, string providerKey);
    Task<UserDocument?> GetUserByIdAsync(string id);
    Task CreateUserAsync(UserDocument user);
    Task UpdateUserAsync(UserDocument user);

    /// <summary>
    /// True if at least one user already exists. Used to decide whether a newly-registering
    /// user is the very first account in the system (see B1 remediation: the first user, and only
    /// the first user, is granted ServerAdmin when no Codex:AdminUsername is configured).
    /// </summary>
    Task<bool> AnyUsersExistAsync();

    /// <summary>
    /// Atomically reserves a normalized (lower-cased) username for the given user id.
    /// Returns false if the username is already taken. Backed by a RavenDB compare-exchange
    /// value in a cluster-wide transaction so two concurrent registrations can't both win (B12).
    /// </summary>
    Task<bool> TryReserveUsernameAsync(string username, string userId);
}
