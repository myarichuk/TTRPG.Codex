using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The armor, shields and weapons of the generated <c>rules-core</c> pack (chapter 6a of the book import) on a Four Degrees character sheet: a level 3 warden (trained in unarmored defense, light and medium armor, and in
/// unarmed, simple and martial weapons, so a rank is worth 3 + 2 = 5) with a Strength modifier of +2 and a Dexterity modifier of +2 or +4. Every number the sheet is expected to show is worked out from the
/// property values the pack file holds (read from the loaded entry in the test, and pinned in the comments from the yaml), never from memory of the book.
/// </summary>
public class FourDegreesEquipmentTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core", "four-degrees-rules" };

    private readonly ContentRegistry _registry = new();
    private readonly YamlContentPackLoader _packs;
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesEquipmentTests()
    {
        _packs = new YamlContentPackLoader(_registry);
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), _packs);
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(SystemId, type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(SystemId, type, Packs);
    }

    private SheetRuleEngine Engine() => new(_system, new PackResolver(_registry));

    private Dictionary<string, object> Warden(long dex, params string[] inventory)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["ancestry"] = "marshkin";
        sheet["background"] = "ferry-hand";
        sheet["class"] = "warden";
        sheet["level"] = 3L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = key == "dex" ? dex : 2L;
        sheet["inventory"] = inventory.Cast<object>().ToList();
        return sheet;
    }

    private ContentEntry Item(string type, string id) => _registry.GetEntries(SystemId, type, Packs).Single(e => e.Id == id);

    private static int Number(ContentEntry entry, string key) => Convert.ToInt32(entry.Properties[key], System.Globalization.CultureInfo.InvariantCulture);

    private static SheetAction Strike(SheetEvaluation evaluation, string weapon) => evaluation.Actions.Single(a => a.Entry.Id == weapon);

    private static List<string> Problems(SheetEvaluation evaluation) =>
        evaluation.Violations.Select(v => $"violation {v.Key}: {v.Message}").Concat(evaluation.Warnings.Select(v => $"warning {v.Key}: {v.Message}")).ToList();

    /// <summary>The abilities and the damage dice of a weapon's <c>use</c> map, as the pack file writes them.</summary>
    private static (string Attack, string[] Abilities, string Damage) Use(ContentEntry weapon)
    {
        var use = Assert.IsAssignableFrom<IDictionary<string, object>>(weapon.Properties["use"]);
        return (use["attack"].ToString()!, ((IEnumerable<object>)use["abilities"]).Select(a => a.ToString()!).ToArray(), use["damage"].ToString()!);
    }

    private static string UseText(ContentEntry weapon)
    {
        var (attack, abilities, damage) = Use(weapon);
        return $"{attack}|{string.Join(',', abilities)}|{damage}";
    }

    [Fact]
    public void TheRulesPackLoadsWithNoProblem_AndTheNewCategoryIsInTheSystem()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
        Assert.Empty(_packs.TakeProblems());

        // The advanced weapons are a category of the dropdown and a key of the ranked weapons list.
        var weapons = _system.SheetRules!.Ranked.Single(r => r.Key == "weapons").List.Select(l => l.Key).ToArray();
        Assert.Equal(new[] { "unarmed", "simple", "martial", "advanced" }, weapons);
        Assert.Contains("advanced", _system.EntityTypes.Single(t => t.Type == "weapon").Fields.Single(f => f.Key == "weaponCategory").Options!);
        Assert.Contains("unarmored", _system.EntityTypes.Single(t => t.Type == "armor").Fields.Single(f => f.Key == "armorCategory").Options!);
    }

    [Theory]
    [InlineData("chain-shirt", 12, 3, 19)]          // light armor: armorBase 12 (10 + the printed bonus 2), Dexterity +2 under the cap 3, trained 5: 12 + 2 + 5
    [InlineData("breastplate", 14, 1, 20)]          // medium armor: armorBase 14, Dexterity +2 limited to the cap 1, trained 5: 14 + 1 + 5
    [InlineData("explorer-s-clothing", 10, 5, 17)]  // unarmored: armorBase 10 (a printed bonus of 0), cap 5, trained in unarmored defense: 10 + 2 + 5
    [InlineData("full-plate", 16, 0, 16)]           // heavy armor: armorBase 16, a cap of 0 (Dexterity does not count), and the warden is not trained in heavy armor: 16 + 0 + 0
    public void ImportedBodyArmor_GivesItsBasePlusDexterityUpToItsCap_AndTheRankOfItsCategory(string id, int armorBase, int cap, int expected)
    {
        var armor = Item("armor", id);
        Assert.Equal(armorBase, Number(armor, "armorBase"));
        Assert.Equal(cap, Number(armor, "armorDexCap"));
        Assert.False(armor.Properties.ContainsKey("armorType"));

        var evaluation = Engine().Recompute(Warden(2, $"{id}|equipped:1"));

        Assert.Empty(Problems(evaluation));
        Assert.Equal(expected, evaluation.ArmorClass!.Total);
    }

    [Fact]
    public void AnImportedShield_AddsItsBonus_WhenItIsEquipped_OnTopOfTheArmor()
    {
        var shield = Item("armor", "wooden-shield");
        // A wooden shield: armorType shield and a bonus of 2 (the book's printed AC bonus), with hardness 3 and 12 hit points and a broken threshold of 6.
        Assert.Equal("shield", shield.Properties["armorType"]);
        Assert.Equal(new[] { 2, 3, 12, 6 }, new[] { "armorClassBonus", "hardness", "hp", "brokenThreshold" }.Select(k => Number(shield, k)));
        Assert.False(shield.Properties.ContainsKey("armorBase"));

        var engine = Engine();
        var armored = engine.Recompute(Warden(2, "breastplate|equipped:1")).ArmorClass!.Total;
        var both = engine.Recompute(Warden(2, "breastplate|equipped:1", "wooden-shield|equipped:1"));

        Assert.Empty(Problems(both));
        Assert.Equal(armored + Number(shield, "armorClassBonus"), both.ArmorClass!.Total); // 20 + 2
        // With no armor worn the shield's bonus is added to the unarmored class: 10 + Dexterity +2 + the rank 5, and 2.
        Assert.Equal(10 + 2 + 5 + 2, engine.Recompute(Warden(2, "wooden-shield|equipped:1")).ArmorClass!.Total);
        // A tower shield prints two bonuses; the first is the one the pack's field holds.
        Assert.Equal(2, Number(Item("armor", "tower-shield"), "armorClassBonus"));
    }

    [Fact]
    public void AFinesseWeapon_StrikesWithTheBetterOfStrengthAndDexterity_AndDealsItsDamageDice()
    {
        var rapier = Item("weapon", "rapier");
        var longsword = Item("weapon", "longsword");
        // The pack file: a rapier is martial, 1d6, finesse, and its use names Strength and Dexterity; a longsword is martial, 1d8 and names Strength alone.
        Assert.Equal("melee|str,dex|1d6", UseText(rapier));
        Assert.Equal("melee|str|1d8", UseText(longsword));
        Assert.Contains("finesse", ((IEnumerable<object>)rapier.Properties["traits"]).Select(t => t.ToString()));
        Assert.Contains("weapons", rapier.Tags);

        // Strength +2 and Dexterity +4; the martial rank is 3 + 2.
        var evaluation = Engine().Recompute(Warden(4, "rapier|equipped:1", "longsword|equipped:1"));

        Assert.Empty(Problems(evaluation));
        Assert.Equal(4 + 5, Strike(evaluation, "rapier").AttackBonus);
        Assert.Equal("1d6", Strike(evaluation, "rapier").Damage);
        Assert.Equal(2 + 5, Strike(evaluation, "longsword").AttackBonus);
        Assert.Equal("1d8", Strike(evaluation, "longsword").Damage);
    }

    [Fact]
    public void ARangedWeaponUsesDexterity_AndAnUnarmedAttackItsOwnRank()
    {
        var shortbow = Item("weapon", "shortbow");
        var fist = Item("weapon", "fist");
        Assert.Equal("ranged|dex|1d6", UseText(shortbow));
        Assert.Equal("melee|str,dex|1d4", UseText(fist));
        Assert.Equal(60, Number(shortbow, "range"));
        Assert.Equal("unarmed", fist.Properties["weaponCategory"]);

        var evaluation = Engine().Recompute(Warden(4, "shortbow|equipped:1", "fist|equipped:1"));

        Assert.Empty(Problems(evaluation));
        Assert.Equal(4 + 5, Strike(evaluation, "shortbow").AttackBonus);
        Assert.Equal("1d6", Strike(evaluation, "shortbow").Damage);
        Assert.Equal(4 + 5, Strike(evaluation, "fist").AttackBonus); // finesse: Dexterity +4 and the unarmed rank
        Assert.Equal("1d4", Strike(evaluation, "fist").Damage);
    }

    [Fact]
    public void EveryImportedWeaponMakesAStrike_WithItsOwnDamageDice_AndTheRankOfItsCategory()
    {
        var weapons = Entries("weapon").ToList();
        Assert.Equal(83, weapons.Count);

        var evaluation = Engine().Recompute(Warden(4, weapons.Select(w => $"{w.Id}|equipped:1").ToArray()));

        Assert.Equal(83, evaluation.Actions.Count);
        // Strength +2 and Dexterity +4; a rank is 5 in the categories the warden is trained in (unarmed, simple, martial) and nothing in the advanced one. The expectation is worked out from the
        // weapon's range, traits, category and damage fields (the book's attack rules), not from its `use` map, which is what the sheet reads: a ranged weapon uses Dexterity, a finesse one the better of
        // the two, any other Strength.
        Assert.All(weapons, w =>
        {
            var traits = ((IEnumerable<object>?)w.Properties.GetValueOrDefault("traits") ?? Array.Empty<object>()).Select(t => t.ToString()).ToList();
            var ability = w.Properties.ContainsKey("range") || traits.Contains("finesse") ? 4 : 2;
            var rank = (string)w.Properties["weaponCategory"] == "advanced" ? 0 : 5;
            Assert.Equal(ability + rank, Strike(evaluation, w.Id).AttackBonus);
            Assert.Equal(w.Properties["damage"]!.ToString(), Strike(evaluation, w.Id).Damage);
        });
        Assert.Equal(new[] { "advanced", "martial", "simple", "unarmed" }, weapons.Select(w => (string)w.Properties["weaponCategory"]).Distinct().Order());
        // Pinned from the pack file: the bonus of a few of them (a thrown melee weapon is a Strength attack; a finesse knife a Dexterity one; an advanced one adds no rank).
        Assert.Equal(new[] { 7, 9, 9, 2 }, new[] { "hatchet", "dagger", "longbow", "dwarven-waraxe" }.Select(id => Strike(evaluation, id).AttackBonus!.Value));
    }

    [Fact]
    public void EveryImportedSuitOfArmorAndShield_Equips_WithTheArmorClassOfItsNumbers()
    {
        // Dexterity +2, the warden's rank 5 in unarmored, light and medium armor and none in heavy: base + the Dexterity the cap lets through + the rank, from the pack file's numbers.
        var suits = new Dictionary<string, int>
        {
            ["breastplate"] = 14 + 1 + 5, ["chain-mail"] = 14 + 1 + 5, ["chain-shirt"] = 12 + 2 + 5, ["explorer-s-clothing"] = 10 + 2 + 5, ["full-plate"] = 16 + 0 + 0, ["half-plate"] = 15 + 1 + 0,
            ["hide-armor"] = 13 + 2 + 5, ["leather"] = 11 + 2 + 5, ["padded-armor"] = 11 + 2 + 5, ["scale-mail"] = 13 + 2 + 5, ["splint-mail"] = 15 + 1 + 0, ["studded-leather"] = 12 + 2 + 5,
        };
        // A shield alone: the unarmored 10 + Dexterity +2 + rank 5, and the shield's bonus.
        var shields = new Dictionary<string, int> { ["buckler"] = 17 + 1, ["wooden-shield"] = 17 + 2, ["steel-shield"] = 17 + 2, ["tower-shield"] = 17 + 2 };
        Assert.Equal(suits.Keys.Concat(shields.Keys).Order(), Entries("armor").Select(a => a.Id).Order());
        foreach (var (id, expected) in suits.Concat(shields))
        {
            var evaluation = Engine().Recompute(Warden(2, $"{id}|equipped:1"));
            Assert.Empty(Problems(evaluation));
            Assert.Equal(expected, evaluation.ArmorClass!.Total);
        }
    }

    [Fact]
    public void AnAgileWeapon_LowersTheMultipleAttackPenalty_AndALongswordDoesNot()
    {
        // The multiple attack rule (sheetRules.multipleAttack): the plain table is 0, -5, -10, and the agile trait's row is 0, -4, -8.
        Assert.Contains("agile", ((IEnumerable<object>)Item("weapon", "blowgun").Properties["traits"]).Select(t => t.ToString()));
        var sheet = Warden(2, "blowgun|equipped:1", "longsword|equipped:1");
        double Total(string weapon, int attack) => SheetRolls.Roll(_system, new PackResolver(_registry), sheet, new SheetRollRequest
        {
            Definition = "strike",
            Inputs = new Dictionary<string, object?> { ["bonus"] = 9d, ["target"] = 18d, ["attackNumber"] = (double)attack, ["weapon"] = weapon }
        }, new SequenceDiceSource(new[] { 10 })).Total;

        Assert.Equal(10 + 9, Total("blowgun", 1));
        Assert.Equal(10 + 9 - 4, Total("blowgun", 2));
        Assert.Equal(10 + 9 - 8, Total("blowgun", 3));
        Assert.Equal(10 + 9 - 5, Total("longsword", 2));
        Assert.Equal(10 + 9 - 10, Total("longsword", 3));
    }

    [Fact]
    public void ACapOfZeroLetsANegativeDexterityThrough_ForTheFullPlateOfAClumsyWearer()
    {
        // Full plate: base 16, a cap of 0, untrained for the warden. a Dexterity modifier of -1: the cap is a maximum of the bonus only (the system's dexCapLimitsOnlyBonus), so the penalty counts.
        Assert.True(_system.SheetRules!.ArmorClass!.DexCapLimitsOnlyBonus);
        Assert.Equal(16 - 1, Engine().Recompute(Warden(-1, "full-plate|equipped:1")).ArmorClass!.Total);
        Assert.Equal(16, Engine().Recompute(Warden(0, "full-plate|equipped:1")).ArmorClass!.Total);
        Assert.Equal(16, Engine().Recompute(Warden(4, "full-plate|equipped:1")).ArmorClass!.Total); // +4 is capped to 0
    }

    /// <summary>
    /// The gear of chapter 6b is carried through the sheet's inventory like armor and weapons: a pick of a `gear` entry resolves, raises no violation or warning (a bomb has no weapon `use` map, so it makes no Strike), and
    /// changes no number the armor and weapons set. The sheet sums only a numeric weight property (`weightProperty`), and four-degrees names none: the Bulk of the gear is the text the book prints (`L` is light, ten of
    /// which are 1 Bulk), so the sheet shows the item and its printed Bulk and adds nothing up (no number of the sheet depends on it, so none is asserted: the checks are what the sheet does compute, the inventory, its quantities, the Strikes and the armor class).
    /// </summary>
    [Fact]
    public void AGearItemIsCarriedOnTheSheet_WithItsPrintedBulk_AndTheSheetSumsNoBulk()
    {
        var gear = Entries("gear").ToDictionary(e => e.Id);
        Assert.Contains("gear", _system.SheetRules!.Inventory!.Types);
        Assert.Null(_system.SheetRules.Inventory.Capacity);
        var picks = new[] { "backpack|equipped:1", "wheelchair", "rope-50-feet", "lesser-acid-flask|qty:3", "clothing-explorer-s" };

        var carrying = Engine().Recompute(Warden(2, picks.Append("breastplate|equipped:1").ToArray()));
        var bare = Engine().Recompute(Warden(2, "breastplate|equipped:1"));

        Assert.Empty(Problems(carrying));
        Assert.Equal(new[] { "backpack", "wheelchair", "rope-50-feet", "lesser-acid-flask", "clothing-explorer-s", "breastplate" }, carrying.Inventory.Select(i => i.Entry.Id));
        Assert.All(carrying.Inventory.Take(5), i => Assert.Equal("gear", i.Entry.Type));
        Assert.Equal(3, carrying.Inventory.Single(i => i.Entry.Id == "lesser-acid-flask").Quantity);
        // The printed Bulk is on the entry as the book writes it (a wheelchair 2, a bomb L, a backpack none), and the sheet adds none of it.
        Assert.Equal("2", gear["wheelchair"].Properties["bulk"].ToString());
        Assert.Equal("L", gear["lesser-acid-flask"].Properties["bulk"]);
        Assert.False(gear["backpack"].Properties.ContainsKey("bulk"));
        // No Strike for a bomb (it has no weapon `use` map), and the armor class is the armor's alone.
        Assert.DoesNotContain(carrying.Actions, a => a.Entry.Id == "lesser-acid-flask");
        Assert.Equal(bare.ArmorClass!.Total, carrying.ArmorClass!.Total);
    }

    private IEnumerable<ContentEntry> Entries(string type) => _registry.GetEntries(SystemId, type, new HashSet<string> { "four-degrees-rules" });
}
