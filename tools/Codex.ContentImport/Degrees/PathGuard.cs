namespace Codex.ContentImport.Degrees;

/// <summary>
/// The book and everything extracted from it stay out of the repository. A gitignore entry is only a backstop, so the tool refuses any
/// path that resolves, symbolic links included, to somewhere inside a git work tree.
/// </summary>
public static class PathGuard
{
    /// <summary>The nearest enclosing directory holding a <c>.git</c> entry (a folder, or the file a linked worktree has), or null.</summary>
    public static string? FindGitRoot(string path)
    {
        var resolved = Resolve(path);
        var dir = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved).Directory;
        for (; dir != null; dir = dir.Parent)
        {
            var dot = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dot) || File.Exists(dot))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    public static void EnsureOutsideGitTree(string path, string what)
    {
        if (FindGitRoot(path) != null)
        {
            throw new InvalidOperationException($"{what} must be outside any git work tree (a gitignore entry is not enough): move it and pass the new path.");
        }
    }

    /// <summary>The full path with every existing component's symbolic link followed. Components that do not exist yet are kept as given.</summary>
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            var target = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
            current = target != null ? Path.GetFullPath(target.FullName) : next;
        }

        return current;
    }
}
