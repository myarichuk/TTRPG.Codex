namespace Codex.Persistence;

public interface IUserRepository
{
    Task<UserDocument?> GetUserByUsernameAsync(string username);
    Task<UserDocument?> GetUserByEmailAsync(string email);
    Task<UserDocument?> GetUserByExternalLoginAsync(string provider, string providerKey);
    Task<UserDocument?> GetUserByIdAsync(string id);
    Task CreateUserAsync(UserDocument user);
    /// <summary>Loads the stored user, applies <paramref name="mutate"/> and saves under optimistic concurrency, going again on a
    /// collision (D18): a caller changes only the fields it names, so switching language in one tab and saving an AI provider in
    /// another no longer drop each other. <paramref name="mutate"/> may run more than once. Returns the stored user, or null.</summary>
    Task<UserDocument?> UpdateUserAsync(string userId, Action<UserDocument> mutate);

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

    /// <summary>
    /// Whether <paramref name="userId"/>, about to be created, takes the server's first ServerAdmin seat. Asking
    /// <see cref="AnyUsersExistAsync"/> and then creating let two sign-ups at once both see an empty server and both become
    /// admin (R13). This claims a compare-exchange key only one writer can create, and only while no account is ServerAdmin,
    /// so players who signed up over the network before the DM do not use it up. Callers decide who may ask (see
    /// <c>ExposurePolicy.MayClaimFirstAdmin</c>).
    /// </summary>
    Task<bool> TryClaimFirstAdminAsync(string userId);

    /// <summary>Every account, waiting ones first, then by name: the admin's list.</summary>
    Task<IReadOnlyList<UserDocument>> ListAsync();

    /// <summary>How many accounts wait for approval.</summary>
    Task<int> CountPendingAsync();

    /// <summary>Deletes the account and frees its name for someone else. False when there is no such account.</summary>
    Task<bool> DeleteAsync(string userId);
}
