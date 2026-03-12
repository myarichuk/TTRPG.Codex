namespace Codex.Persistence;

public interface IUserRepository
{
    Task<UserDocument?> GetUserByUsernameAsync(string username);
    Task<UserDocument?> GetUserByIdAsync(string id);
    Task CreateUserAsync(UserDocument user);
    Task UpdateUserAsync(UserDocument user);
}
