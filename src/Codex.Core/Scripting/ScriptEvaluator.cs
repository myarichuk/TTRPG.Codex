using System;
using System.Collections.Generic;
using DynamicExpresso;
using DefaultEcs;
using Codex.Plugin.Abstractions;
using Codex.Core.Components;

namespace Codex.Core.Scripting;

public record AbilityContext(
    Entity Caster,
    Entity? Target,
    CodexWorld World,
    Dictionary<string, object>? Params = null
);

public class ScriptEvaluator
{
    private readonly Interpreter _interpreter;

    public ScriptEvaluator()
    {
        _interpreter = new Interpreter()
            .Reference(typeof(Entity))
            .Reference(typeof(CodexWorld))
            .Reference(typeof(IAbilityEffect))
            .Reference(typeof(DurationComponent))
            .Reference(typeof(StatusEffectComponent));

        // Use shorter names for ease of scripting
        _interpreter.Reference(typeof(DurationComponent), "Duration");
        _interpreter.Reference(typeof(StatusEffectComponent), "Status");
    }

    public void Execute(string script, AbilityContext context)
    {
        if (string.IsNullOrWhiteSpace(script)) return;

        try
        {
            _interpreter.SetVariable("context", context);
            _interpreter.SetVariable("caster", context.Caster);
            _interpreter.SetVariable("target", context.Target);
            _interpreter.SetVariable("world", context.World);

            _interpreter.Eval(script);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Script execution failed: {ex.Message}");
        }
    }
}