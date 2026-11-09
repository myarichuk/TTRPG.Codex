using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The built-in vitals handlers (<see cref="VitalsHandlers"/>, overhaul step 8) as intents, on a creature that is only
/// data. What the table makes of them is the encounter and runtime tests' (the oracle for this move); these pin which intents
/// say it, so a script handler can say the same.</summary>
public class VitalsHandlersTests
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: vitals-test
        name: Vitals test
        attributes:
          - { key: con, label: Constitution, modifier: half-minus-five }
        resources:
          - { key: HP, label: Hit points, maxKey: HP_Max, isPrimary: true }
        vitals:
          temporaryResource: TempHP
          down: {}
          deathSaves: {}
          damage: {}
          concentration: { saveAttribute: con }
          exhaustion: {}
          hitDice: { attribute: con }
          rests:
            - { name: long, restores: [HP], clears: [TempHP], hitDiceRegain: half, exhaustionDelta: -1, clearsConditions: [Frightened] }
        """);

    private static VitalsView Creature(int hp, bool player = true, params string[] conditions) => With(hp, player, new Dictionary<string, int>(), conditions);

    private static VitalsView With(int hp, bool player, Dictionary<string, int> more, params string[] conditions)
    {
        var pools = new Dictionary<string, int> { ["HP"] = hp, ["HP_Max"] = 20 };
        foreach (var (key, value) in more) pools[key] = value;
        return new VitalsView(pools, conditions, player, new Dictionary<string, object> { ["resistances"] = "fire" });
    }

    private static IEnumerable<string> Said(IEnumerable<SheetIntent> intents) =>
        intents.Select(i => i.Kind + (i.Fields.Count == 0 ? "" : " " + string.Join(" ", i.Fields.Select(f => $"{f.Key}={f.Value}"))));

    [Fact]
    public void Damage_IsAbsorbedByTemporaryPoints_ResistedByType_AndOweAConcentrationSave()
    {
        var result = VitalsHandlers.Damage(System, With(20, true, new() { ["TempHP"] = 3 }, "Concentrating"), "HP", 30, "fire", false);

        Assert.Equal(new[] { "set track=TempHP to=0", "set track=HP to=8", "roll definition=concentration dc=10 attribute=con" }, Said(result.Intents));
        Assert.Equal((15, 3, "resisted", 10), (result.Report.Dealt, result.Report.Absorbed, result.Report.Modifier, result.Report.ConcentrationDc));
    }

    [Fact]
    public void Damage_ToZero_KnocksAPlayerDown_EndingConcentration_AndKillsAMonster()
    {
        var down = VitalsHandlers.Damage(System, Creature(5, true, "Concentrating"), "HP", 7, null, false);
        var monster = VitalsHandlers.Damage(System, Creature(5, false), "HP", 7, null, false);

        Assert.Equal(new[] { "set track=HP to=0", "condition add=Unconscious source=zero hit points", "endConcentration" }, Said(down.Intents));
        Assert.True(down.Report.WentDown && down.Report.ConcentrationLost);
        Assert.Equal(new[] { "set track=HP to=0", "kill" }, Said(monster.Intents));
        Assert.True(monster.Report.Died);
    }

    [Fact]
    public void AHitWhileDown_IsAFailedSave_TwoOnACritical_AndTheThirdKills_Once()
    {
        var hit = VitalsHandlers.Damage(System, Creature(0, true, "Unconscious"), "HP", 4, null, isCritical: true);
        var third = VitalsHandlers.Damage(System, With(0, true, new() { ["DeathSaveFailures"] = 2 }, "Unconscious"), "HP", 4, null, false);

        Assert.Equal(new[] { "set track=HP to=0", "counter key=DeathSaveSuccesses to=0", "counter key=DeathSaveFailures to=2" }, Said(hit.Intents));
        Assert.Equal(2, hit.Report.FailuresAdded);
        Assert.Equal(new[] { "set track=HP to=0", "kill" }, Said(third.Intents));
        Assert.True(third.Report.Died);
    }

    [Fact]
    public void ARolledDeathSave_ANaturalTwentyGetsUp_AndThreeSuccessesStabilize()
    {
        var twenty = VitalsHandlers.RollDeathSave(System, With(0, true, new() { ["DeathSaveFailures"] = 1 }, "Unconscious"), _ => 20);
        var third = VitalsHandlers.RollDeathSave(System, With(0, true, new() { ["DeathSaveSuccesses"] = 2 }, "Unconscious"), _ => 12);
        var dead = VitalsHandlers.RollDeathSave(System, Creature(0, true, "Dead"), _ => throw new InvalidOperationException("no roll for the dead"));

        Assert.Equal(new[] { "counter key=DeathSaveFailures clear=True", "set track=HP to=1", "condition remove=Unconscious" }, Said(twenty.Intents));
        Assert.True(twenty.Report.Revived);
        Assert.Equal(new[] { "counter key=DeathSaveSuccesses clear=True", "condition add=Stable source=stabilized" }, Said(third.Intents));
        Assert.True(third.Report.Stabilized);
        Assert.Empty(dead.Intents);
        Assert.True(dead.Report.NotApplicable);
    }

    [Fact]
    public void ALongRest_RestoresAndClears_RegainsHalfTheHitDice_EasesExhaustion_AndLiftsConditions()
    {
        var view = With(0, true, new() { ["TempHP"] = 2, ["HitDice"] = 1, ["HitDice_Max"] = 6, ["Exhaustion"] = 2 }, "Unconscious", "Frightened");

        var result = VitalsHandlers.Rest(System, view, "long", Array.Empty<string>());

        Assert.Equal(new[] { "restored HP", "cleared TempHP", "hitDice 3", "exhaustionNow 1", "lifted Frightened" },
            result.Report.Select(s => $"{s.Kind} {s.Key ?? s.Amount.ToString()}"));
        Assert.Contains("condition remove=Unconscious", Said(result.Intents));
        Assert.Contains("set track=HitDice to=4", Said(result.Intents));
    }

    [Fact]
    public void WithNoSystem_DamageStopsAtZero_AndNothingElse()
    {
        var result = VitalsHandlers.Damage(null, Creature(5), "HP", 9, null, false);

        Assert.Equal(new[] { "set track=HP to=0" }, Said(result.Intents));
    }
}
