using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>P4b (a) of docs/CONDITION_IMPLIES_DESIGN.md: <c>implies</c> loads and is checked, expands on read and is never stored, the turn-end drop is a
/// Core rule, and an entry a sheet kind's feature source reads is warned about when it aims at a number only the sheet rules read.</summary>
[Collection("RavenDb")]
public class ConditionImpliesTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private const string Dread = """
        conditions: [Senseless, Dazzled, Rooted, Overwhelmed]
        conditionEffects:
          Senseless:
            implies: [Dazzled, Rooted]
          Overwhelmed:
            implies: [{ condition: Shaken, value: 2 }]
          Dread:
            value: { max: 4, decrease: { on: turnEnd, by: 1 } }
            implies: [{ condition: Shaken, value: value }]
          Shaken:
            value: { max: 4 }
            modifiers:
              - { target: attack, amount: "-value", type: status }
          Chain:
            implies: [Senseless]
        """;

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public ConditionImpliesTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("Implies_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private static SystemDefinition Parse(string body, string head = "") =>
        SystemDefinitionLoader.Parse("id: imply-test\nname: Imply test\n" + head + "\n" + body);

    private static IReadOnlyList<string> Errors(string body, string head = "") =>
        SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Parse(body, head)));

    private static Dictionary<string, object> Sheet(params (string Key, object Value)[] values)
    {
        var sheet = new Dictionary<string, object>();
        foreach (var (key, value) in values) sheet[key] = value;
        return sheet;
    }

    // ---- Load ----

    [Fact]
    public void ANameAndAMapItem_Parse_AndAnImpliesOnlyEntryLoads()
    {
        var system = Parse(Dread);

        var senseless = system.ConditionEffects["Senseless"];
        Assert.Equal(new[] { "Dazzled", "Rooted" }, senseless.Implies.Select(i => i.Condition));
        Assert.All(senseless.Implies, i => Assert.Null(i.Value));
        Assert.Empty(senseless.Modifiers);
        var overwhelmed = Assert.Single(system.ConditionEffects["Overwhelmed"].Implies);
        Assert.Equal(("Shaken", "2"), (overwhelmed.Condition, overwhelmed.Value));
        Assert.Equal("value", system.ConditionEffects["Dread"].Implies[0].Value);
    }

    [Fact]
    public void AnUnknownName_ASelfImply_AndATwiceImplied_AreRefused()
    {
        Assert.Contains(Errors("conditions: [A]\nconditionEffects:\n  A: { implies: [Ghost] }"), e => e.Contains("conditionEffects.A.implies[0]") && e.Contains("'Ghost'"));
        Assert.Contains(Errors("conditions: [A]\nconditionEffects:\n  A: { implies: [A] }"), e => e.Contains("conditionEffects.A.implies[0]") && e.Contains("itself"));
        Assert.Contains(Errors("conditions: [A, B]\nconditionEffects:\n  A: { implies: [B, b] }"), e => e.Contains("conditionEffects.A.implies[1]") && e.Contains("twice"));
    }

    [Fact]
    public void ALoopOfThree_IsRefused_AndTheMessageNamesTheLoop()
    {
        var errors = Errors("conditions: [A, B, C]\nconditionEffects:\n  A: { implies: [B] }\n  B: { implies: [C] }\n  C: { implies: [A] }");

        Assert.Contains(errors, e => e.Contains("A -> B -> C -> A"));
    }

    [Fact]
    public void AValue_NeedsAValuedCondition_NeverExhaustion_ReadsAndStaysWithinTheMaximum()
    {
        var plain = "conditions: [A, B]\nconditionEffects:\n  A: { implies: [{ condition: B, value: 2 }] }";
        Assert.Contains(Errors(plain), e => e.Contains("conditionEffects.A.implies[0]") && e.Contains("no value"));

        var exhaustion = "conditionEffects:\n  A: { implies: [{ condition: Exhaustion, value: 2 }] }\n  Exhaustion: { }";
        Assert.Contains(Errors(exhaustion, "vitals:\n  exhaustion: { key: exhaustion, condition: Exhaustion, maxLevel: 6 }"), e => e.Contains("exhaustion level"));

        Assert.Contains(Errors("conditions: [A]\nconditionEffects:\n  A: { implies: [{ condition: B, value: \"2 +\" }] }\n  B: { value: { max: 4 } }"),
            e => e.Contains("conditionEffects.A.implies[0].value '2 +' cannot be read"));
        Assert.Contains(Errors("conditions: [A]\nconditionEffects:\n  A: { implies: [{ condition: B, value: 9 }] }\n  B: { value: { max: 4 } }"),
            e => e.Contains("above the highest 'B' goes (4)"));
        Assert.Contains(Errors("conditions: [A, B]\nconditionEffects:\n  A: { implies: [{ condition: B, stays: true }] }\n  B: { value: { max: 4 } }"),
            e => e.Contains("'stays' is for a condition with no value"));
    }

    [Fact]
    public void AStay_IsRefusedUnderAValuedSource_AndABadKeyOrBooleanIsReported()
    {
        Assert.Contains(Errors("conditions: [Prone]\nconditionEffects:\n  Dread: { value: { max: 3 }, implies: [{ condition: Prone, stays: true }] }"),
            e => e.Contains("conditionEffects.Dread.implies[0]") && e.Contains("'stays' is not allowed"));
        Assert.Contains(Errors("conditions: [A, B]\nconditionEffects:\n  A: { implies: [{ condition: B, stay: true }] }"), e => e.Contains("'stay' is not a key"));
        Assert.Contains(Errors("conditions: [A, B]\nconditionEffects:\n  A: { implies: [{ condition: B, stays: yes }] }"), e => e.Contains("'stays' is true or false"));
    }

    // ---- Holding ----

    [Fact]
    public void ASource_BringsItsConditions_AndTheRawListHoldsTheSourceAlone()
    {
        var system = Parse(Dread);
        var sheet = Sheet((SheetModel.ConditionsKey, new List<object> { "Senseless" }));

        Assert.Equal(new[] { "Senseless", "Dazzled", "Rooted" }, SheetConditions.Of(system, sheet));
        var live = SheetConditions.LiveView(system, sheet, null, null);
        Assert.Equal(new[] { "Senseless" }, SheetRuleEngine.RawList(live, SheetModel.ConditionsKey));
        var held = SheetConditions.Holding(system, sheet);
        Assert.True(held[0].Listed);
        Assert.False(held[1].Listed);
        Assert.Equal(new[] { "Senseless" }, held[1].From);
    }

    [Fact]
    public void ASourcesValue_ReachesTheImpliedValuedCondition_AndTheHighestCounts()
    {
        var system = Parse(Dread);
        var key = SheetConditions.DefaultKey("Dread");
        var shaken = SheetConditions.DefaultKey("Shaken");

        var three = SheetConditions.Holding(system, Sheet((key, 3L)));
        Assert.Equal(3, three.Single(h => h.Name == "Shaken").Value);
        Assert.False(three.Single(h => h.Name == "Shaken").Listed);

        // Shaken stored at 1 with Dread 3 is held at 3, once.
        var both = SheetConditions.Holding(system, Sheet((key, 3L), (shaken, 1L)));
        var held = Assert.Single(both, h => h.Name == "Shaken");
        Assert.Equal(3, held.Value);
        Assert.True(held.Listed);
        Assert.Equal(new[] { "Dread" }, held.From);

        // A higher stored value wins; Dread dropping to 1 leaves Shaken at 1; Dread at 0 ends it.
        Assert.Equal(4, SheetConditions.Holding(system, Sheet((key, 3L), (shaken, 4L))).Single(h => h.Name == "Shaken").Value);
        Assert.Equal(1, SheetConditions.Holding(system, Sheet((key, 1L))).Single(h => h.Name == "Shaken").Value);
        Assert.DoesNotContain("Shaken", SheetConditions.Of(system, Sheet((key, 0L))));
    }

    [Fact]
    public void AFixedValue_AFormulaClampedToTheMaximum_AndATwoStepChain_Work()
    {
        var system = Parse(Dread);
        Assert.Equal(2, SheetConditions.Holding(system, Sheet((SheetModel.ConditionsKey, new List<object> { "Overwhelmed" }))).Single(h => h.Name == "Shaken").Value);

        var clamped = Parse(Dread.Replace("max: 4, decrease", "max: 6, decrease"));
        Assert.Equal(4, SheetConditions.Holding(clamped, Sheet((SheetConditions.DefaultKey("Dread"), 6L))).Single(h => h.Name == "Shaken").Value);

        var chain = SheetConditions.Of(system, Sheet((SheetModel.ConditionsKey, new List<object> { "Chain" })));
        Assert.Equal(new[] { "Chain", "Senseless", "Dazzled", "Rooted" }, chain);
    }

    [Fact]
    public void AnImpliedValuedCondition_ReachesTheKindsModifiers_AtItsHeldValue()
    {
        var system = Parse("""
            conditions: [Dread]
            sheetKinds:
              - kind: hero
                values:
                  - { key: strike, label: Strike, formula: "10", modifiers: attack }
            conditionEffects:
              Dread:
                value: { max: 4 }
                implies: [{ condition: Shaken, value: value }]
              Shaken:
                value: { max: 4 }
                modifiers:
                  - { target: attack, amount: "-value" }
            """);
        var sheet = Sheet(("kind", "hero"), (SheetConditions.DefaultKey("Dread"), 3L));

        var result = new SheetModel(system, NullEntryResolver.Instance, sheet).Evaluate();

        Assert.Contains("Shaken", result.Conditions);
        Assert.Equal(7d, result.Values["strike"]);
    }

    [Fact]
    public void AnImpliedValue_IsClampedToAMaximumThatIsAFormula_WithoutGoingRound()
    {
        var system = Parse("""
            conditions: [Dread]
            conditionEffects:
              Dread:
                value: { max: 6 }
                implies: [{ condition: Shaken, value: value }]
              Shaken:
                value: { max: level }
            """, "attributes: [{ key: level, default: 3 }]");
        var sheet = Sheet((SheetConditions.DefaultKey("Dread"), 6L));

        Assert.Equal(3, SheetConditions.Holding(system, sheet).Single(h => h.Name == "Shaken").Value);
        // The stepper's maximum is worked out on a model too, and reads what holds: that must stop as well.
        Assert.Equal(3, ConditionLines.Valued(system, sheet).Single(v => v.Name == "Shaken").Max);
        Assert.NotNull(new SheetModel(system, NullEntryResolver.Instance, sheet).Evaluate());
    }

    // ---- The drop ----

    [Fact]
    public void Drops_LowerTheValuesThatFallAtTheMoment_NeverBelowZero()
    {
        var system = Parse(Dread);
        var key = SheetConditions.DefaultKey("Dread");

        var drops = SheetConditions.Drops(system, k => k == key ? 2 : null, "TURNEND");
        Assert.Equal(new ConditionDrop("Dread", key, 2, 1), Assert.Single(drops));
        Assert.Empty(SheetConditions.Drops(system, _ => 0, "turnEnd"));
        Assert.Empty(SheetConditions.Drops(system, _ => 2, "turnStart"));

        var sheet = Sheet((key, 1L));
        SheetConditions.Drop(system, sheet, "turnEnd");
        Assert.Equal(0L, sheet[key]);
        Assert.Empty(SheetConditions.Drop(system, sheet, "turnEnd"));
    }

    // ---- srd5e ----

    [Fact]
    public void Srd5e_ParalyzedPetrifiedStunnedAndUnconscious_ImplyIncapacitated_AndUnconsciousLeavesProne()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

        foreach (var name in new[] { "Paralyzed", "Petrified", "Stunned" })
        {
            Assert.Equal(new[] { "Incapacitated" }, SheetConditions.Of(system, Sheet((SheetModel.ConditionsKey, new List<object> { name }))).Skip(1));
        }

        var unconscious = SheetConditions.Of(system, Sheet((SheetModel.ConditionsKey, new List<object> { "Unconscious" })));
        Assert.Equal(new[] { "Unconscious", "Incapacitated", "Prone" }, unconscious);
        Assert.Equal(new[] { "Prone" }, SheetConditions.Stays(system, "unconscious"));
        Assert.Empty(SheetConditions.Stays(system, "Stunned"));
    }

    // ---- The card line ----

    [Fact]
    public void ALine_SaysWhatBringsIt_AndIsNotListed()
    {
        var system = Parse(Dread);
        var lines = ConditionLines.Holding(system, Sheet((SheetConditions.DefaultKey("Dread"), 3L)));

        var shaken = Assert.Single(lines, l => l.Name == "Shaken");
        Assert.False(shaken.Listed);
        Assert.Equal(3, shaken.Value);
        Assert.Equal(new[] { "Dread" }, shaken.From);
        Assert.True(Assert.Single(lines, l => l.Name == "Dread").Listed);
    }

    // ---- The warning ----

    private static SystemDefinition Degrees() =>
        SystemDefinitionLoader.Load(Path.Combine(Codex.Tests.Reference.ReferenceFixtures.Root, "degrees", "system.yaml"));

    private static ContentEntry Trinket(string target) => new()
    {
        Id = "pebble", Type = "trinket", Name = "Lucky pebble", SystemId = "ref-degrees",
        Properties = new Dictionary<string, object> { ["modifiers"] = new List<object> { new Dictionary<string, object> { ["target"] = target, ["amount"] = "1" } } }
    };

    [Fact]
    public void AKindsFeatureAimedAtASheetRulesNumber_WarnsOnce_AndOneAimedAtTheKindsValueDoesNot()
    {
        var system = Degrees();

        var warnings = SheetConditions.UnreachedKindModifiers(system, Trinket("classDc")).ToList();
        Assert.Equal("Trinket 'Lucky pebble': modifiers[0] aims at 'classDc', which only the sheet rules read; a sheet kind's feature never reaches it, so it counts for nothing.", Assert.Single(warnings));
        Assert.Empty(SheetConditions.UnreachedKindModifiers(system, Trinket("senseDc")));
        // A feat is read by sheetRules, which counts its modifiers.
        var feat = Trinket("classDc");
        feat.Type = "feat";
        Assert.Empty(SheetConditions.UnreachedKindModifiers(system, feat));
    }

    [Theory]
    [InlineData("en", "Trinket 'Lucky pebble': modifiers[0] aims at 'classDc'")]
    [InlineData("ru", "modifiers[0]")]
    [InlineData("he", "modifiers[0]")]
    public void TheWarning_IsSaidInTheReadersLanguage_AndTheEnglishIsTheResourceString(string culture, string start)
    {
        var localizer = Codex.Localization.Localizer.For(culture);

        var said = Assert.Single(SheetConditions.UnreachedKindModifiers(Degrees(), Trinket("classDc"), (key, args) => localizer.Get(key, args)));

        Assert.Contains(start, said);
        Assert.Equal(culture == "en", said == Assert.Single(SheetConditions.UnreachedKindModifiers(Degrees(), Trinket("classDc"))));
        Assert.Equal(string.Format(SheetConditions.UnreachedEnglish, "Trinket", "Lucky pebble", 0, "classDc"), Codex.Localization.Localizer.For("en").Get(SheetConditions.UnreachedKey, new object?[] { "Trinket", "Lucky pebble", 0, "classDc" }));
    }

    [Fact]
    public void ANonHybridSystem_NeverWarns()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

        Assert.Empty(SheetConditions.UnreachedKindModifiers(system, Trinket("ac")));
    }

    // ---- At the table ----

    private async Task<(CampaignRuntime Runtime, ActorDocument Hero, SessionDocument Session)> TableAsync(SystemDefinition system)
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1",
            Sheet = new Dictionary<string, object> { ["HP"] = 20L, ["HP_Max"] = 20L, ["con"] = 14L, ["dex"] = 12L, ["level"] = 5L }
        };
        ActorSheetSync.ApplySheetToState(hero, system, _registry);
        await _actors.SaveAsync(hero);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, null, system);
        runtime.Hydrate(new[] { hero });
        return (runtime, hero, session);
    }

    [Fact]
    public async Task RemovingTheSource_EndsWhatItBrought_NothingImpliedWasStored_AndTheStayRemainsAsAStatus_Async()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var (runtime, hero, session) = await TableAsync(system);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Unconscious", hero.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, hero.Id));

        var state = runtime.Snapshot.Actor(hero.Id)!;
        Assert.Equal(new[] { "Unconscious" }, state.Effects.Select(e => e.EffectId));
        var held = SheetConditions.Of(system, state.LiveView(system, hero.Sheet));
        Assert.Contains("Incapacitated", held);
        Assert.Contains("Prone", held);

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(hero.Id, "Unconscious"));

        state = runtime.Snapshot.Actor(hero.Id)!;
        Assert.Equal(new[] { "Prone" }, state.Effects.Select(e => e.EffectId));
        var after = SheetConditions.Of(system, state.LiveView(system, hero.Sheet));
        Assert.DoesNotContain("Incapacitated", after);
        Assert.Contains("Prone", after);
        Assert.Contains(session.Events, e => e.Type == "StatusEffectRemoved" && e.Description.Contains("Aria lost Unconscious.") && e.Description.Contains("Aria gained Prone."));
    }
}
