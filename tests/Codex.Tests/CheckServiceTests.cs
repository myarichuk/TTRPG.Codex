using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>H4a (docs/HAZARDS_DESIGN.md section 12): <see cref="CheckService"/> on a runtime with a scripted die. Runs on srd5e.</summary>
[Collection("RavenDb")]
public class CheckServiceTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private const string Gm = "dm-user";

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public CheckServiceTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("CheckService_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    /// <summary>A die that rolls the faces it is told to, in order, and does the sums of the rest (an extra die is a 1).</summary>
    private sealed class ScriptedRoller : IDiceRoller
    {
        public Queue<int> Faces { get; } = new();
        public List<string> Expressions { get; } = new();

        public DiceRollResult Roll(string expression)
        {
            Expressions.Add(expression);
            if (expression.Contains("bad", StringComparison.Ordinal)) throw new FormatException(expression);
            var terms = Regex.Matches(expression, @"[+-]?[^+-]+").Select(m => m.Value).ToList();
            var kept = new List<int>();
            var modifier = 0;
            var total = 0;
            foreach (var (term, index) in terms.Select((t, i) => (t, i)))
            {
                var sign = term.StartsWith('-') ? -1 : 1;
                var body = term.TrimStart('+', '-');
                if (body.Contains('d'))
                {
                    var face = index == 0 ? Faces.Dequeue() : 1;
                    kept.Add(face);
                    total += sign * face;
                }
                else
                {
                    modifier += sign * int.Parse(body, CultureInfo.InvariantCulture);
                    total += sign * int.Parse(body, CultureInfo.InvariantCulture);
                }
            }

            return new DiceRollResult(expression, kept, Array.Empty<int>(), modifier, total) { CheckDieIndex = 0 };
        }
    }

    private static SystemDefinition System(Action<SystemDefinition>? more = null)
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        // Frightened is a valued condition that changes armor class; Guided is a flat tick box here (the shipped one is 1d4).
        system.ConditionEffects["Frightened"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Max = "4" },
            Modifiers = { new ModifierDefinition { Target = "armorClass", Amount = "-1 * value" } }
        };
        more?.Invoke(system);
        return system;
    }

    private static Dictionary<string, object> Hero() => new()
    {
        ["HP"] = 30L, ["HP_Max"] = 30L, ["level"] = 5L, ["acMode"] = "auto",
        ["str"] = 10L, ["dex"] = 16L, ["con"] = 14L, ["int"] = 10L, ["wis"] = 14L, ["cha"] = 10L,
        ["skillProficiencies"] = new List<object> { "Perception" },
        ["saveProficiencies"] = new List<object> { "dex" }
    };

    private sealed record Table(CampaignRuntime Runtime, ScriptedRoller Dice, ActorDocument Hero, ActorDocument Goblin, ActorDocument Third, SessionDocument Session, SystemDefinition System);

    private async Task<Table> TableAsync(SystemDefinition? system = null, Action<ActorDocument>? goblin = null, Action<ActorDocument>? hero = null)
    {
        system ??= System();
        var campaignId = Guid.NewGuid().ToString();
        async Task<ActorDocument> Seed(string name, ActorKind kind, Dictionary<string, object> sheet, Action<ActorDocument>? shape = null)
        {
            var doc = new ActorDocument
            {
                Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = kind,
                OwnerUserId = kind == ActorKind.PlayerCharacter ? "player-1" : null, Sheet = sheet
            };
            shape?.Invoke(doc);
            ActorSheetSync.ApplySheetToState(doc, system, _registry);
            await _actors.SaveAsync(doc);
            return doc;
        }

        var heroDoc = await Seed("Aria", ActorKind.PlayerCharacter, Hero(), hero);
        var goblinDoc = await Seed("Goblin", ActorKind.Monster, new Dictionary<string, object>
        {
            ["HP"] = 7L, ["HP_Max"] = 7L, ["dex"] = 14L, ["wis"] = 8L, ["armorClass"] = 15L, ["skills"] = "History +12, Perception +10",
            // What ActorSheetSync.SeedFromBlueprint writes for any stat block: the printed method and the printed scores as base scores.
            [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod, [SheetRuleEngine.BasePrefix + "dex"] = 14L, [SheetRuleEngine.BasePrefix + "wis"] = 8L
        }, goblin ?? (d => d.BlueprintId = "srd5e:goblin"));
        var thirdDoc = await Seed("Brin", ActorKind.PlayerCharacter, Hero());
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var dice = new ScriptedRoller();
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, system);
        runtime.Hydrate(new[] { heroDoc, goblinDoc, thirdDoc });
        return new Table(runtime, dice, heroDoc, goblinDoc, thirdDoc, session, system);
    }

    private static CheckRequest Skill(ActorDocument actor, string check = "Perception", int? dc = 15) => new() { ActorId = actor.Id, Check = check, Dc = dc };

    /// <summary>Makes the check inside a command, as a hazard's command will, and says what it came to.</summary>
    private static async Task<CheckResult> MakeAsync(Table table, CheckRequest request)
    {
        var command = new MakeCheckCommand(request, Gm);
        await table.Runtime.EnqueueAsync(command);
        return command.Result!;
    }

    // ---- Skill, save, attribute ----

    [Fact]
    public async Task APcsSkillCheck_RollsTheCheckDiePlusTheSkillBonus_AndGradesItAgainstTheDc_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(12);

        var result = await MakeAsync(table, Skill(table.Hero));

        Assert.Equal(CheckFailure.None, result.Failed);
        Assert.Equal(("1d20+5", 12, 17), (table.Dice.Expressions.Single(), result.Natural, result.Total));
        Assert.Equal(new CheckOutcome("success", true), result.Outcome);
        Assert.Equal(CheckBonusSource.Evaluated, result.Bonus!.Source);
        Assert.Equal(new CheckSpec(CheckKind.Skill, "perception", "Perception"), result.Spec);
    }

    [Fact]
    public async Task ACheckBelowTheDc_Fails_AndWithNoDc_IsNotGraded_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(5);
        table.Dice.Faces.Enqueue(5);

        var failed = await MakeAsync(table, Skill(table.Hero));
        var ungraded = await MakeAsync(table, Skill(table.Hero, dc: null));

        Assert.Equal(new CheckOutcome("failure", false), failed.Outcome);
        Assert.Null(ungraded.Outcome);
        Assert.Equal(("failure", null), (table.Session.RollLog[0].Outcome, table.Session.RollLog[1].Outcome));
    }

    [Fact]
    public async Task ASaveAndAnAttributeCheck_UseTheSavingThrowsAndTheModifierPlusCheckBonuses_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(10);
        table.Dice.Faces.Enqueue(10);

        // Dexterity 16 is +3; the save is proficient (+3 at level 5), the check is not.
        var save = await MakeAsync(table, new CheckRequest { ActorId = table.Hero.Id, Check = "Dexterity", Kind = CheckKind.Save, Dc = 15 });
        var check = await MakeAsync(table, new CheckRequest { ActorId = table.Hero.Id, Check = "Dexterity", Dc = 15 });

        Assert.Equal((CheckKind.Save, 16), (save.Spec!.Kind, save.Total));
        Assert.Equal((CheckKind.Attribute, 13), (check.Spec!.Kind, check.Total));
        Assert.Equal(new[] { "1d20+6", "1d20+3" }, table.Dice.Expressions);
        // The same numbers the sheet shows.
        var shown = new SheetRuleEngine(table.System, NullEntryResolver.Instance).EvaluateLive(table.Runtime.LiveSheetOf(table.Hero), null);
        Assert.Equal(shown.SavingThrows.Single(s => s.Key == "dex").Bonus, save.Bonus!.Value);
        Assert.Equal(shown.AttributeModifiers["dex"] + shown.CheckBonuses.GetValueOrDefault("dex"), check.Bonus!.Value);
    }

    // ---- The live sheet ----

    [Fact]
    public async Task ACondition_HeldAtTheTable_NotOnTheStoredSheet_ChangesTheBonus_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(12);
        table.Dice.Faces.Enqueue(12);
        table.Dice.Faces.Enqueue(12);

        var before = await MakeAsync(table, Skill(table.Hero));
        await table.Runtime.EnqueueAsync(new AdjustConditionCommand(table.Hero.Id, "Exhaustion", 2));
        var exhausted = await MakeAsync(table, Skill(table.Hero));
        await table.Runtime.EnqueueAsync(new AdjustConditionCommand(table.Hero.Id, "Exhaustion", -2));
        var recovered = await MakeAsync(table, Skill(table.Hero));

        // Exhaustion level 2 takes 4 off a D20 Test (SRD 5.2.1). At level 0 it takes nothing.
        Assert.Equal(17, before.Total);
        Assert.Equal(13, exhausted.Total);
        Assert.Equal(17, recovered.Total);
        Assert.Equal(-4, exhausted.Bonus!.Value - before.Bonus!.Value);
        Assert.False(table.Hero.Sheet.ContainsKey("condition_Exhaustion") && Convert.ToInt32(table.Hero.Sheet["condition_Exhaustion"], CultureInfo.InvariantCulture) != 0);
    }

    [Fact]
    public async Task ARollerTickedOptIn_IsAdded_AndOneNotTickedIsNot_Async()
    {
        var system = System(s => s.ConditionEffects["Guided"] = new ConditionEffectDefinition
        {
            Modifiers = { new ModifierDefinition { Target = "check", Amount = "3", OptIn = new OptInDefinition { Label = "Guidance" } } }
        });
        var table = await TableAsync(system);
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new AddStatusEffectCommand(table.Hero.Id, "Guided", "dm", 10, EffectExpiry.EndOfTurn, table.Hero.Id));
        var id = new SheetRuleEngine(system, NullEntryResolver.Instance)
            .OptIns(table.Runtime.LiveSheetOf(table.Hero), new SheetRuleEngine(system, NullEntryResolver.Instance).SkillSite("perception")).Single().Id;
        table.Dice.Faces.Enqueue(10);
        table.Dice.Faces.Enqueue(10);

        var plain = await MakeAsync(table, Skill(table.Hero));
        var ticked = await MakeAsync(table, Skill(table.Hero) with { OptIns = new[] { id } });

        Assert.Equal(15, plain.Total);
        Assert.Equal(18, ticked.Total);
        Assert.Equal("1d20+8", table.Dice.Expressions[1]);
        Assert.Contains(ticked.Bonus!.Parts, b => b is { Ticked: true, Amount: 3 });
        Assert.DoesNotContain(plain.Bonus!.Parts, b => b.Ticked);
        Assert.Equal("Perception (+3 Guidance)", ticked.Logged!.Label);
    }

    [Fact]
    public async Task ATickedDiceOptIn_IsRolledWithTheDie_NotFoldedIntoTheNumber_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new AddStatusEffectCommand(table.Hero.Id, "Guided", "dm", 10, EffectExpiry.EndOfTurn, table.Hero.Id));
        var engine = new SheetRuleEngine(table.System, NullEntryResolver.Instance);
        var id = engine.OptIns(table.Runtime.LiveSheetOf(table.Hero), engine.SkillSite("perception")).Single().Id;
        table.Dice.Faces.Enqueue(10);

        var result = await MakeAsync(table, Skill(table.Hero) with { OptIns = new[] { id } });

        Assert.Equal("1d20+5+1d4", table.Dice.Expressions.Single());
        Assert.Equal(5, result.Bonus!.Value);
    }

    // ---- Passive and armor class ----

    [Fact]
    public async Task APassive_RollsNothingAndLogsNothing_AndTotalIsThePassiveScore_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        var result = await MakeAsync(table, Skill(table.Hero) with { Passive = true });

        Assert.Equal((15, null, null, CheckFailure.None), (result.Total, result.Natural, result.Logged, result.Failed));
        Assert.Equal(CheckKind.Passive, result.Spec!.Kind);
        Assert.Equal(new CheckOutcome("success", true), result.Outcome);
        Assert.Empty(table.Dice.Expressions);
        Assert.Empty(table.Session.RollLog);
    }

    [Fact]
    public async Task APassiveTheSheetHasNone_Of_IsUnresolved_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        var result = await MakeAsync(table, Skill(table.Hero, "Stealth") with { Passive = true });

        Assert.Equal(CheckFailure.Unresolved, result.Failed);
        Assert.Empty(table.Dice.Expressions);
        Assert.Empty(table.Session.RollLog);
    }

    [Fact]
    public async Task ArmorClass_OfAPc_IsTheEvaluatedTotal_WithTheTablesConditions_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var plain = table.Runtime.Checks.ArmorClassOf(table.Hero.Id);
        await table.Runtime.EnqueueAsync(new AdjustConditionCommand(table.Hero.Id, "Frightened", 2));
        var frightened = table.Runtime.Checks.ArmorClassOf(table.Hero.Id);

        Assert.Equal((CheckKind.ArmorClass, 13, CheckBonusSource.Evaluated), (plain.Spec!.Kind, plain.Total, plain.Bonus!.Source));
        Assert.Equal(11, frightened.Total);
        Assert.Empty(table.Session.RollLog);
    }

    [Fact]
    public async Task ArmorClass_OfAPrintedCreature_IsThePrintedNumber_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        var goblin = table.Runtime.Checks.ArmorClassOf(table.Goblin.Id);

        Assert.Equal((15, CheckBonusSource.Printed), (goblin.Total, goblin.Bonus!.Source));
        Assert.Equal(CheckFailure.UnknownActor, table.Runtime.Checks.ArmorClassOf("nobody").Failed);
    }

    // ---- Printed creatures and typed bonuses ----

    [Fact]
    public async Task APrintedCreature_NeedsABonus_AndNothingIsRolledOrLogged_UntilOneIsTyped_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(8);

        var bare = await MakeAsync(table, Skill(table.Goblin));
        var typed = await MakeAsync(table, Skill(table.Goblin) with { Bonus = 4 });

        Assert.Equal(CheckFailure.NeedsBonus, bare.Failed);
        Assert.Null(bare.Logged);
        Assert.Equal(("1d20+4", 12, CheckBonusSource.Typed), (table.Dice.Expressions.Single(), typed.Total, typed.Bonus!.Source));
        Assert.Single(table.Session.RollLog);
        // The printed text is only a suggestion for the GM to confirm.
        Assert.Equal(10, ActorChecks.Suggest(table.System, table.Goblin, bare.Spec!));
    }

    [Fact]
    public async Task ATypedBonus_BeatsTheEvaluatedOne_AndRollsAToolTheSystemDoesNotKnow_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(10);
        table.Dice.Faces.Enqueue(10);
        table.Dice.Faces.Enqueue(10);

        var typed = await MakeAsync(table, Skill(table.Hero) with { Bonus = 1 });
        var tools = await MakeAsync(table, Skill(table.Hero, "Thieves' Tools") with { Bonus = 6 });
        var without = await MakeAsync(table, Skill(table.Hero, "Thieves' Tools"));

        Assert.Equal((11, CheckBonusSource.Typed), (typed.Total, typed.Bonus!.Source));
        Assert.Equal((16, "Thieves' Tools"), (tools.Total, tools.Logged!.Label));
        Assert.Equal(CheckFailure.NeedsBonus, without.Failed);
    }

    // ---- Printed creatures, homebrew included ----

    [Fact]
    public async Task AHomebrewCreature_WithNoBlueprintId_IsStillPrinted_AndNeedsABonus_Async()
    {
        var table = await TableAsync(goblin: d => d.BlueprintId = null);
        await using var _ = table.Runtime;

        var skill = await MakeAsync(table, Skill(table.Goblin));
        var save = await MakeAsync(table, new CheckRequest { ActorId = table.Goblin.Id, Check = "Dexterity", Kind = CheckKind.Save, Dc = 12 });

        Assert.Equal((CheckFailure.NeedsBonus, CheckFailure.NeedsBonus), (skill.Failed, save.Failed));
        Assert.Empty(table.Dice.Expressions);
        Assert.Empty(table.Session.RollLog);
    }

    [Fact]
    public async Task APrintedCreature_HasAnEvaluatedAttributeCheck_ButNoSaveOrPassive_WithoutATypedBonus_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(10);

        var attribute = await MakeAsync(table, new CheckRequest { ActorId = table.Goblin.Id, Check = "Dexterity", Dc = 12 });
        var save = await MakeAsync(table, new CheckRequest { ActorId = table.Goblin.Id, Check = "Dexterity", Kind = CheckKind.Save });
        var passive = await MakeAsync(table, Skill(table.Goblin) with { Passive = true });

        Assert.Equal((CheckFailure.None, "1d20+2", CheckBonusSource.Evaluated), (attribute.Failed, table.Dice.Expressions.Single(), attribute.Bonus!.Source));
        Assert.Equal(CheckFailure.NeedsBonus, save.Failed);
        Assert.Equal(CheckFailure.NeedsBonus, passive.Failed);
    }

    // ---- Typed bonuses keep what the table adds ----

    [Fact]
    public async Task AlwaysOnDice_AreRolledWithAnEvaluatedBonus_AndWithATypedOne_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new AddStatusEffectCommand(table.Hero.Id, "Bane", "dm", 10, EffectExpiry.EndOfTurn, table.Hero.Id));
        await table.Runtime.EnqueueAsync(new AddStatusEffectCommand(table.Goblin.Id, "Bane", "dm", 10, EffectExpiry.EndOfTurn, table.Goblin.Id));
        table.Dice.Faces.Enqueue(10);
        table.Dice.Faces.Enqueue(10);
        table.Dice.Faces.Enqueue(10);

        await MakeAsync(table, new CheckRequest { ActorId = table.Hero.Id, Check = "Dexterity", Kind = CheckKind.Save, Dc = 15 });
        await MakeAsync(table, new CheckRequest { ActorId = table.Goblin.Id, Check = "Dexterity", Kind = CheckKind.Save, Dc = 15, Bonus = 2 });
        await MakeAsync(table, new CheckRequest { ActorId = table.Hero.Id, Check = "Dexterity", Kind = CheckKind.Save, Dc = 15, Bonus = 7 });

        Assert.Equal(new[] { "1d20+6-1d4", "1d20+2-1d4", "1d20+7-1d4" }, table.Dice.Expressions);
    }

    [Fact]
    public async Task ATypedBonus_StillCountsATickedFlatOptIn_Async()
    {
        var system = System(s => s.ConditionEffects["Guided"] = new ConditionEffectDefinition
        {
            Modifiers = { new ModifierDefinition { Target = "check", Amount = "3", OptIn = new OptInDefinition { Label = "Guidance" } } }
        });
        var table = await TableAsync(system);
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new AddStatusEffectCommand(table.Hero.Id, "Guided", "dm", 10, EffectExpiry.EndOfTurn, table.Hero.Id));
        var engine = new SheetRuleEngine(system, NullEntryResolver.Instance);
        var id = engine.OptIns(table.Runtime.LiveSheetOf(table.Hero), engine.SkillSite("perception")).Single().Id;
        table.Dice.Faces.Enqueue(10);

        var result = await MakeAsync(table, Skill(table.Hero) with { Bonus = 1, OptIns = new[] { id } });

        Assert.Equal(("1d20+4", CheckBonusSource.Typed), (table.Dice.Expressions.Single(), result.Bonus!.Source));
        Assert.Contains(result.Bonus.Parts, b => b is { Ticked: true, Amount: 3 });
    }

    [Fact]
    public async Task ATypedPassiveOrArmorClass_IsTheScoreItself_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        var passive = await MakeAsync(table, Skill(table.Goblin) with { Passive = true, Bonus = 18 });
        var armor = await MakeAsync(table, new CheckRequest { ActorId = table.Goblin.Id, Check = "AC", Kind = CheckKind.ArmorClass, Dc = 12, Bonus = 12 });

        Assert.Equal((18, CheckBonusSource.Typed, true), (passive.Total, passive.Bonus!.Source, passive.Outcome!.Passed));
        Assert.Equal((12, CheckKind.ArmorClass, true), (armor.Total, armor.Spec!.Kind, armor.Outcome!.Passed));
        Assert.Empty(table.Dice.Expressions);
    }

    [Fact]
    public async Task AManualArmorClass_IsReadFromTheSheet_Async()
    {
        var table = await TableAsync(hero: d => { d.Sheet["acMode"] = "manual"; d.Sheet["armorClass"] = 17L; });
        await using var _ = table.Runtime;

        var result = table.Runtime.Checks.ArmorClassOf(table.Hero.Id);

        Assert.Equal((17, CheckBonusSource.Printed), (result.Total, result.Bonus!.Source));
    }

    [Fact]
    public async Task TheLiveArmorClass_NotTheStoredOne_IsRead_WhenATableStatusChangesIt_Async()
    {
        // Unnerved is a status the table put on the hero, never on the stored sheet: worked out from the stored sheet the armor class is 13, at the table it is 11.
        var system = System(s => s.ConditionEffects["Unnerved"] = new ConditionEffectDefinition
        {
            Modifiers = { new ModifierDefinition { Target = "armorClass", Amount = "-2" } }
        });
        var table = await TableAsync(system);
        await using var _ = table.Runtime;
        var stored = table.Runtime.Checks.ArmorClassOf(table.Hero.Id);
        await table.Runtime.EnqueueAsync(new AddStatusEffectCommand(table.Hero.Id, "Unnerved", "dm", 10, EffectExpiry.EndOfTurn, table.Hero.Id));

        var live = table.Runtime.Checks.ArmorClassOf(table.Hero.Id);

        Assert.DoesNotContain("Unnerved", SheetRuleEngine.RawList(table.Runtime.GetActorDocument(table.Hero.Id)!.Sheet, "conditions"));
        Assert.Equal((13, 11), (stored.Total, live.Total));
    }

    [Fact]
    public async Task AReadThatThrowsInsideTheOptInRead_NeverFailsAMakeAllHalfway_Async()
    {
        // The evaluation works (a missing class entry is no error); the tick-box and always-on-dice read then throws for Aria, whose sheet names a class.
        var resolver = new ThrowsInOptInRead();
        var table = await TableAsync(hero: d => d.Sheet["class"] = "fighter");
        await using var _ = table.Runtime;
        table.Runtime.Resolver = resolver;
        table.Dice.Faces.Enqueue(1);
        table.Dice.Faces.Enqueue(2);
        table.Dice.Faces.Enqueue(3);
        var command = new MakeAllChecks(new[] { Skill(table.Hero), Skill(table.Third), Skill(table.Hero) });

        await table.Runtime.EnqueueAsync(command);

        Assert.Equal(2, resolver.Thrown);
        Assert.Equal(3, command.Results.Count);
        Assert.All(command.Results, r => Assert.Equal(CheckFailure.None, r.Failed));
        Assert.Equal(3, table.Session.RollLog.Count);
        Assert.Single(table.Session.Events, e => e.Type == "DiceRoll");
    }

    /// <summary>Finds nothing, and throws only when asked from the tick-box read, which the evaluation itself never calls.</summary>
    private sealed class ThrowsInOptInRead : IEntryResolver
    {
        public int Thrown { get; private set; }

        public ContentEntry? Resolve(string type, string id)
        {
            if (Environment.StackTrace.Contains("OptInsReaching", StringComparison.Ordinal))
            {
                Thrown++;
                throw new InvalidOperationException("the read fails");
            }

            return null;
        }

        public IEnumerable<ContentEntry> Candidates(string type) => Array.Empty<ContentEntry>();
    }

    // ---- Logging and the loop ----

    [Fact]
    public async Task ASecretCheck_IsASecretRollEntry_ThePlayersNeverGet_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(10);
        table.Dice.Faces.Enqueue(10);

        var secret = await MakeAsync(table, Skill(table.Hero));
        var open = await MakeAsync(table, Skill(table.Hero) with { Secret = false });

        Assert.True(secret.Logged!.IsSecret);
        Assert.False(open.Logged!.IsSecret);
        Assert.Equal((table.Hero.Id, Gm, "Perception", 5, "success"), (secret.Logged.ActorId, secret.Logged.RollerUserId, secret.Logged.Name, secret.Logged.Modifier, secret.Logged.Outcome));
        var seen = new HashSet<string> { table.Hero.Id, table.Goblin.Id, table.Third.Id };
        Assert.Equal(2, TableVisibility.Rolls(table.Runtime.Snapshot.RollLog, isDm: true, seen).Count());
        Assert.Single(TableVisibility.Rolls(table.Runtime.Snapshot.RollLog, isDm: false, seen));
        var stored = await _sessions.GetVisibleAsync(table.Session.Id, new CampaignAccess(table.Session.CampaignId, "player-1", CampaignRole.Player));
        Assert.Single(stored!.RollLog);
        Assert.Equal(2, (await _sessions.GetVisibleAsync(table.Session.Id, new CampaignAccess(table.Session.CampaignId, Gm, CampaignRole.DM)))!.RollLog.Count);
    }

    [Fact]
    public async Task ABadDiceOverride_IsReported_AndTheRuntimeTakesTheNextCommand_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(10);

        var bad = await MakeAsync(table, Skill(table.Hero) with { Dice = "bad" });
        var next = await MakeAsync(table, Skill(table.Hero));

        Assert.Equal((CheckFailure.BadDice, null), (bad.Failed, bad.Logged));
        Assert.Equal(CheckFailure.None, next.Failed);
        Assert.Single(table.Session.RollLog);
    }

    [Fact]
    public async Task ADiceOverride_ReplacesTheCheckDie_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(14);

        var result = await MakeAsync(table, Skill(table.Hero) with { Dice = "2d20kh1" });

        Assert.Equal(("2d20kh1+5", 14), (table.Dice.Expressions.Single(), result.Natural));
    }

    [Fact]
    public async Task MakeAll_GivesTheResultsInOrder_AndOneLogEntryForEachRoll_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(1);
        table.Dice.Faces.Enqueue(2);
        table.Dice.Faces.Enqueue(3);
        var actors = new[] { table.Hero, table.Third, table.Hero };
        var command = new MakeAllChecks(actors.Select(a => Skill(a)).ToList());

        await table.Runtime.EnqueueAsync(command);

        Assert.Equal(new[] { 6, 7, 8 }, command.Results.Select(r => r.Total));
        Assert.Equal(new[] { table.Hero.Id, table.Third.Id, table.Hero.Id }, command.Results.Select(r => r.ActorId));
        Assert.Equal(3, table.Session.RollLog.Count);
        Assert.Single(table.Session.Events, e => e.Type == "DiceRoll");
    }

    /// <summary>What a hazard's command will do: several checks inside one command, one event for them all.</summary>
    private sealed record MakeAllChecks(IReadOnlyList<CheckRequest> Requests) : IRuntimeCommand
    {
        public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();
        public IReadOnlyList<CheckResult> Results { get; private set; } = Array.Empty<CheckResult>();

        public void Apply(CampaignRuntime runtime) => Results = runtime.Checks.MakeAll(Requests, Gm);

        public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("DiceRoll", new[] { EventMessage.Of("Roll.Secret", "hazard", "3 checks") });
    }

    // ---- The command ----

    [Fact]
    public async Task MakeCheckCommand_DescribesItselfWithAGmOnlyPart_WhenSecret_SoPlayersGetNoEvent_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        table.Dice.Faces.Enqueue(12);
        table.Dice.Faces.Enqueue(12);

        await MakeAsync(table, Skill(table.Hero));
        await MakeAsync(table, Skill(table.Hero) with { Secret = false });

        var events = table.Session.Events.Where(e => e.Type == "DiceRoll").ToList();
        Assert.Equal("Aria: Perception 17 vs DC 15, success", events[0].Description);
        Assert.Contains(events[0].Parts.Single().Kinds, k => k.StartsWith("gm:", StringComparison.Ordinal));
        Assert.DoesNotContain(events[1].Parts.Single().Kinds, k => k.StartsWith("gm:", StringComparison.Ordinal));

        var asPlayer = SessionEventRedactor.ForPlayers(events, new[] { table.Hero });
        Assert.Equal("Aria: Perception 17 vs DC 15, success", Assert.Single(asPlayer).Description);
    }

    [Fact]
    public async Task ASecretPassive_NeedsBonus_Unresolved_AndFailedChecks_AreGmOnlyToo_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        await MakeAsync(table, Skill(table.Hero) with { Passive = true });
        await MakeAsync(table, Skill(table.Goblin));
        await MakeAsync(table, Skill(table.Hero, "Stealth") with { Passive = true });
        await MakeAsync(table, Skill(table.Hero) with { Dice = "bad" });

        var events = table.Session.Events.Where(e => e.Type == "DiceRoll").ToList();
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Contains(e.Parts.Single().Kinds, k => k.StartsWith("gm:", StringComparison.Ordinal)));
        Assert.Empty(SessionEventRedactor.ForPlayers(events, new[] { table.Hero, table.Goblin }));
        Assert.Equal("Aria: Stealth has no passive score, so nothing was read.", events[2].Description);
    }

    [Fact]
    public void APlayersOwnLastRoll_IsNeverASecretOneTheGmMadeInBetween()
    {
        var mine = new RollLogEntry { ActorId = "hero", RollerUserId = "player-1", Label = "Perception", Total = 14, Timestamp = DateTime.UtcNow.AddSeconds(-2) };
        var secret = new RollLogEntry { ActorId = "hero", RollerUserId = Gm, Label = "Perception", Total = 3, IsSecret = true, Timestamp = DateTime.UtcNow };
        var open = new RollLogEntry { ActorId = "hero", RollerUserId = Gm, Label = "Perception", Total = 4, Timestamp = DateTime.UtcNow };

        Assert.Same(mine, TableVisibility.OwnLast(new[] { mine, secret }, "hero", "Perception", "player-1"));
        Assert.Same(mine, TableVisibility.OwnLast(new[] { mine, secret, open }, "hero", "Perception", "player-1"));
        Assert.Null(TableVisibility.OwnLast(new[] { secret }, "hero", "Perception", "player-1"));
    }

    [Fact]
    public async Task MakeCheckCommand_SaysPassiveAndNeedsBonusInItsOwnWords_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        await MakeAsync(table, Skill(table.Hero) with { Passive = true, Secret = false });
        await MakeAsync(table, Skill(table.Goblin) with { Secret = false });

        var said = table.Session.Events.Where(e => e.Type == "DiceRoll").Select(e => e.Description).ToList();
        Assert.Equal("Aria: passive Perception 15 vs DC 15", said[0]);
        Assert.Equal("Goblin: Perception needs a bonus typed in, so nothing was rolled.", said[1]);
    }

    [Fact]
    public async Task AnArmorClassRead_SaysArmorClassFromItsOwnString_InEveryLanguage_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        await MakeAsync(table, new CheckRequest { ActorId = table.Hero.Id, Check = ActorChecks.ArmorClassName(table.System), Kind = CheckKind.ArmorClass, Dc = 12, Secret = false });

        var said = Assert.Single(table.Session.Events, e => e.Type == "DiceRoll");
        Assert.Equal("Aria: armor class 13 vs DC 12", said.Description);
        Assert.Contains("класс доспеха 13", said.Text(Codex.Localization.Localizer.For("ru")));
        Assert.Contains("דרגת שריון 13", said.Text(Codex.Localization.Localizer.For("he")));
    }
}
