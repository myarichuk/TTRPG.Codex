namespace Codex.Plugin.Abstractions;

public record PackManifest(
    string Id,
    string Name,
    string Version,
    string SystemId,
    string MinAppVersion = "1.0.0",
    int Priority = 0,
    string[]? ContentPaths = null,
    string[]? Dependencies = null
);