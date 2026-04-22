namespace Codex.Plugin.Abstractions;

public interface ICodexSystemPlugin
{
    string SystemId { get; }
    string Name { get; }
    string Description { get; }
    string ThumbnailPath { get; }

    void RegisterComponents(ComponentRegistry registry);

    // Dynamic approach to break cyclic dependency
    void RegisterSystems(dynamic world);
}
