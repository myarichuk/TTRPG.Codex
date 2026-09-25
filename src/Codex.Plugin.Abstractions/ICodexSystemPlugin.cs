using System.Collections.Generic;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Plugin.Abstractions;

public interface ICodexSystemPlugin
{
    string SystemId { get; }

    void RegisterComponents(ComponentRegistry registry);

    // Dynamic approach to break cyclic dependency
    void RegisterSystems(dynamic world);

    IEnumerable<UISchema> GetUISchemas();

    /// <summary>
    /// This system's dice roller (3.6), or null to fall back to <see cref="StandardDiceRoller"/>.
    /// Most systems (D&amp;D 5e included) roll standard polyhedral dice and never need to
    /// override this; SWFFG's narrative dice pool (Success/Advantage/Triumph/Despair symbols
    /// instead of numbers) is exactly the case this exists for.
    /// </summary>
    IDiceRoller? GetDiceRoller() => null;
}
