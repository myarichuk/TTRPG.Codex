using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Codex.Tests;

public class ScriptEvaluatorTests : IDisposable
{
    private readonly CodexWorld _world = new();
    private readonly ScriptEvaluator _evaluator = new(NullLogger<ScriptEvaluator>.Instance);

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Execute_NoTargetAfterPreviousCallHadOne_DoesNotSeePreviousTarget()
    {
        // B4: the old shared-Interpreter implementation only updated the "target" variable when
        // a target was supplied, so a subsequent no-target call would silently operate on
        // whatever entity the previous call had targeted. Prove that no longer happens: a script
        // that checks target.HasValue must see it as false here, not the entity from call #1.
        var caster = _world.CreateEntity();
        var firstTarget = _world.CreateEntity();
        var secondCaster = _world.CreateEntity();

        var result1 = _evaluator.Execute(
            "world.AddStatus(target.Value, \"burning\", \"core\", 3.0)",
            new AbilityContext(caster, firstTarget, _world));
        Assert.True(result1.Success);
        Assert.True(firstTarget.Has<StatusEffectComponent>());

        // A second, target-less call that dereferences target.Value must fail loudly (a null
        // Nullable<Entity> has no Value) rather than silently reuse call #1's target the way the
        // old shared-interpreter implementation did.
        var result2 = _evaluator.Execute(
            "world.AddStatus(target.Value, \"frozen\", \"core\", 1.0)",
            new AbilityContext(secondCaster, null, _world));
        Assert.False(result2.Success);

        // The first target must be untouched by the second, target-less call.
        Assert.Equal("burning", firstTarget.Get<StatusEffectComponent>().EffectId);
    }

    [Fact]
    public void Execute_CannotReachRawWorld_SandboxHoldsUnderDispose()
    {
        // B3: the old ScriptEvaluator exposed CodexWorld (and therefore InnerWorld, the raw
        // DefaultEcs World) to scripts, so a script could call world.InnerWorld.Dispose() and
        // take down the single world shared by every user on the server. IScriptApi exposes no
        // such member, so this must fail to compile rather than execute.
        var caster = _world.CreateEntity();

        var result = _evaluator.Execute(
            "world.InnerWorld.Dispose()",
            new AbilityContext(caster, null, _world));

        Assert.False(result.Success);

        // The world must still be usable afterwards.
        var stillWorks = _world.CreateEntity();
        Assert.True(stillWorks.IsAlive);
    }

    [Fact]
    public void Execute_SameScriptTwiceWithDifferentTargets_EachInvocationIsIndependent()
    {
        var caster = _world.CreateEntity();
        var targetA = _world.CreateEntity();
        var targetB = _world.CreateEntity();

        const string script = "world.AddStatus(target.Value, \"poisoned\", \"core\", 5.0)";

        _evaluator.Execute(script, new AbilityContext(caster, targetA, _world));
        _evaluator.Execute(script, new AbilityContext(caster, targetB, _world));

        Assert.True(targetA.Has<StatusEffectComponent>());
        Assert.True(targetB.Has<StatusEffectComponent>());
    }
}
