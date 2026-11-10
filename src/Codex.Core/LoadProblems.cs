namespace Codex.Core;

/// <summary>What a load problem is about.</summary>
public enum LoadProblemSource
{
    /// <summary>A <c>system.yaml</c> (or a system's own files).</summary>
    System,

    /// <summary>A content pack: its manifest, an entry in it, or the pack as a whole.</summary>
    Pack,

    /// <summary>A compiled plugin (<c>*.dll</c>).</summary>
    Plugin,

    /// <summary>The loader itself, not one file.</summary>
    Loader
}

/// <summary>
/// One problem found while loading systems and packs. <see cref="Message"/> is safe to show anyone (it names the
/// system or pack, never a path on the server); <see cref="Path"/> is where the file is, for whoever runs the server.
/// </summary>
/// <param name="Source">What kind of thing failed.</param>
/// <param name="Subject">Its name as an author knows it: a system id, a pack id, a plugin file name.</param>
/// <param name="Message">What is wrong, without file paths.</param>
/// <param name="SystemId">The system the problem belongs to, when known; null for a problem with no system.</param>
/// <param name="Path">The file or folder on the server, when there is one.</param>
public sealed record LoadProblem(LoadProblemSource Source, string Subject, string Message, string? SystemId = null, string? Path = null)
{
    /// <summary>The problem as one line: "Pack 'x': message".</summary>
    public string Text => Source == LoadProblemSource.Loader ? Message : $"{Source} '{Subject}': {Message}";
}

/// <summary>
/// A system that was found but did not load. <see cref="Id"/> is the id its file states when that much could be
/// read; <see cref="Name"/> is what to call it either way.
/// </summary>
public sealed record FailedSystem(string? Id, string Name, IReadOnlyList<string> Errors, string Path);
