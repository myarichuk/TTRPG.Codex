using System;
using System.Collections.Generic;
using DynamicExpresso;
using DefaultEcs;
using Codex.Plugin.Abstractions;

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
            .Reference(typeof(IAbilityEffect));

        // You can add more global functions or types here as needed
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
            // In a real system, you'd log this properly and perhaps surface it to the DM
            Console.WriteLine($"Script execution failed: {ex.Message}");
        }
    }
}