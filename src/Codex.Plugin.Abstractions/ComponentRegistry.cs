using System;
using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public class ComponentRegistry
{
    public HashSet<Type> RegisteredComponents { get; } = new();

    // Minimal implementation for registering components
    public void Register<T>()
    {
        RegisteredComponents.Add(typeof(T));
    }
}
