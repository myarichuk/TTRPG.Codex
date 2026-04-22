using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public interface IAbilityEffect
{
    string Type { get; }
    string? Script { get; }
    Dictionary<string, object>? Params { get; }
}