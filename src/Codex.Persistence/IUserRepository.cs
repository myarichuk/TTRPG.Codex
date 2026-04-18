namespace Codex.Persistence;

public interface IUserRepository
{
    Task<UserDocument?> GetUserByUsernameAsync(string username);
    Task<UserDocument?> GetUserByEmailAsync(string email);
    Task<UserDocument?> GetUserByExternalLoginAsync(string provider, string providerKey);
    Task<UserDocument?> GetUserByIdAsync(string id);
    Task CreateUserAsync(UserDocument user);
    Task UpdateUserAsync(UserDocument user);
}
