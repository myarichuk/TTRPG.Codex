using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// P11b of docs/COMPANIONS_DESIGN.md: the animal companion and familiar sheet kinds in the shipped four-degrees system and its rules-core pack. The expected numbers are worked out here
/// from the book's rules, without reading the system's formulas.
/// </summary>
public class CompanionDataTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core", "four-degrees-rules" };
    private const string BondFeat = "test-familiar-bond";

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public CompanionDataTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The pack, plus one feat of a table's own that adds a familiar ability (the pack's feats are generated and carry no modifiers).</summary>
    private sealed class Resolver(ContentRegistry registry) : IEntryResolver
    {
        private static readonly ContentEntry Bond = new()
        {
            Type = "feat",
            Id = BondFeat,
            Name = "Test bond",
            Properties =
            {
                ["modifiers"] = new List<object> { new Dictionary<string, object> { ["target"] = "familiarAbilities", ["amount"] = "1", ["type"] = "untyped" } }
            }
        };

        public ContentEntry? Resolve(string type, string id) =>
            type == "feat" && id == BondFeat ? Bond : registry.GetEntries(SystemId, type, Packs).FirstOrDefault(e => e.Id == id);

        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(SystemId, type, Packs);
    }

    private sealed class Actors(params (string Id, Dictionary<string, object> Sheet)[] sheets) : ILinkedSheets
    {
        public IReadOnlyDictionary<string, object>? Sheet(string actorId) => sheets.FirstOrDefault(s => s.Id == actorId).Sheet;
    }

    private SheetRuleEngine Engine() => new(_system, new Resolver(_registry));

    private ContentEntry CompanionType(string id) => _registry.GetEntries(SystemId, "companion", Packs).Single(e => e.Id == id);

    private Dictionary<string, object> Master(int level, long dex = 12, long con = 12, long wis = 12, bool bondFeat = false, bool shield = false, params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["kind"] = "character";
        sheet["ancestry"] = "dwarf-ancestry";
        sheet["background"] = "acolyte";
        sheet["class"] = "fighter-class";
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "int", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 12L;
        sheet[SheetRuleEngine.BasePrefix + "dex"] = dex;
        sheet[SheetRuleEngine.BasePrefix + "con"] = con;
        sheet[SheetRuleEngine.BasePrefix + "wis"] = wis;
        if (bondFeat) sheet["feats"] = new List<object> { BondFeat };
        if (shield) sheet["inventory"] = new List<object> { "wooden-shield|equipped:1" };
        foreach (var (key, value) in more) sheet[key] = value;
        Engine().Recompute(sheet); // the rules write the armor class back
        return sheet;
    }

    private static Dictionary<string, object> Companion(string type, string stage = "young", string specialization = "none", params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object>
        {
            ["kind"] = "companion", ["link__owner"] = "owner", ["type"] = new List<object> { type }, ["stage"] = stage, ["specialization"] = specialization
        };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static Dictionary<string, object> Familiar(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "familiar", ["link__master"] = "master" };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static double Value(SheetEvaluation evaluation, string key) => Convert.ToDouble(evaluation.Model!.Values[key], global::System.Globalization.CultureInfo.InvariantCulture);

    // ---- Expected numbers (book pages 207, 212, 213 and 260 of the extracted text) ----

    private sealed record Numbers(int Str, int Dex, int Con, int Int, int Wis, int Cha, int HitPoints, int Ac, int Fort, int Reflex, int Will, int Perception,
        int Attack, int FinesseAttack, int Acrobatics, int Athletics, int Intimidation, int Stealth, int Survival, int Dice, int ExtraDamage);

    /// <summary>What a companion of the type, stage and specialization comes to at a level (a rank is 0 untrained to 4 legendary, worth level + 2 a rank).</summary>
    private Numbers Expected(string type, string stage, string spec, int level)
    {
        var entry = CompanionType(type);
        int P(string key) => Convert.ToInt32(entry.Properties[key], global::System.Globalization.CultureInfo.InvariantCulture);
        var mature = stage != "young";
        var nimble = stage == "nimble";
        var savage = stage == "savage";
        if (!nimble && !savage) spec = "none"; // a specialization counts only on a nimble or savage companion
        var specialized = spec != "none";

        var str = P("str") + (mature ? 1 : 0) + (nimble ? 1 : 0) + (savage ? 2 : 0) + (spec is "bully" or "wrecker" ? 1 : 0);
        var dex = P("dex") + (mature ? 1 : 0) + (nimble ? 2 : 0) + (savage ? 1 : 0) + (specialized ? 1 : 0) + (spec is "ambusher" or "daredevil" ? 1 : 0);
        var con = P("con") + (mature ? 1 : 0) + (nimble ? 1 : 0) + (savage ? 1 : 0) + (spec == "racer" ? 1 : 0);
        var intelligence = P("int") + (specialized ? 2 : 0);
        var wis = P("wis") + (mature ? 1 : 0) + (nimble ? 1 : 0) + (savage ? 1 : 0) + (spec == "tracker" ? 1 : 0);
        var cha = P("cha") + (spec == "bully" ? 3 : 0);

        static int Bonus(int level, int rank) => rank == 0 ? 0 : level + 2 * rank;
        var defenseRank = specialized ? 3 : mature ? 2 : 1;
        var typeSkill = (string)entry.Properties["skill"];

        // Rank of the three skills that grow, by stage and specialization.
        int Rank(string skill, string raisedBy)
        {
            var rank = typeSkill == skill ? (mature ? 2 : 1) : (mature ? 1 : 0);
            return spec == raisedBy ? (rank >= 2 ? 3 : 2) : rank;
        }

        var acrobatics = spec == "daredevil" ? 3 : nimble ? 2 : 1;
        var athletics = spec == "wrecker" ? 3 : spec == "bully" || savage ? 2 : 1;
        var attackRank = specialized ? 2 : 1;

        return new Numbers(str, dex, con, intelligence, wis, cha,
            HitPoints: P("hp") + (6 + con) * level,
            Ac: 10 + dex + Bonus(level, spec is "ambusher" or "daredevil" ? 2 : 1),
            Fort: con + Bonus(level, spec == "racer" ? 4 : defenseRank),
            Reflex: dex + Bonus(level, defenseRank),
            Will: wis + Bonus(level, defenseRank),
            Perception: wis + Bonus(level, defenseRank),
            Attack: str + Bonus(level, attackRank),
            FinesseAttack: Math.Max(str, dex) + Bonus(level, attackRank),
            Acrobatics: dex + Bonus(level, acrobatics),
            Athletics: str + Bonus(level, athletics),
            Intimidation: cha + Bonus(level, Rank("intimidation", "bully")),
            Stealth: dex + Bonus(level, Rank("stealth", "ambusher")),
            Survival: wis + Bonus(level, Rank("survival", "tracker")),
            Dice: 1 + (mature ? 1 : 0) + (specialized ? 1 : 0),
            ExtraDamage: ((nimble ? 2 : 0) + (savage ? 3 : 0)) * (specialized ? 2 : 1));
    }

    private void AssertCompanion(Numbers expected, SheetEvaluation actual)
    {
        Assert.Equal(expected.Str, Value(actual, "str"));
        Assert.Equal(expected.Dex, Value(actual, "dex"));
        Assert.Equal(expected.Con, Value(actual, "con"));
        Assert.Equal(expected.Int, Value(actual, "int"));
        Assert.Equal(expected.Wis, Value(actual, "wis"));
        Assert.Equal(expected.Cha, Value(actual, "cha"));
        Assert.Equal(expected.HitPoints, Value(actual, "HP_Max"));
        Assert.Equal(expected.Ac, Value(actual, "ac"));
        Assert.Equal(expected.Fort, Value(actual, "fortitude"));
        Assert.Equal(expected.Reflex, Value(actual, "reflex"));
        Assert.Equal(expected.Will, Value(actual, "will"));
        Assert.Equal(expected.Perception, Value(actual, "perception"));
        Assert.Equal(expected.Attack, Value(actual, "attack"));
        Assert.Equal(expected.FinesseAttack, Value(actual, "attackFinesse"));
        Assert.Equal(expected.Acrobatics, Value(actual, "acrobatics"));
        Assert.Equal(expected.Athletics, Value(actual, "athletics"));
        Assert.Equal(expected.Intimidation, Value(actual, "intimidation"));
        Assert.Equal(expected.Stealth, Value(actual, "stealth"));
        Assert.Equal(expected.Survival, Value(actual, "survival"));
        Assert.Equal(expected.Dice, Value(actual, "damageDice"));
        Assert.Equal(expected.ExtraDamage, Value(actual, "damageExtra"));
    }

    // ---- Load ----

    [Fact]
    public void FourDegrees_LoadsWithBothKinds_AsPlayerOwnedCreaturesThatAreNotThePartyAndLinkToTheCharacter()
    {
        Assert.Empty(SystemDefinitionLoader.Validate(_system));
        Assert.Equal(new[] { "character", "companion", "familiar" }, _system.SheetKinds.Select(k => k.Kind)); // a new player character is still a character by default

        var companion = _system.SheetKinds.Single(k => k.Kind == "companion");
        var familiar = _system.SheetKinds.Single(k => k.Kind == "familiar");
        foreach (var kind in new[] { companion, familiar })
        {
            Assert.Equal("player", kind.Role);
            Assert.False(kind.PartyMember);
            Assert.Equal("character", Assert.Single(kind.Links).Kind);
            var track = Assert.Single(kind.Tracks);
            Assert.Equal(_system.Resources.Single(r => r.IsPrimary).Key, track.Key); // "HP": the key the down rule and the hit point bar read
            Assert.Equal("number", track.Display);
            Assert.NotEmpty(kind.Layout);
        }

        Assert.Equal("owner", companion.Links[0].Key);
        Assert.Equal("master", familiar.Links[0].Key);
        Assert.Equal("companion", companion.Lists.Single(l => l.Key == "type").Type);
        Assert.Equal("familiar-ability", familiar.Lists.Single(l => l.Key == "abilities").Type);
    }

    [Fact]
    public void ACompanionSheet_GetsNoAncestryClassBoostsOrCharacterHitPoints()
    {
        var sheet = Companion("wolf");
        var evaluation = Engine().Recompute(sheet, new Actors(("owner", Master(5))));

        Assert.False(SheetRuleEngine.AppliesTo(_system, sheet));
        Assert.Null(evaluation.HitPoints);
        Assert.Null(evaluation.ArmorClass);
        Assert.Empty(evaluation.Skills);
        Assert.Empty(evaluation.SavingThrows);
        Assert.Empty(evaluation.Classes);
        Assert.DoesNotContain("familiarAbilities", evaluation.Derived.Keys); // not the character's derived number
        Assert.False(sheet.ContainsKey("armorClass") || sheet.ContainsKey("acMode") || sheet.ContainsKey(SheetRuleEngine.MethodKey) || sheet.ContainsKey("ancestry"));
        // Its hit points are its own track: the maximum is written and a new sheet starts full.
        Assert.Equal(Value(evaluation, "HP_Max"), Convert.ToDouble(sheet["HP"]));
    }

    // ---- The companion ----

    [Fact]
    public void AYoungWolf_AtFirstLevel_HasTheNumbersOfTheYoungRules()
    {
        // Numbers from the wolf entry of the pack.
        var evaluation = Engine().Recompute(Companion("wolf"), new Actors(("owner", Master(1))));

        Assert.Equal(1, Value(evaluation, "level"));
        Assert.Equal(6 + (6 + 2) * 1, Value(evaluation, "HP_Max")); // type + (6 + Con) a level
        Assert.Equal(10 + 3 + 3, Value(evaluation, "ac"));          // 10 + Dex + (level + 2)
        Assert.Equal(2 + 3, Value(evaluation, "fortitude"));
        Assert.Equal(3 + 3, Value(evaluation, "reflex"));
        Assert.Equal(1 + 3, Value(evaluation, "will"));
        Assert.Equal(1 + 3, Value(evaluation, "perception"));
        Assert.Equal(2 + 3, Value(evaluation, "attack"));
        Assert.Equal(3 + 3, Value(evaluation, "attackFinesse"));
        Assert.Equal(3 + 3, Value(evaluation, "acrobatics"));
        Assert.Equal(2 + 3, Value(evaluation, "athletics"));
        Assert.Equal(0 + 0, Value(evaluation, "intimidation")); // untrained: nothing added
        Assert.Equal(3 + 0, Value(evaluation, "stealth"));
        Assert.Equal(1 + 3, Value(evaluation, "survival"));
    }

    [Theory]
    [InlineData("wolf", "young", "none", 1)]
    [InlineData("wolf", "mature", "none", 5)]
    [InlineData("ape", "nimble", "none", 7)]
    [InlineData("bear", "savage", "none", 9)]
    [InlineData("ape", "savage", "bully", 9)]
    [InlineData("snake", "nimble", "ambusher", 11)]
    [InlineData("wolf", "nimble", "tracker", 13)]
    [InlineData("badger", "savage", "wrecker", 15)]
    [InlineData("cat", "nimble", "daredevil", 12)]
    [InlineData("horse", "savage", "racer", 20)]
    [InlineData("shark", "nimble", "ambusher", 6)]
    [InlineData("shark", "mature", "ambusher", 6)] // no benefit before nimble or savage
    [InlineData("wolf", "young", "racer", 3)]
    public void ACompanionsNumbers_FollowItsTypeItsStageItsSpecializationAndItsOwnersLevel(string type, string stage, string spec, int level)
    {
        var evaluation = Engine().Recompute(Companion(type, stage, spec), new Actors(("owner", Master(level))));

        Assert.Equal(level, Value(evaluation, "level"));
        AssertCompanion(Expected(type, stage, spec, level), evaluation);
    }

    [Fact]
    public void ChangingTheOwnersLevel_MovesTheCompanion()
    {
        var owner = Master(3);
        var sheet = Companion("wolf", "mature");
        var before = Engine().Recompute(sheet, new Actors(("owner", owner)));
        Assert.Equal(3, Value(before, "level"));

        owner["level"] = 6L;
        var after = Engine().Recompute(sheet, new Actors(("owner", owner)));

        Assert.Equal(6, Value(after, "level"));
        AssertCompanion(Expected("wolf", "mature", "none", 6), after);
        Assert.Equal(Value(before, "HP_Max") + 3 * (6 + 3), Value(after, "HP_Max")); // 6 + Con (+3 when mature) for each level more
        Assert.Equal(Value(before, "ac") + 3, Value(after, "ac"));
    }

    [Fact]
    public void ACompanionWithNoOwnerSheetSupplied_HasAnUnknownLink_AndKeepsItsPicksStageAndHitPoints()
    {
        var sheet = Companion("wolf", "nimble", "none", ("HP", 4L));
        var evaluation = Engine().Recompute(sheet); // the host gave no sheet for the link

        Assert.True(evaluation.LinksUnknown);
        Assert.Equal(4L, sheet["HP"]); // not clamped or reset while the maximum is unknown
        Assert.Equal("nimble", sheet["stage"]);
        Assert.Equal(new List<object> { "wolf" }, sheet["type"]);
    }

    [Fact]
    public void ACompanionWithTwoTypes_IsReported_AndWithNoneItsTypeNumbersAreLeftOutWithAWarning()
    {
        var two = Companion("wolf");
        two["type"] = new List<object> { "wolf", "ape" };
        var linked = new Actors(("owner", Master(2)));
        Assert.Contains(Engine().Evaluate(two, linked).Violations, v => v.Message.Contains("one type"));
        Assert.DoesNotContain(Engine().Evaluate(Companion("wolf"), linked).Violations, v => v.Message.Contains("one type"));

        var none = Companion("wolf");
        none["type"] = new List<object>();
        var evaluation = Engine().Recompute(none, linked);
        // Nothing the type decides can be worked out, and the sheet says so; the level (the owner's) still reads.
        Assert.Equal(2, Value(evaluation, "level"));
        Assert.DoesNotContain("ac", evaluation.Model!.Values.Keys);
        Assert.DoesNotContain("HP_Max", evaluation.Model.Values.Keys);
        Assert.NotEmpty(evaluation.Model.Warnings);
    }

    // ---- Conditions ----

    [Fact]
    public void Frightened_OnACompanion_LowersItsChecksAndArmorClassOnce()
    {
        var owner = new Actors(("owner", Master(5)));
        var calm = Engine().Evaluate(Companion("wolf", "mature"), owner);
        var frightened = Engine().Evaluate(Companion("wolf", "mature", "none", ("condition_frightened", 2L)), owner);

        foreach (var key in new[] { "ac", "fortitude", "reflex", "will", "perception", "attack", "attackFinesse", "acrobatics", "athletics", "intimidation", "stealth", "survival" })
        {
            Assert.Equal(Value(calm, key) - 2, Value(frightened, key));
        }

        Assert.Equal(Value(calm, "str"), Value(frightened, "str")); // an attribute is no check
        Assert.Equal(Value(calm, "HP_Max"), Value(frightened, "HP_Max"));
    }

    [Fact]
    public void ConditionsThatTheCompanionHasNoTargetFor_ReachNothingAndDoNotThrow()
    {
        var owner = new Actors(("owner", Master(5)));
        var calm = Engine().Evaluate(Companion("wolf"), owner);
        // Sickened, drained and stupefied aim at targets the character rules own (armorClass, classDc, spellDc, HP_Max...) or at ones the kind may lack.
        var worn = Engine().Evaluate(Companion("wolf", "young", "none", ("condition_sickened", 1L), ("condition_stupefied", 2L), ("condition_drained", 1L), ("condition_enfeebled", 1L)), owner);

        Assert.Equal(Value(calm, "ac") - 1, Value(worn, "ac"));
        Assert.Equal(Value(calm, "will") - 2, Value(worn, "will")); // sickened 1 and stupefied 2 are both status penalties: the worse counts
        Assert.Equal(Value(calm, "athletics") - 1, Value(worn, "athletics")); // enfeebled 1, sickened 1: one status penalty
        Assert.Equal(Value(calm, "HP_Max") - 5, Value(worn, "HP_Max"));        // drained lowers the maximum by its value times the level
    }

    // ---- The familiar ----

    [Fact]
    public void AFamiliar_TakesItsLevelArmorClassAndSavesFromItsMaster_AndItsHitPointsFromTheLevel()
    {
        var master = Master(5, dex: 16, con: 14, wis: 10);
        var worked = Engine().Recompute(master);
        var familiar = Engine().Recompute(Familiar(), new Actors(("master", master)));

        Assert.Equal(5, Value(familiar, "level"));
        Assert.Equal(Convert.ToDouble(master["armorClass"]), Value(familiar, "ac"));
        Assert.Equal(worked.SavingThrows.Single(s => s.Key == "con").Bonus, Value(familiar, "fortitude"));
        Assert.Equal(worked.SavingThrows.Single(s => s.Key == "dex").Bonus, Value(familiar, "reflex"));
        Assert.Equal(worked.SavingThrows.Single(s => s.Key == "wis").Bonus, Value(familiar, "will"));
        Assert.NotEqual(0, Value(familiar, "fortitude"));
        Assert.Equal(5 * 5, Value(familiar, "HP_Max")); // 5 a level
        Assert.Equal(2, Value(familiar, "abilityCount"));
        Assert.Equal(5 + 3, Value(familiar, "perception"));
        Assert.Equal(5 + 3, Value(familiar, "acrobatics"));
        Assert.Equal(5 + 3, Value(familiar, "stealth"));
        Assert.Equal(5, Value(familiar, "skills"));
    }

    [Fact]
    public void AnArmorOrLevelChangeOnTheMaster_MovesTheFamiliarsArmorClassAndSaves()
    {
        var master = Master(5, dex: 10, con: 10);
        var first = Engine().Recompute(Familiar(), new Actors(("master", master)));

        master[SheetRuleEngine.BasePrefix + "dex"] = 18L;
        master[SheetRuleEngine.BasePrefix + "con"] = 16L;
        Engine().Recompute(master);
        var second = Engine().Recompute(Familiar(), new Actors(("master", master)));
        Assert.Equal(Convert.ToDouble(master["armorClass"]), Value(second, "ac"));
        Assert.True(Value(second, "ac") > Value(first, "ac"));
        Assert.True(Value(second, "reflex") > Value(first, "reflex"));
        Assert.True(Value(second, "fortitude") > Value(first, "fortitude"));

        master["level"] = 8L;
        Engine().Recompute(master);
        var third = Engine().Recompute(Familiar(), new Actors(("master", master)));
        Assert.Equal(8, Value(third, "level"));
        Assert.Equal(8 * 5, Value(third, "HP_Max"));
        Assert.Equal(Convert.ToDouble(master["armorClass"]), Value(third, "ac"));
        Assert.True(Value(third, "will") > Value(second, "will"));
    }

    [Fact]
    public void AFamiliarsPerceptionAcrobaticsAndStealth_UseTheMastersSpellcastingModifierWhenItIsHigher()
    {
        var master = new Actors(("master", Master(4)));
        Assert.Equal(4 + 3, Value(Engine().Recompute(Familiar(("casterModifier", 2L)), master), "perception"));
        var higher = Engine().Recompute(Familiar(("casterModifier", 5L)), master);
        Assert.Equal(4 + 5, Value(higher, "perception"));
        Assert.Equal(4 + 5, Value(higher, "acrobatics"));
        Assert.Equal(4 + 5, Value(higher, "stealth"));
        Assert.Equal(4, Value(higher, "skills"));
    }

    [Fact]
    public void TheToughPetAbility_AddsTwoHitPointsALevel()
    {
        var evaluation = Engine().Recompute(Familiar(("tough", "yes")), new Actors(("master", Master(6))));
        Assert.Equal(6 * (5 + 2), Value(evaluation, "HP_Max"));
    }

    [Fact]
    public void Frightened_OnAFamiliar_LowersItsChecksAndArmorClassOnce_AndNotItsHitPoints()
    {
        var master = new Actors(("master", Master(5)));
        var calm = Engine().Evaluate(Familiar(), master);
        var frightened = Engine().Evaluate(Familiar(("condition_frightened", 3L)), master);

        foreach (var key in new[] { "ac", "fortitude", "reflex", "will", "perception", "acrobatics", "stealth", "skills" })
        {
            Assert.Equal(Value(calm, key) - 3, Value(frightened, key));
        }

        Assert.Equal(Value(calm, "HP_Max"), Value(frightened, "HP_Max"));
    }

    [Fact]
    public void AFamiliarCanPickNoMoreAbilitiesThanItsCount()
    {
        var abilities = _registry.GetEntries(SystemId, "familiar-ability", Packs).Select(e => (object)e.Id).Take(4).ToList();
        var master = new Actors(("master", Master(5)));

        Assert.DoesNotContain(Engine().Evaluate(Familiar(("abilities", abilities.Take(2).ToList())), master).Violations, v => v.Key == "Actor.Rule.FamiliarAbilities");
        var over = Engine().Evaluate(Familiar(("abilities", abilities.Take(3).ToList())), master).Violations.Single(v => v.Key == "Actor.Rule.FamiliarAbilities");
        Assert.Equal("more abilities than 2 (tough counts as one).", over.Message);
    }

    [Fact]
    public void TheToughAbility_CountsAgainstTheFamiliarsAbilityLimit()
    {
        var abilities = _registry.GetEntries(SystemId, "familiar-ability", Packs).Select(e => (object)e.Id).Take(2).ToList();
        var master = new Actors(("master", Master(5)));

        Assert.DoesNotContain(Engine().Evaluate(Familiar(("abilities", abilities)), master).Violations, v => v.Key == "Actor.Rule.FamiliarAbilities");
        Assert.DoesNotContain(Engine().Evaluate(Familiar(("abilities", abilities.Take(1).ToList()), ("tough", "yes")), master).Violations, v => v.Key == "Actor.Rule.FamiliarAbilities");
        Assert.Contains(Engine().Evaluate(Familiar(("abilities", abilities), ("tough", "yes")), master).Violations, v => v.Key == "Actor.Rule.FamiliarAbilities");
        // A value that is not yes is no tough ability.
        Assert.DoesNotContain(Engine().Evaluate(Familiar(("abilities", abilities), ("tough", "maybe")), master).Violations, v => v.Key == "Actor.Rule.FamiliarAbilities");
        Assert.Equal(5 * 5, Value(Engine().Recompute(Familiar(("tough", "maybe")), master), "HP_Max"));
    }

    [Fact]
    public void AFamiliarsArmorClassAndSaves_AreItsMastersBeforeConditionsAndShield_AndItsOwnConditionsApplyOnce()
    {
        var calm = Master(5);
        var scared = Master(5, more: ("condition_frightened", 2L));
        var shielded = Master(5, shield: true);
        Assert.Equal(Convert.ToInt64(calm["armorClass"]) - 2, Convert.ToInt64(scared["armorClass"]));    // the master's own sheet shows both changes
        Assert.Equal(Convert.ToInt64(calm["armorClass"]) + 2, Convert.ToInt64(shielded["armorClass"]));

        var baseline = Engine().Evaluate(Familiar(), new Actors(("master", calm)));
        foreach (var master in new[] { scared, shielded })
        {
            var familiar = Engine().Evaluate(Familiar(), new Actors(("master", master)));
            foreach (var key in new[] { "ac", "fortitude", "reflex", "will" }) Assert.Equal(Value(baseline, key), Value(familiar, key));
        }

        // The familiar's own frightened lowers them once, whatever its master holds.
        var both = Engine().Evaluate(Familiar(("condition_frightened", 2L)), new Actors(("master", scared)));
        foreach (var key in new[] { "ac", "fortitude", "reflex", "will" }) Assert.Equal(Value(baseline, key) - 2, Value(both, key));
        Assert.Equal(Convert.ToDouble(calm["armorClass"]), Value(baseline, "ac"));
    }

    [Fact]
    public void ASpecializationOnAYoungOrMatureCompanion_GivesNothing_AndIsReported()
    {
        var owner = new Actors(("owner", Master(9)));
        foreach (var stage in new[] { "young", "mature" })
        {
            var plain = Engine().Evaluate(Companion("snake", stage), owner);
            var special = Engine().Evaluate(Companion("snake", stage, "ambusher"), owner);
            foreach (var key in new[] { "str", "dex", "int", "ac", "perception", "attack", "stealth", "damageDice", "HP_Max" }) Assert.Equal(Value(plain, key), Value(special, key));
            Assert.Contains(special.Violations, v => v.Key == "Actor.Rule.CompanionSpecialization");
            Assert.DoesNotContain(plain.Violations, v => v.Key == "Actor.Rule.CompanionSpecialization");
        }

        var nimble = Engine().Evaluate(Companion("snake", "nimble", "ambusher"), owner);
        Assert.DoesNotContain(nimble.Violations, v => v.Key == "Actor.Rule.CompanionSpecialization");
        Assert.True(Value(nimble, "int") > Value(Engine().Evaluate(Companion("snake", "nimble"), owner), "int"));

        // A stage or specialization the system does not name is young, and none.
        var odd = Engine().Evaluate(Companion("snake", "elder", "mystic"), owner);
        AssertCompanion(Expected("snake", "young", "none", 9), odd);
    }

    [Fact]
    public void TheCompanionsStrikeRoll_ReadsNoStrikeModifiers_SoEachConditionCountsOnce()
    {
        var layout = _system.SheetKinds.Single(k => k.Kind == "companion").Layout.SelectMany(s => s.Widgets).Single(w => w.Widget == "roll" && w.Roll != "degreeCheck");
        var roll = _system.Rolls.Single(r => r.Key == layout.Roll);
        Assert.DoesNotContain(roll.Add, a => a.Contains("modifiers('strike')", StringComparison.Ordinal));
        // The ordinary strike roll still reads them (characters), and the companion's displayed attack values carry the conditions once.
        Assert.Contains(_system.Rolls.Single(r => r.Key == "strike").Add, a => a.Contains("modifiers('strike')", StringComparison.Ordinal));
        var calm = Engine().Evaluate(Companion("wolf"), new Actors(("owner", Master(5))));
        var scared = Engine().Evaluate(Companion("wolf", "young", "none", ("condition_frightened", 2L), ("conditions", new List<object> { "Prone" })), new Actors(("owner", Master(5))));
        Assert.Equal(Value(calm, "attack") - 4, Value(scared, "attack")); // a status penalty of 2 and a circumstance penalty of 2, each once
    }

    [Fact]
    public void AFeatOnTheMasterThatRaisesTheCount_LetsTheFamiliarPickOneMore()
    {
        var abilities = _registry.GetEntries(SystemId, "familiar-ability", Packs).Select(e => (object)e.Id).Take(3).ToList();
        var plain = Master(5);
        var bonded = Master(5, bondFeat: true);
        var picks = Familiar(("abilities", abilities));

        // The same three picks: too many for a master without the feat, fine for one with it.
        Assert.Contains(Engine().Evaluate(picks, new Actors(("master", plain))).Violations, v => v.Key == "Actor.Rule.FamiliarAbilities");
        var allowed = Engine().Evaluate(picks, new Actors(("master", bonded)));
        Assert.DoesNotContain(allowed.Violations, v => v.Key == "Actor.Rule.FamiliarAbilities");
        Assert.Equal(3, Value(allowed, "abilityCount"));
        Assert.Equal(1, Engine().Recompute(bonded).Derived["familiarAbilities"] is long n ? n : -1);
        Assert.Equal(0, Engine().Recompute(plain).Derived["familiarAbilities"] is long m ? m : -1);
    }

    [Fact]
    public void TheFamiliarsPicksHold_WhenNoMasterSheetIsSupplied()
    {
        var abilities = _registry.GetEntries(SystemId, "familiar-ability", Packs).Select(e => (object)e.Id).Take(3).ToList();
        var sheet = Familiar(("abilities", abilities), ("HP", 7L));
        var evaluation = Engine().Recompute(sheet);

        Assert.True(evaluation.LinksUnknown);
        Assert.Equal(abilities, sheet["abilities"]);
        Assert.Equal(7L, sheet["HP"]);
        Assert.DoesNotContain(evaluation.Violations, v => v.Key == "Actor.Rule.FamiliarAbilities"); // the count is not judged while the master is unknown
    }

    // ---- P11c: the owner's sheet, a companion's damage, the familiar's list moment, the hidden number ----

    [Fact]
    public void TheCharactersSheet_EndsWithAMembersWidget_AndTheKindsWithTheirOwnLayoutAreUnchanged()
    {
        // A character saved with no kind key is the system's first kind, so it gets the same sheet.
        foreach (var sheet in new[] { new Dictionary<string, object> { ["kind"] = "character" }, new Dictionary<string, object>() })
        {
            var widgets = Assert.Single(SheetLayout.For(_system, sheet)).Widgets;
            Assert.Equal("members", widgets[^1].Widget);
            Assert.Equal(1, widgets.Count(w => w.Widget == "members"));
        }

        foreach (var kind in new[] { "companion", "familiar" })
        {
            var layout = SheetLayout.For(_system, new Dictionary<string, object> { ["kind"] = kind });
            Assert.DoesNotContain(layout.SelectMany(s => s.Widgets), w => w.Widget == "members");
        }

        Assert.Empty(SheetLayout.Problems(_system));
    }

    [Theory]
    [InlineData("young", "none", 0)]
    [InlineData("mature", "none", 1)]
    [InlineData("nimble", "none", 4)]   // Strength +2 (grown, nimble), extra damage 2
    [InlineData("savage", "none", 6)]   // Strength +3 (grown, savage twice), extra damage 3
    [InlineData("savage", "bully", 10)] // Strength +4, extra damage 3 doubled by the specialization
    public void ACompanionsDamageBonus_IsItsStrengthPlusItsExtraDamage(string stage, string specialization, int aboveTheTypesStrength)
    {
        var evaluation = Engine().Recompute(Companion("wolf", stage, specialization), new Actors(("owner", Master(9))));
        var typeStrength = Convert.ToInt32(CompanionType("wolf").Properties["str"], global::System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(typeStrength + aboveTheTypesStrength, Value(evaluation, "damageBonus"));
        Assert.Equal(Value(evaluation, "str") + Value(evaluation, "damageExtra"), Value(evaluation, "damageBonus"));
    }

    [Fact]
    public void ACompanionsSheet_ShowsItsDamageBonusWithItsAttacks()
    {
        var attacks = _system.SheetKinds.Single(k => k.Kind == "companion").Layout.Single(s => s.Section == "Attacks");
        Assert.Contains("damageBonus", attacks.Widgets.SelectMany(w => w.Keys));
    }

    [Fact]
    public void TheFamiliarsAbilityList_NamesTheDailyPreparations_AndTheSheetKeysSayWhoOffersThem()
    {
        var list = _system.SheetKinds.Single(k => k.Kind == "familiar").Lists.Single(l => l.Key == "abilities");
        Assert.Equal(new[] { "dailyPrep" }, list.ChangesOn);

        var picks = _registry.GetEntries(SystemId, "familiar-ability", Packs).Select(e => (object)e.Id).Take(2).ToList();
        var sheet = Familiar(("abilities", picks));
        Assert.False(SystemMoments.Matters(_system, sheet, "dailyPrep")); // nothing yet tells the table

        Engine().Recompute(sheet, new Actors(("master", Master(5))));

        Assert.Equal("dailyPrep", sheet["changes_on__abilities"]);
        Assert.Equal("Abilities", sheet["changes_on__abilities_Label"]);
        Assert.Equal(new[] { ("abilities", "Abilities") }, SheetRuleEngine.ChoicesToChangeAfter(sheet, "dailyPrep"));
        Assert.Empty(SheetRuleEngine.ChoicesToChangeAfter(sheet, "sessionStart"));
        // A familiar is not a party member, and the moment still reaches it through its own keys.
        Assert.True(SystemMoments.Matters(_system, sheet, "dailyPrep"));
        Assert.True(SystemMoments.ReachesPartyMember(_system, sheet, "dailyPrep"));
        Assert.False(SystemMoments.ReachesPartyMember(_system, sheet, "sessionStart"));
        Assert.Equal(picks, sheet["abilities"]); // the picks are guidance, never cleared by a recompute
    }

    [Fact]
    public void ThePartyBarsLinkToTheFamiliarsList_ResolvesToTheList_AndTheTabThatShowsIt()
    {
        var sheet = Familiar();
        var list = LinkWidgets.ListAnchoredAt(_system, sheet, "choice-abilities");
        Assert.Equal("abilities", list?.Key);
        Assert.Equal("Abilities", LinkWidgets.TabOfList(SheetLayout.For(_system, sheet), list!.Key));

        // Only a list with a changesOn is a target; a list without one, and a name that is none, are not.
        Assert.Null(LinkWidgets.ListAnchoredAt(_system, sheet, "choice-nothing"));
        Assert.Null(LinkWidgets.ListAnchoredAt(_system, new Dictionary<string, object> { ["kind"] = "character" }, "choice-trinkets"));
    }

    [Fact]
    public void ACharacter_HasNoListMoment_AndTheExtraFamiliarAbilitiesNumberIsHiddenButStillRead()
    {
        var derived = _system.SheetRules!.Derived.Single(d => d.Key == "familiarAbilities");
        Assert.True(derived.Hidden);
        var plain = Master(5); // Master recomputes the sheet, which is where the keys would be written
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith(SheetRuleEngine.ChangesOnPrefix, StringComparison.Ordinal));
        Engine().Recompute(plain);
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith(SheetRuleEngine.ChangesOnPrefix, StringComparison.Ordinal));

        // Hidden from the sheet only: the evaluation holds it, and the familiar reads it through the link.
        var bonded = Master(5, bondFeat: true);
        Assert.Equal(1L, Engine().Evaluate(bonded).Derived["familiarAbilities"]);
        Assert.Equal(3, Value(Engine().Evaluate(Familiar(), new Actors(("master", bonded))), "abilityCount"));
    }

    // ---- Strings ----

    private static IEnumerable<string> Labels(SheetKindDefinition kind)
    {
        yield return kind.Label;
        foreach (var link in kind.Links) yield return link.Label;
        foreach (var list in kind.Lists)
        {
            yield return list.Label;
            if (!string.IsNullOrWhiteSpace(list.Description)) yield return list.Description!;
        }

        // Only what the layout shows: the helper values the formulas read are never on screen.
        var shown = kind.Layout.SelectMany(s => s.Widgets).SelectMany(w => w.Keys).ToHashSet();
        foreach (var value in kind.Values.Where(v => shown.Contains(v.Key)))
        {
            yield return value.Label ?? value.Key;
            foreach (var option in value.Options ?? new List<string>()) yield return option;
        }

        foreach (var track in kind.Tracks) yield return track.Label ?? track.Key;
        foreach (var section in kind.Layout) yield return section.Section;
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void EveryLabelOfTheNewKinds_IsTranslated(string language)
    {
        var values = _system.Translations[language].Values;
        var missing = _system.SheetKinds.Where(k => k.Kind != "character").SelectMany(Labels).Distinct().Where(l => !values.ContainsKey(l)).ToList();
        Assert.Empty(missing);
        Assert.True(_system.Translations[language].SheetRules.Derived.ContainsKey("familiarAbilities"));
    }

    [Fact]
    public void TheFamiliarsLink_IsNotTheWordForTheGameMaster()
    {
        var link = _system.SheetKinds.Single(k => k.Kind == "familiar").Links.Single().Label;
        Assert.DoesNotContain("master", link, StringComparison.OrdinalIgnoreCase); // "Master" is the game master's word in two languages
        Assert.DoesNotContain("мастер", _system.Translations["ru"].Values[link], StringComparison.OrdinalIgnoreCase);
    }
}
