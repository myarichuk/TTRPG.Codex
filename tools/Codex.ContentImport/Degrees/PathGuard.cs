namespace Codex.ContentImport.Degrees;

/// <summary>An error whose message the tool wrote itself, so it is safe to print: it never holds a path, a file name or book content.</summary>
public sealed class DegreesException(string message) : Exception(message);

/// <summary>
/// The book and everything extracted from it stay out of the repository. A gitignore entry is only a backstop, so the tool refuses any
/// path that resolves, with every symbolic link in the chain followed, to somewhere inside a git work tree.
/// </summary>
public static class PathGuard
{
    private const int MaxLinkHops = 40;

    /// <summary>The nearest enclosing directory of the real path holding a <c>.git</c> entry (a folder, or the file a linked worktree has), or null.</summary>
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
            throw new DegreesException($"{what} must be outside any git work tree (a gitignore entry is not enough): move it and pass the new path.");
        }
    }

    /// <summary>
    /// The real path, as realpath gives it: each component that is a link is replaced by its target, and the target's own components are
    /// resolved again, so a link that points through another link is followed to the end. Components that do not exist yet are kept as given.
    /// </summary>
    public static string Resolve(string path)
    {
        var pending = new Queue<string>(Split(Path.GetFullPath(path), out var current));
        var hops = 0;
        while (pending.Count > 0)
        {
            var part = pending.Dequeue();
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            var target = info.Exists && info.LinkTarget != null ? info.ResolveLinkTarget(returnFinalTarget: false) : null;
            if (target == null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new DegreesException("A path has too many symbolic links to resolve.");
            }

            var rest = pending.ToArray();
            pending = new Queue<string>(Split(Path.GetFullPath(target.FullName), out current).Concat(rest));
        }

        return current;
    }

    private static string[] Split(string full, out string root)
    {
        root = Path.GetPathRoot(full) ?? "/";
        return full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }
}
