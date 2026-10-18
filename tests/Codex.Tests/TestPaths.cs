namespace Codex.Tests;

/// <summary>Locates the checkout so tests can load the real <c>content/</c> folder.</summary>
public static class TestPaths
{
    public static string RepoRoot { get; } = Find();

    public static string ContentRoot => Path.Combine(RepoRoot, "content");

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !dir.GetFiles("TTRPG.Codex.sln").Any())
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (TTRPG.Codex.sln) above " + AppContext.BaseDirectory);
    }
}
