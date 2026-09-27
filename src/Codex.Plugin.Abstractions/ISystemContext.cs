namespace Codex.Plugin.Abstractions;

/// <summary>A plugin-provided system updated once per <see cref="ISystemContext"/> tick.
/// Narrower than a full ECS system: sufficient for over-time rules (e.g. a wound/bleed
/// system) without dragging an ECS framework reference into the plugin contract.</summary>
public interface ITickSystem
{
    void Update(float deltaTime);
}

/// <summary>Typed replacement for the old <c>RegisterSystems(dynamic world)</c> (5.2a, B16).
/// The old signature used <c>dynamic</c> to dodge a layering problem (the concrete world
/// lives in Core, which plugins must not reference). This interface lives in Abstractions,
/// so plugins program against a contract and the runtime adapts it onto its own world.</summary>
public interface ISystemContext
{
    void AddTickSystem(ITickSystem system);
}
