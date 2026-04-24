using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public interface ICodexSystemPlugin
{
    string SystemId { get; }

    void RegisterComponents(ComponentRegistry registry);

    // Dynamic approach to break cyclic dependency
    void RegisterSystems(dynamic world);

    IEnumerable<UISchema> GetUISchemas();
}
