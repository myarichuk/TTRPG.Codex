namespace Codex.Web;

/// <summary>What a new password must be: the sign-up form's and the setup's rule.</summary>
public static class PasswordRules
{
    /// <summary>The resource key of what is wrong with a username and password pair, or null when they will do.</summary>
    public static string? Problem(string? username, string? password, string? confirm)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return "Page.Register.Required";
        if (password != confirm) return "Page.Register.Mismatch";
        if (password.Length < 8) return "Page.Register.TooShort";
        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit)) return "Page.Register.Weak";
        return null;
    }
}
