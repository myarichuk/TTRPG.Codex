using Codex.ContentImport;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What a sheet can cast and attack with (sheetRules.actions), on the real SRD system and its pack: the attack
/// bonus, the saving throw and its DC, the dice with their bonus, and which slots pay for it. And the importer's reading of
/// the rules text into the <c>use</c> property those actions come from.</summary>
public class SheetActionsTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public SheetActionsTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Character(string cls, int level, params (string Attribute, long Score)[] scores)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new Pack(_registry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = cls;
        sheet["species"] = "human";
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var (attribute, score) in scores) sheet[SheetRuleEngine.BasePrefix + attribute] = score;
        return (engine, sheet);
    }

    private static SheetAction Action(SheetEvaluation evaluation, string id) => evaluation.Actions.Single(a => a.Entry.Id == id);

    [Fact]
    public void ACastersSpells_AreActions_WithTheirAttackSaveDiceAndSlots()
    {
        var (engine, sheet) = Character("wizard", 5, ("int", 16));
        sheet["cantrips"] = new List<object> { "fire-bolt", "light" };
        sheet["spells"] = new List<object> { "fireball", "burning-hands", "hold-person" };

        var evaluation = engine.Recompute(sheet);

        // Proficiency +3, Intelligence +3.
        var bolt = Action(evaluation, "fire-bolt");
        Assert.Equal("cast", bolt.Source.Key);
        Assert.Equal("ranged", bolt.Attack);
        Assert.Equal(6, bolt.AttackBonus);
        Assert.Equal("2d10", bolt.Damage); // level 5: the cantrip's second step
        Assert.Equal("Fire", bolt.DamageType);
        Assert.False(bolt.Spends);
        Assert.Empty(bolt.Costs);

        var fireball = Action(evaluation, "fireball");
        Assert.Null(fireball.Attack);
        Assert.Equal("dex", fireball.SaveAttribute);
        Assert.Equal(14, fireball.SaveDc);
        Assert.True(fireball.Spends);
        var only = Assert.Single(fireball.Costs); // a level 5 wizard's highest slot is level 3
        Assert.Equal("slots_3", only.Key);
        Assert.Equal("8d6", only.Damage);

        var hands = Action(evaluation, "burning-hands");
        Assert.Equal(new[] { "slots_1", "slots_2", "slots_3" }, hands.Costs.Select(c => c.Key));
        Assert.Equal(new[] { "3d6", "4d6", "5d6" }, hands.Costs.Select(c => c.Damage)); // one more die per slot level above the first

        var hold = Action(evaluation, "hold-person");
        Assert.Equal("wis", hold.SaveAttribute);
        Assert.Null(hold.Damage);
        Assert.Equal(new[] { "slots_2", "slots_3" }, hold.Costs.Select(c => c.Key));

        var light = Action(evaluation, "light"); // nothing to roll or spend, still on the list
        Assert.Null(light.Attack);
        Assert.Null(light.Damage);
        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == "Actor.Rule.FormulaError");
    }

    [Fact]
    public void HealingAddsTheCastingModifier_WhenTheSpellSaysSo()
    {
        var (engine, sheet) = Character("cleric", 3, ("wis", 16));
        sheet["spells"] = new List<object> { "cure-wounds" };

        var cure = Action(engine.Recompute(sheet), "cure-wounds");

        Assert.True(cure.Heals);
        Assert.Equal("2d8+3", cure.Damage);
        Assert.Equal(new[] { "2d8+3", "4d8+3" }, cure.Costs.Select(c => c.Damage));
        Assert.Null(cure.DamageType);
    }

    [Fact]
    public void AHeldWeapon_IsAnAttack_WithTheBestAbilityAndTrainingCounted()
    {
        var (engine, sheet) = Character("fighter", 5, ("str", 16), ("dex", 12));
        sheet["inventory"] = new List<object> { "longsword|equipped:1", "dagger" };

        var evaluation = engine.Recompute(sheet);

        var sword = Assert.Single(evaluation.Actions); // the dagger is carried, not held
        Assert.Equal("attack", sword.Source.Key);
        Assert.Equal("melee", sword.Attack);
        Assert.Equal(6, sword.AttackBonus); // Strength +3, proficiency +3
        Assert.Equal("1d8+3", sword.Damage);
        Assert.Equal("Slashing", sword.DamageType);
        Assert.Equal("1d10+3", sword.Alternatives["two-handed"]);
        Assert.False(sword.Spends);

        // A wizard holds the same sword without training: no proficiency bonus. A dagger takes the better of the two abilities.
        var (other, wizard) = Character("wizard", 5, ("str", 8), ("dex", 16));
        wizard["inventory"] = new List<object> { "longsword|equipped:1", "dagger|equipped:1" };
        var untrained = other.Recompute(wizard);
        Assert.Equal(-1, Action(untrained, "longsword").AttackBonus);
        Assert.Equal("1d8-1", Action(untrained, "longsword").Damage);
        Assert.Equal(6, Action(untrained, "dagger").AttackBonus); // Dexterity +3, and wizards are trained with daggers
        Assert.Equal("1d4+3", Action(untrained, "dagger").Damage);
    }

    [Fact]
    public void EveryDiceAnActionOffers_IsSomethingTheDiceRollerTakes()
    {
        var (engine, sheet) = Character("wizard", 20, ("int", 20));
        sheet["cantrips"] = _registry.GetEntries("srd5e", "spell").Where(e => Convert.ToInt32(e.Properties["level"]) == 0).Select(e => (object)e.Id).ToList();
        sheet["spells"] = _registry.GetEntries("srd5e", "spell").Where(e => Convert.ToInt32(e.Properties["level"]) > 0).Select(e => (object)e.Id).ToList();
        sheet["inventory"] = _registry.GetEntries("srd5e", "equipment").Where(e => e.Tags.Contains("weapons")).Select(e => (object)(e.Id + "|equipped:1")).ToList();

        var evaluation = engine.Evaluate(sheet);

        Assert.True(evaluation.Actions.Count > 300);
        var dice = evaluation.Actions.SelectMany(a => a.Costs.Select(c => c.Damage).Append(a.Damage).Concat(a.Alternatives.Values)).Where(SheetAction.HasDice).Distinct().ToList();
        Assert.NotEmpty(dice);
        foreach (var expression in dice) DiceExpressionParser.Parse(expression!);
    }

    [Theory]
    [InlineData("8d6", "1d6", 2, "10d6")]
    [InlineData("3d8", "1d8", 0, "3d8")]
    [InlineData("1d4+1", "1d4", 1, "2d4+1")]
    [InlineData("2d8", "1d6", 2, "2d8+2d6")]
    [InlineData("2d8", null, 3, "2d8")]
    public void MoreDice_AddsToTheSameDie_OrAsItsOwnTerm(string dice, string? per, int times, string expected) =>
        Assert.Equal(expected, SheetRuleEngine.MoreDice(dice, per, times));

    [Fact]
    public void TheImporter_ReadsWhatASpellRolls_FromItsRulesText()
    {
        var fireball = UseData.ForSpell("Each creature in a 20-foot-radius Sphere makes a Dexterity saving throw, taking 8d6 Fire damage on a failed save or half as much damage on a successful one. " +
                                        "The damage increases by 1d6 for each spell slot level above 3.")!;
        Assert.Equal("dex", fireball["save"]);
        Assert.Equal("8d6", fireball["damage"]);
        Assert.Equal("Fire", fireball["damageType"]);
        Assert.Equal("1d6", fireball["perSlot"]);
        Assert.False(fireball.ContainsKey("attack"));

        var bolt = UseData.ForSpell("Make a ranged spell attack against the target. On a hit, the target takes 1d10 Fire damage. " +
                                    "Cantrip Upgrade. The damage increases by 1d10 when you reach levels 5 (2d10), 11 (3d10), and 17 (4d10).")!;
        Assert.Equal("ranged", bolt["attack"]);
        Assert.Equal("1d10", bolt["damage"]);
        Assert.Equal("3d10", ((Dictionary<string, object>)bolt["atLevels"])["11"]);

        var cure = UseData.ForSpell("A creature you touch regains a number of Hit Points equal to 2d8 plus your spellcasting ability modifier. The healing increases by 2d8 for each spell slot level above 1.")!;
        Assert.Equal(true, cure["heals"]);
        Assert.Equal(true, cure["addAbility"]);
        Assert.Equal("2d8", cure["perSlot"]);
        Assert.False(cure.ContainsKey("damageType"));

        var missile = UseData.ForSpell("A dart deals 1d4 + 1 Force damage to its target.")!;
        Assert.Equal("1d4+1", missile["damage"]);
        Assert.Equal("Force", missile["damageType"]);

        Assert.Null(UseData.ForSpell("You touch one object that is no larger than 10 feet in any dimension. It sheds Bright Light."));
    }

    [Fact]
    public void TheImporter_ReadsAWeaponsAttack_FromItsDamageAndProperties()
    {
        var sword = UseData.ForWeapon("1d8", "Slashing", "1d10", new[] { "Versatile" }, ranged: false)!;
        Assert.Equal("melee", sword["attack"]);
        Assert.Equal(new object[] { "str" }, (List<object>)sword["abilities"]);
        Assert.Equal("1d10", ((Dictionary<string, object>)sword["alternatives"])["two-handed"]);

        Assert.Equal(new object[] { "str", "dex" }, (List<object>)UseData.ForWeapon("1d4", "Piercing", null, new[] { "Finesse", "Light" }, ranged: false)!["abilities"]);
        Assert.Equal(new object[] { "dex" }, (List<object>)UseData.ForWeapon("1d8", "Piercing", null, new[] { "Ammunition" }, ranged: true)!["abilities"]);
        Assert.Equal(1L, UseData.ForWeapon("1", "Piercing", null, Array.Empty<string>(), ranged: true)!["damage"]);
        Assert.Null(UseData.ForWeapon(null, null, null, Array.Empty<string>(), ranged: false));
    }

    private sealed class Pack(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type);
    }
}
