using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Events, handlers and intents on a sheet (<see cref="SheetEvents"/>, <see cref="SheetIntents"/>, overhaul step 5), on
/// a small system of their own. The reference fixtures are the spec; these pin the edges it leaves implicit: limits, what is
/// left for the table, what a handler that cannot run says, and where tracks start.</summary>
public class SheetEventsTests
{
    private static SystemDefinition System(string events = "{}", string tracks = "") => SystemDefinitionLoader.Parse($$"""
        id: event-test
        name: Event test
        conditions: [Broken, Dead, Hurt, Worse]
        sheetKinds:
          - kind: hero
            label: Hero
            values:
              - { key: grit, formula: "2" }
            tracks:
              - { key: stress, max: 6, overflow: { into: scars, then: reset, ask: { who: gm, choices: [a, b] } } }
              - { key: scars, max: 3, thresholds: [{ atOrAbove: 3, condition: Broken }] }
              - { key: heat, max: 4, overflow: { into: wanted, by: "excess > 2 ? 2 : 1", then: carry } }
              - { key: wanted, max: 9 }
              - { key: luck, max: 3, overflow: { into: luck, then: keep } }
              - { key: HP, max: 10, min: -5, start: max }
              - { key: xp, max: 5 }
              - { key: notes, display: slots, holds: { type: text }, max: 2 }
              - key: wounds
                display: slots
                holds: { type: text }
                levels: [{ level: 1, slots: 1, condition: Hurt }, { level: 2, slots: 1, condition: Worse }]
                whenFull: pushUp
                beyond: { condition: Dead }
              - key: bruises
                display: slots
                holds: { type: text }
                levels: [{ level: 1, slots: 1 }]
        {{tracks}}
        events: {{events}}
        """);

    private static SheetIntent Intent(string kind, params (string Key, object? Value)[] fields) =>
        new(kind, null, fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));

    private static SheetIntentResult Apply(SystemDefinition system, Dictionary<string, object> sheet, params SheetIntent[] intents) =>
        SheetIntents.Apply(system, NullEntryResolver.Instance, sheet, intents);

    private static List<string> Conditions(SystemDefinition system, Dictionary<string, object> sheet) =>
        new SheetModel(system, NullEntryResolver.Instance, sheet).Evaluate().Conditions;

    // ---- Tracks ----

    [Fact]
    public void Overflow_ResetsTheTrack_AddsToTheOtherOne_AndAsks()
    {
        var system = System();
        var sheet = new Dictionary<string, object> { ["stress"] = 5L, ["scars"] = 2L };

        var result = Apply(system, sheet, Intent("adjust", ("track", "stress"), ("by", 4d)));

        Assert.Equal(0L, sheet["stress"]);
        Assert.Equal(3L, sheet["scars"]);
        Assert.Contains("Broken", Conditions(system, sheet)); // the other track's threshold follows
        var ask = Assert.Single(result.Raised);
        Assert.Equal("ask", ask.Kind);
        Assert.Equal("gm", ask.Fields["who"]);
        Assert.Equal(new object?[] { "a", "b" }, (IEnumerable<object?>)ask.Fields["choices"]!);
        Assert.Equal(new[] { ask }, result.Left); // nothing on the sheet can answer it
        Assert.Equal(new[] { "scars", "stress" }, result.Changed);
    }

    [Fact]
    public void Carry_OverflowsOncePerFill_AndByReadsTheExcess()
    {
        var system = System();
        var sheet = new Dictionary<string, object> { ["heat"] = 3L };

        Apply(system, sheet, Intent("adjust", ("track", "heat"), ("by", 8d)));

        // 3 + 8 = 11 on a track of 4: 7 past (wanted +2, since more than 2 went past), then 3 past (+2), then 3 is within.
        Assert.Equal(3L, sheet["heat"]);
        Assert.Equal(4L, sheet["wanted"]);
    }

    [Fact]
    public void ATrackThatOverflowsIntoItself_Stops()
    {
        var system = System();
        var sheet = new Dictionary<string, object> { ["luck"] = 3L };

        Apply(system, sheet, Intent("adjust", ("track", "luck"), ("by", 1d)));

        Assert.Equal(3L, sheet["luck"]); // keep: full, and the overflows into itself stopped at the limit
    }

    [Fact]
    public void Set_IsKeptWithinTheTrack_AndNeverOverflows()
    {
        var system = System();
        var sheet = new Dictionary<string, object> { ["stress"] = 1L, ["HP"] = 4L };

        Apply(system, sheet, Intent("set", ("track", "stress"), ("to", 20d)), Intent("set", ("track", "HP"), ("to", -20d)));

        Assert.Equal(6L, sheet["stress"]);
        Assert.False(sheet.ContainsKey("scars"));
        Assert.Equal(-5L, sheet["HP"]);
    }

    [Fact]
    public void ATrackTheSheetHasNoValueFor_StartsFromItsStart_OrFromNothing()
    {
        var system = System();
        var sheet = new Dictionary<string, object>();

        Apply(system, sheet, Intent("adjust", ("track", "HP"), ("by", -3d)), Intent("mark", ("track", "xp")));

        Assert.Equal(7L, sheet["HP"]); // start: max
        Assert.Equal(1L, sheet["xp"]);
    }

    [Fact]
    public void Recompute_WritesEachTracksMaximum_AndItsDeclaredStart_AndTheTableMirrorsThem()
    {
        var system = System();
        var actor = new ActorDocument { Sheet = { ["kind"] = "hero", ["stress"] = 2L } };

        new SheetRuleEngine(system, NullEntryResolver.Instance).Recompute(actor.Sheet);
        var registry = new ComponentRegistry();
        registry.Register<ResourcePoolComponent>();
        ActorSheetSync.ApplySheetToState(actor, system, registry);

        Assert.Equal(6L, Assert.IsType<long>(actor.Sheet["stress_Max"], exactMatch: false));
        Assert.Equal(10L, actor.Sheet["HP"]); // start: max, on a sheet that had none
        Assert.False(actor.Sheet.ContainsKey("xp")); // no start declared: nothing is written
        Assert.False(actor.Sheet.ContainsKey("notes_Max")); // slots stay text on the sheet
        var pool = registry.Hydrate(actor.State).OfType<ResourcePoolComponent>().Single();
        Assert.Equal(2, pool.Get("stress"));
        Assert.Equal(6, pool.Get("stress_Max"));
        Assert.Equal(10, pool.Get("HP"));
        Assert.False(pool.Pools.ContainsKey("notes"));
    }

    // ---- Boxes and slots ----

    [Fact]
    public void Slots_TakeTextUpToTheirMaximum_AndClearByText()
    {
        var system = System();
        var sheet = new Dictionary<string, object>();

        var full = Apply(system, sheet,
            Intent("mark", ("track", "notes"), ("text", "a")), Intent("mark", ("track", "notes"), ("text", "b")), Intent("mark", ("track", "notes"), ("text", "c")));
        Assert.Equal(new object[] { "a", "b" }, (List<object>)sheet["notes"]);
        Assert.Equal("c", Assert.Single(full.Left).Fields["text"]);

        Apply(system, sheet, Intent("clear", ("track", "notes"), ("text", "A")));
        Assert.Equal(new object[] { "b" }, (List<object>)sheet["notes"]);
    }

    [Fact]
    public void SlotsInLevels_PushUp_ThenBringTheConditionBeyond_ButOnlyWhenTheTrackSaysSo()
    {
        var system = System();
        var sheet = new Dictionary<string, object>();

        var result = Apply(system, sheet,
            Intent("mark", ("track", "wounds"), ("level", 1d), ("text", "cut")),
            Intent("mark", ("track", "wounds"), ("level", 1d), ("text", "gash")),
            Intent("mark", ("track", "wounds"), ("level", 1d), ("text", "stab")),
            Intent("mark", ("track", "bruises"), ("text", "one")),
            Intent("mark", ("track", "bruises"), ("text", "two")));

        var wounds = (Dictionary<string, object>)sheet["wounds"];
        Assert.Equal(new object[] { "cut" }, (List<object>)wounds["1"]);
        Assert.Equal(new object[] { "gash" }, (List<object>)wounds["2"]);
        Assert.Equal(new object[] { "Dead" }, (List<object>)sheet["conditions"]);
        Assert.Equal(new[] { "Dead", "Hurt", "Worse" }, Conditions(system, sheet).Order());
        Assert.Equal("two", Assert.Single(result.Left).Fields["text"]); // bruises do not push up

        Apply(system, sheet, Intent("clear", ("track", "wounds"), ("level", 2d)));
        Assert.False(((Dictionary<string, object>)sheet["wounds"]).ContainsKey("2"));
    }

    [Fact]
    public void Conditions_AreAddedOnce_AndRemoved()
    {
        var system = System();
        var sheet = new Dictionary<string, object>();

        Apply(system, sheet, Intent("condition", ("add", "Hurt")), Intent("condition", ("add", "hurt")), Intent("condition", ("add", "Worse")));
        Assert.Equal(new object[] { "Hurt", "Worse" }, (List<object>)sheet["conditions"]);

        Apply(system, sheet, Intent("condition", ("remove", "HURT")));
        Assert.Equal(new object[] { "Worse" }, (List<object>)sheet["conditions"]);
    }

    [Fact]
    public void WhatTheSheetCannotDo_IsLeftForTheTable()
    {
        var system = System();
        var sheet = new Dictionary<string, object> { ["stress"] = 1L };
        var other = new SheetIntent("adjust", "someone-else", new Dictionary<string, object?> { ["track"] = "stress", ["by"] = 1d });
        var mine = new SheetIntent("adjust", "me", new Dictionary<string, object?> { ["track"] = "stress", ["by"] = 1d });
        var roll = Intent("roll", ("definition", "save"));
        var log = Intent("log", ("key", "Something.Happened"));

        var result = SheetIntents.Apply(system, NullEntryResolver.Instance, sheet, new[] { other, mine, roll, log }, self: "me");

        Assert.Equal(new[] { other, roll, log }, result.Left);
        Assert.Equal(2L, sheet["stress"]);
    }

    // ---- Events and handlers ----

    [Fact]
    public void AnEventThatIsAnIntent_IsApplied_AndOnlyWhatItRaisedIsReturned()
    {
        var system = System();
        var sheet = new Dictionary<string, object> { ["stress"] = 6L };

        var result = SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("mark", ("track", "stress")));

        Assert.Equal("ask", Assert.Single(result.Intents).Kind);
        Assert.Equal(1L, sheet["scars"]);
    }

    [Fact]
    public void HandlerLines_AddTheirIntentsWhenTheyHold_WorkingOutFormulasAndKeepingBareWords()
    {
        var system = System("""
            {
              hit: [
                { when: "ev.amount > 3", intents: [{ adjust: { track: HP, by: "-ev.amount + grit" } }, { condition: { add: Hurt } }] },
                { when: "ev.amount > 100", intents: [{ condition: { add: Dead } }] },
                { intents: [{ log: { key: Hit.Taken, args: "ev.who" } }, { adjust: { actor: ev.who, track: stress, by: 1 } }] }
              ]
            }
            """);
        var sheet = new Dictionary<string, object> { ["HP"] = 10L };

        var result = SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("hit", ("amount", 5L), ("who", "ally")));

        Assert.Equal(new[] { "adjust", "condition", "log", "adjust" }, result.Intents.Select(i => i.Kind));
        Assert.Equal(-3d, result.Intents[0].Fields["by"]);
        Assert.Equal("Hit.Taken", result.Intents[2].Fields["key"]);
        Assert.Equal("ally", result.Intents[2].Fields["args"]);
        Assert.Equal("ally", result.Intents[3].Actor);
        Assert.Equal(7L, sheet["HP"]);
        Assert.Equal(new object[] { "Hurt" }, (List<object>)sheet["conditions"]);
        Assert.Equal(new[] { "log", "adjust" }, result.Applied.Left.Select(i => i.Kind));
    }

    [Fact]
    public void AHandlerThatCannotRun_SaysWhy()
    {
        var system = System("""
            {
              broken: [{ intents: [{ adjust: { track: HP, by: "nothing + 1" } }] }],
              scripted: { script: rules.js#onIt },
              tick: [{ target: campaign.clocks, intents: [{ adjust: { clock: ev.clock, by: 1 } }] }],
              odd: { builtin: teleport }
            }
            """);
        var sheet = new Dictionary<string, object>();
        SheetEventException Raise(string type) =>
            Assert.Throws<SheetEventException>(() => SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of(type)));

        Assert.Contains("nothing called \"nothing\"", Raise("broken").Message);
        Assert.Contains("there is no script rules.js", Raise("scripted").Message);
        Assert.Contains("has no clocks", Raise("tick").Message);
        Assert.Contains("not a built-in handler", Raise("odd").Message);
        Assert.Empty(SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("unhandled")).Intents);
        Assert.Empty(sheet);
    }

    [Fact]
    public void Formulas_ReadListsInTheEvent()
    {
        var system = System("""{ end: [{ intents: [{ adjust: { track: xp, by: "count(ev.answers, a => a) + (contains(ev.names, 'x') ? 2 : 0)" } }] }] }""");
        var sheet = new Dictionary<string, object>();

        SheetEvents.Raise(system, NullEntryResolver.Instance, sheet,
            SheetEvent.Of("end", ("answers", new List<object> { true, false, true }), ("names", new List<object> { "x", "y", "x" })));

        Assert.Equal(4L, sheet["xp"]);
    }

    [Fact]
    public void TheDamageHandler_TakesTheBestReductionNotTheSum_AndNothingLeftIsNoIntent()
    {
        var system = System("""
            {
              damage: { builtin: damage, track: HP, reduction: { list: dr, amount: amount, bypassedByTag: bypass, onlyTypes: [slashing] },
                        resistance: { property: resist }, nonlethal: { tag: soft, track: xp } }
            }
            """);
        var sheet = new Dictionary<string, object>
        {
            ["HP"] = 10L,
            ["dr"] = new List<object> { new Dictionary<string, object> { ["amount"] = 3L }, new Dictionary<string, object> { ["amount"] = 5L, ["bypass"] = "silver" } },
            ["resist"] = new Dictionary<string, object> { ["fire"] = 5L }
        };
        IReadOnlyList<SheetIntent> Damage(long amount, string type, params string[] tags) =>
            SheetEvents.Handle(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("damage", ("amount", amount), ("damageType", type), ("tags", tags.Cast<object>().ToList())));

        Assert.Equal(-3d, Damage(8, "slashing").Single().Fields["by"]); // 5 is the best, not 3 + 5
        Assert.Equal(-5d, Damage(8, "slashing", "silver").Single().Fields["by"]); // silver gets past the 5
        Assert.Equal(-8d, Damage(8, "piercing").Single().Fields["by"]); // not a type the reduction touches
        Assert.Empty(Damage(4, "fire")); // resisted entirely
        var soft = Damage(9, "bludgeoning", "soft").Single();
        Assert.Equal(("xp", 9d), (soft.Fields["track"], soft.Fields["by"]));
    }

    [Fact]
    public void TheLoader_NamesWhatIsWrongWithHandlersAndTracks()
    {
        var system = System("""
            {
              adjust: [{ intents: [] }],
              a: { builtin: teleport },
              b: [{ intents: [{ explode: { track: HP } }] }, { when: "true" }],
              c: { script: x.js#y },
              d: 3
            }
            """, """
              - kind: other
                tracks:
                  - { key: t, overflow: { into: nowhere, then: vanish } }
                  - { key: u, whenFull: sideways }
            """);

        var problems = SheetEvents.Problems(system);

        Assert.Contains(problems, p => p.Contains("adjust handler never runs"));
        Assert.Contains(problems, p => p.Contains("\"teleport\", which is not a built-in"));
        Assert.Contains(problems, p => p.Contains("\"explode\", which is not an intent"));
        Assert.Contains(problems, p => p.Contains("A b handler line is not { when, intents }"));
        Assert.DoesNotContain(problems, p => p.Contains("The c handler"));
        Assert.Contains(SystemScripts.Problems(system), p => p.Contains("The c handler names the script x.js, which is not in the system's folder"));
        Assert.Contains(problems, p => p.Contains("The d handler is neither"));
        Assert.Contains(problems, p => p.Contains("into \"nowhere\", which is not a track"));
        Assert.Contains(problems, p => p.Contains("not \"vanish\""));
        Assert.Contains(problems, p => p.Contains("has no maximum to overflow"));
        Assert.Contains(problems, p => p.Contains("whenFull is pushUp, not \"sideways\""));
        Assert.Contains(problems, p => p.Contains("whenFull and beyond are for slots in levels"));
        Assert.Empty(SheetEvents.Problems(System()));
    }

    [Fact]
    public void A5eSheet_HasItsHitPointsTemporaryHitPointsAndSlotsAsTracks_SoIntentsActOnThem()
    {
        var srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.RepoRoot, "content", "systems", "srd5e", SystemDefinitionLoader.FileName));
        var sheet = new Dictionary<string, object> { ["HP"] = 7L, ["HP_Max"] = 12L, ["TempHP"] = 3L, ["slots_1"] = 2L, ["slots_1_Max"] = 4L };

        var model = new SheetModel(srd, NullEntryResolver.Instance, sheet);
        Assert.Equal(new[] { "HP", "TempHP", "slots_1" }, model.TrackKeys());
        Assert.Equal((0d, 12d), (model.TrackOf("HP")!.Min!.Value, model.TrackOf("HP")!.Max!.Value));
        Assert.Null(model.TrackOf("TempHP")!.Max);

        var applied = SheetIntents.Apply(srd, NullEntryResolver.Instance, sheet, new[]
        {
            Intent("adjust", ("track", "HP"), ("by", -20)),
            Intent("adjust", ("track", "slots_1"), ("by", 5)),
            Intent("adjust", ("track", "TempHP"), ("by", -1))
        });

        Assert.Equal((0L, 4L, 2L), ((long)sheet["HP"], (long)sheet["slots_1"], (long)sheet["TempHP"]));
        Assert.Empty(applied.Left);
    }
}
