using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using DynamicExpresso;
using DefaultEcs;
using Microsoft.Extensions.Logging;

namespace Codex.Core.Scripting;

public record AbilityContext(
    Entity Caster,
    Entity? Target,
    CodexWorld World,
    Dictionary<string, object>? Params = null
);

public record ScriptExecutionResult(bool Success, string? Error = null)
{
    public static readonly ScriptExecutionResult Ok = new(true);
    public static ScriptExecutionResult Failed(string error) => new(false, error);
}

/// <summary>
/// The only surface a content-pack script may touch (B3 remediation). A script must never be
/// handed CodexWorld/InnerWorld (the raw DefaultEcs World) directly — that let a pack script
/// call world.InnerWorld.Dispose() and take down the single world shared by every user on the
/// server. Everything a script can safely do goes through here instead.
/// </summary>
public interface IScriptApi
{
    void Log(string message);
    void AddStatus(Entity target, string effectId, string packId, double durationRounds);
}

internal sealed class ScriptApi(CodexWorld world, ILogger logger) : IScriptApi
{
    public void Log(string message) => logger.LogInformation("[Script]: {Message}", message);

    public void AddStatus(Entity target, string effectId, string packId, double durationRounds) =>
        world.AddStatus(target, effectId, packId, durationRounds);
}

/// <summary>
/// Compiles and runs ability scripts. B4 remediation: the previous version held one shared,
/// stateful DynamicExpresso Interpreter and mutated it (SetVariable) on every call, which meant
/// a no-target script picked up whatever entity the *previous* caller happened to target, and
/// concurrent executions from different Blazor circuits stomped on each other's caster/target/
/// world variables. Each distinct script string is now compiled exactly once into a Lambda via
/// Interpreter.Parse, cached, and invoked with explicit per-call arguments — no shared mutable
/// interpreter state, so this is safe under concurrent execution.
/// </summary>
public class ScriptEvaluator(ILogger<ScriptEvaluator> logger)
{
    private readonly ConcurrentDictionary<string, Lambda> _compiledScripts = new();

    public ScriptExecutionResult Execute(string script, AbilityContext context)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return ScriptExecutionResult.Ok;
        }

        try
        {
            var lambda = _compiledScripts.GetOrAdd(script, Compile);
            var api = new ScriptApi(context.World, logger);

            lambda.Invoke(context.Caster, context.Target, api);
            return ScriptExecutionResult.Ok;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Script execution failed: {Script}", script);
            return ScriptExecutionResult.Failed(ex.Message);
        }
    }

    private static Lambda Compile(string script)
    {
        // A fresh Interpreter per compiled script, used only to Parse — no state survives past
        // this call, so there's nothing left to share or race on between invocations.
        var interpreter = new Interpreter()
            .Reference(typeof(Entity))
            .Reference(typeof(IScriptApi));

        return interpreter.Parse(
            script,
            new Parameter("caster", typeof(Entity)),
            new Parameter("target", typeof(Entity?)),
            new Parameter("world", typeof(IScriptApi)));
    }
}
