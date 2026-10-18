using System.Collections.Generic;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// A game system. Most systems are pure data (<see cref="SystemDefinition"/> loaded from
/// <c>system.yaml</c>, wrapped by the core's data-driven plugin); a compiled implementation of this
/// interface is only needed for behaviour data can't express - a narrative dice roller, a custom
/// ECS system, bespoke derived stats.
/// </summary>
public interface ICodexSystemPlugin
{
    string SystemId { get; }

    /// <summary>Human-readable name shown in the UI. Defaults to the id.</summary>
    string DisplayName => SystemId;

    /// <summary>The data definition this plugin is built on, when there is one.</summary>
    SystemDefinition? Definition => null;

    void RegisterComponents(ComponentRegistry registry);

    // Dynamic approach to break cyclic dependency
    void RegisterSystems(dynamic world);

    IEnumerable<UISchema> GetUISchemas();

    /// <summary>
    /// This system's dice roller, or null to fall back to <see cref="StandardDiceRoller"/>. Most
    /// systems roll standard polyhedral dice; a narrative dice pool (symbols instead of numbers) is
    /// exactly the case this exists for.
    /// </summary>
    IDiceRoller? GetDiceRoller() => null;
}
