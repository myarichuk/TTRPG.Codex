using DefaultEcs;
using DefaultEcs.System;
using System.Collections.Generic;
using Codex.Core.Systems;

namespace Codex.Core;

public sealed class CodexWorld : IDisposable
{
    private readonly World _world;
    private ISystem<float>? _systems;
    private readonly List<ISystem<float>> _registeredSystems = new();

    public World InnerWorld => _world;

    public CodexWorld()
    {
        _world = new World();
        // Core systems
        AddSystem(new DurationSystem(_world));
    }

    public void AddSystem(ISystem<float> system)
    {
        _registeredSystems.Add(system);
        _systems = new SequentialSystem<float>(_registeredSystems.ToArray());
    }

    public Entity CreateEntity()
    {
        return _world.CreateEntity();
    }

    public void Tick(float deltaTime)
    {
        _systems?.Update(deltaTime);
    }

    public void Dispose()
    {
        _systems?.Dispose();
        _world.Dispose();
    }
}