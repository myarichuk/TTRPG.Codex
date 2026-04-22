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
            .Reference(typeof(TypedComponent))
            .Reference(typeof(DurationComponent))
            .Reference(typeof(StatusEffectComponent));

        _interpreter.Reference(typeof(DurationComponent), "Duration");
        _interpreter.Reference(typeof(StatusEffectComponent), "Status");

        // Add a simple log helper
        _interpreter.SetFunction("log", (Action<string>)(msg => Console.WriteLine($"[Script]: {msg}")));
    }

    public void Execute(string script, AbilityContext context)
    {
        if (string.IsNullOrWhiteSpace(script)) return;

        try
        {
            // Use specific types instead of dynamic to help the interpreter
            _interpreter.SetVariable("context", context);
            _interpreter.SetVariable("caster", context.Caster, typeof(Entity));
            if (context.Target.HasValue)
            {
                _interpreter.SetVariable("target", context.Target.Value, typeof(Entity));
            }
            _interpreter.SetVariable("world", context.World, typeof(CodexWorld));

            _interpreter.Eval(script);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Script execution failed: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
            }
        }
    }
}