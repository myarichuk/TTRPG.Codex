using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The magic items and runes of the Four Degrees pack (step P10, docs/ITEM_RUNES_DESIGN.md): the pack loads with no problem, every entry is
/// homebrew, and a level 3 character's attack, damage, armor class and saves read what the items and runes on their gear add, with the
/// system's item bonuses not stacking and the rune-slot limit enforced.
/// </summary>
public class FourDegreesRunesTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core" };

    private readonly ContentRegistry _registry = new();
    private readonly YamlContentPackLoader _packs;
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesRunesTests()
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

    private Dictionary<string, object> Warden(params string[] inventory)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["ancestry"] = "marshkin";
        sheet["background"] = "ferry-hand";
        sheet["class"] = "warden";
        sheet["level"] = 3L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 2L;
        sheet["inventory"] = inventory.Cast<object>().ToList();
        return sheet;
    }

    private static SheetAction Strike(SheetEvaluation evaluation, string weapon) => evaluation.Actions.Single(a => a.Entry.Id == weapon);

    private static List<string> Problems(SheetEvaluation evaluation) =>
        evaluation.Violations.Select(v => $"violation {v.Key}: {v.Message}").Concat(evaluation.Warnings.Select(v => $"warning {v.Key}: {v.Message}")).ToList();

    [Fact]
    public void ThePackLoadsWithNoProblem_AndEveryItemAndRuneIsHomebrew()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
        Assert.Empty(_packs.TakeProblems());

        var items = _registry.GetEntries(SystemId, "magic-item", Packs).ToList();
        var runes = _registry.GetEntries(SystemId, "rune", Packs).ToList();
        Assert.Equal(12, items.Count);
        Assert.Equal(15, runes.Count);
        Assert.All(items.Concat(runes), e =>
        {
            Assert.Contains("homebrew", e.Tags);
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Id);
        });
        Assert.All(runes, r => Assert.Contains(r.Properties["appliesTo"]?.ToString(), new[] { "weapon", "armor" }));
        // Every potency rune opens as many places for property runes as its bonus.
        Assert.All(runes.Where(r => r.Properties.GetValueOrDefault("group")?.ToString() == "potency"), r =>
            Assert.Equal(Convert.ToInt32(((IDictionary<string, object>)((IEnumerable<object>)r.Properties["modifiers"]).Single())["amount"]), Convert.ToInt32(r.Properties["slots"])));
    }

    [Fact]
    public void AWeaponWithNoRune_AttacksAndDealsItsPlainNumbers()
    {
        var evaluation = Engine().Recompute(Warden("ledge-axe|equipped:1"));

        Assert.Empty(Problems(evaluation));
        // Strength +2 and trained in martial weapons at level 3 (3 + 2).
        Assert.Equal(7, Strike(evaluation, "ledge-axe").AttackBonus);
        Assert.Equal("1d10", Strike(evaluation, "ledge-axe").Damage);
    }

    [Fact]
    public void ALevel3CharacterWithPotencyStrikingAndAFlamingRune_GetsTheRightAttackDamageAndDice()
    {
        var sheet = Warden("ledge-axe|equipped:1,rune.honed-edge-1:1,rune.weighted-blow-1:1,rune.flaming:1");
        var engine = Engine();
        var evaluation = engine.Recompute(sheet);

        Assert.Empty(Problems(evaluation));
        var strike = Strike(evaluation, "ledge-axe");
        Assert.Equal(8, strike.AttackBonus); // 7 plus the +1 item bonus
        Assert.Equal("2d10", strike.Damage); // one extra copy of the weapon's dice
        var damage = engine.Bonuses(sheet, engine.DamageSite(strike));
        Assert.Equal(new RollDice("Flaming", "+1d4"), Assert.Single(damage.Dice));
        Assert.Equal(new[] { "Honed edge I" }, engine.Bonuses(sheet, engine.AttackSite(strike)).Bonuses.Select(b => b.Source).ToArray());
    }

    [Fact]
    public void ARuneReachesItsOwnWeaponAlone()
    {
        var evaluation = Engine().Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-1:1,rune.weighted-blow-1:1", "reed-spear|equipped:1"));

        Assert.Equal(8, Strike(evaluation, "ledge-axe").AttackBonus);
        Assert.Equal(7, Strike(evaluation, "reed-spear").AttackBonus); // Strength +2 and trained in simple weapons, nothing more
        Assert.Equal("1d6", Strike(evaluation, "reed-spear").Damage);
    }

    [Fact]
    public void ARuneOnAnItemThatIsNotEquipped_AddsNothing()
    {
        var evaluation = Engine().Recompute(Warden("ledge-axe|rune.honed-edge-1:1"));

        Assert.Empty(evaluation.Actions);
        Assert.Empty(Problems(evaluation));
    }

    [Fact]
    public void ItemBonusesDoNotStack_ThePotencyRuneAndAGloveKeepTheHighest()
    {
        var engine = Engine();

        // A +1 rune and a +1 glove are both item bonuses to the attack: one counts.
        var both = engine.Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-1:1", "steady-glove|equipped:1"));
        Assert.Equal(8, Strike(both, "ledge-axe").AttackBonus);

        // A +2 rune beats the +1 glove; the glove alone gives +1.
        var higher = engine.Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-2:1", "steady-glove|equipped:1"));
        Assert.Equal(9, Strike(higher, "ledge-axe").AttackBonus);
        var glove = engine.Recompute(Warden("ledge-axe|equipped:1", "steady-glove|equipped:1"));
        Assert.Equal(8, Strike(glove, "ledge-axe").AttackBonus);

        // A save: the steadiness rune and a cloak are both item bonuses to the Constitution save, so only the higher counts.
        var plain = engine.Recompute(Warden("lacquered-scale|equipped:1")).SavingThrows.Single(s => s.Key == "con").Bonus;
        Assert.Equal(plain + 1, engine.Recompute(Warden("lacquered-scale|equipped:1", "hearth-cloak|equipped:1")).SavingThrows.Single(s => s.Key == "con").Bonus);
        Assert.Equal(plain + 1, engine.Recompute(Warden("lacquered-scale|equipped:1,rune.steady-heart-1:1", "hearth-cloak|equipped:1")).SavingThrows.Single(s => s.Key == "con").Bonus);
        Assert.Equal(plain + 2, engine.Recompute(Warden("lacquered-scale|equipped:1,rune.steady-heart-2:1", "hearth-cloak|equipped:1")).SavingThrows.Single(s => s.Key == "con").Bonus);
    }

    [Fact]
    public void ArmorRunes_RaiseArmorClass_AsAnItemBonus()
    {
        var engine = Engine();
        var bare = engine.Recompute(Warden("lacquered-scale|equipped:1")).ArmorClass!.Total;

        var warded = engine.Recompute(Warden("lacquered-scale|equipped:1,rune.padded-ward-2:1,rune.mossy-lining:1"));
        Assert.Empty(Problems(warded));
        Assert.Equal(bare + 2, warded.ArmorClass!.Total);
    }

    [Fact]
    public void ThePropertyRuneLimit_IsTheItemsPotency()
    {
        var engine = Engine();

        // One potency place, one property rune: fine. Two property runes on potency 1: over the limit.
        Assert.Empty(Problems(engine.Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-1:1,rune.flaming:1"))));
        var over = engine.Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-1:1,rune.flaming:1,rune.icy:1"));
        Assert.Equal("Actor.Rule.RuneSlots", Assert.Single(over.Violations).Key);

        // Potency 2 has room for two; a weapon with no potency rune has room for none.
        Assert.Empty(Problems(engine.Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-2:1,rune.flaming:1,rune.icy:1"))));
        Assert.Equal("Actor.Rule.RuneSlots", Assert.Single(engine.Recompute(Warden("ledge-axe|equipped:1,rune.flaming:1")).Violations).Key);
    }

    [Fact]
    public void AGroupIsHeldOnce_ARuneFitsItsItem_AndAnUnknownRuneIsReported()
    {
        var engine = Engine();

        Assert.Equal("Actor.Rule.RuneGroup", Assert.Single(engine.Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-1:1,rune.honed-edge-2:1")).Violations).Key);
        Assert.Equal("Actor.Rule.RuneWrongItem", Assert.Single(engine.Recompute(Warden("ledge-axe|equipped:1,rune.padded-ward-1:1")).Violations).Key);
        Assert.Equal("Actor.Rule.RuneNotFound", Assert.Single(engine.Recompute(Warden("ledge-axe|equipped:1,rune.no-such-rune:1")).Violations).Key);
    }

    /// <summary>The pack's entries plus a maul with scaled and alternative dice, which no pack weapon has.</summary>
    private sealed class WithMaul(IEntryResolver inner) : IEntryResolver
    {
        private static readonly ContentEntry Maul = new()
        {
            Id = "test-maul", Type = "weapon", Name = "Test maul", Tags = new List<string> { "weapons" },
            Properties = new Dictionary<string, object>
            {
                ["weaponCategory"] = "martial",
                ["use"] = new Dictionary<string, object>
                {
                    ["attack"] = "melee", ["abilities"] = new List<object> { "str" }, ["damage"] = "1d6",
                    ["atLevels"] = new Dictionary<string, object> { ["3"] = "2d6+1" },
                    ["alternatives"] = new Dictionary<string, object> { ["twoHand"] = "1d10" }
                }
            }
        };

        public ContentEntry? Resolve(string type, string id) => type == "weapon" && id == "test-maul" ? Maul : inner.Resolve(type, id);
        public IEnumerable<ContentEntry> Candidates(string type) => inner.Candidates(type);
    }

    [Fact]
    public void ExtraDice_CopyTheLeadingDice_OfAScaledWeapon_AndOfItsAlternatives()
    {
        var engine = new SheetRuleEngine(_system, new WithMaul(new PackResolver(_registry)));
        var one = engine.Recompute(Warden("test-maul|equipped:1,rune.weighted-blow-1:1"));
        var two = engine.Recompute(Warden("test-maul|equipped:1,rune.honed-edge-2:1,rune.weighted-blow-2:1"));

        // At level 3 the maul rolls 2d6+1: the leading 2d6 gains one copy (4d6) or two (6d6); the trailing +1 stays.
        Assert.Equal("4d6+1", Strike(one, "test-maul").Damage);
        Assert.Equal("6d6+1", Strike(two, "test-maul").Damage);
        Assert.Equal("2d10", Strike(one, "test-maul").Alternatives["twoHand"]);
        Assert.Equal("3d10", Strike(two, "test-maul").Alternatives["twoHand"]);
    }

    [Fact]
    public void ARuneThatIsReported_AddsNothing()
    {
        var engine = Engine();

        // An armor rune on a weapon: reported, and no armor class or attack comes of it.
        var wrong = engine.Recompute(Warden("ledge-axe|equipped:1,rune.padded-ward-1:1"));
        Assert.Equal("Actor.Rule.RuneWrongItem", Assert.Single(wrong.Violations).Key);
        Assert.Equal(engine.Recompute(Warden("ledge-axe|equipped:1")).ArmorClass!.Total, wrong.ArmorClass!.Total);

        // A property rune past the room, and a second rune of one group: reported, and only the first counts.
        var sheet = Warden("ledge-axe|equipped:1,rune.honed-edge-1:1,rune.flaming:1,rune.icy:1");
        Assert.Equal(new RollDice("Flaming", "+1d4"), Assert.Single(engine.Bonuses(sheet, engine.DamageSite(Strike(engine.Recompute(sheet), "ledge-axe"))).Dice));
        var twice = engine.Recompute(Warden("ledge-axe|equipped:1,rune.honed-edge-1:1,rune.honed-edge-3:1"));
        Assert.Equal("Actor.Rule.RuneGroup", Assert.Single(twice.Violations).Key);
        Assert.Equal(8, Strike(twice, "ledge-axe").AttackBonus);
    }

    [Fact]
    public void TwoPropertyRunes_OnPotency2_BothAddTheirDice()
    {
        var sheet = Warden("ledge-axe|equipped:1,rune.honed-edge-2:1,rune.flaming:1,rune.icy:1");
        var engine = Engine();

        var dice = engine.Bonuses(sheet, engine.DamageSite(Strike(engine.Recompute(sheet), "ledge-axe"))).Dice.Select(d => d.Source + d.Dice).ToList();

        // Dice are listed, never stacked by type: both rolls are added.
        Assert.Equal(new[] { "Flaming+1d4", "Icy+1d4" }, dice.OrderBy(d => d).ToArray());
    }

    [Fact]
    public void EveryItemTarget_MovesTheNumberByOne()
    {
        var engine = Engine();
        var bare = engine.Recompute(Warden());
        int Skill(SheetEvaluation e, string key) => e.Skills.Single(s => s.Key == key).Bonus;
        int Save(SheetEvaluation e, string key) => e.SavingThrows.Single(s => s.Key == key).Bonus;

        var worn = engine.Recompute(Warden("far-lens|equipped:1", "silt-glass-charm|equipped:1", "sparrow-band|equipped:1", "knotted-belt|equipped:1", "thornwood-bracer|equipped:1",
            "lacquered-scale|equipped:1,rune.padded-ward-2:1,rune.mossy-lining:1,rune.warded-mind:1"));
        Assert.Empty(Problems(worn));
        Assert.Equal(Convert.ToInt32(bare.Derived["perception"]) + 1, Convert.ToInt32(worn.Derived["perception"]));
        Assert.Equal(Convert.ToInt32(bare.Derived["classDc"]) + 1, Convert.ToInt32(worn.Derived["classDc"]));
        Assert.Equal(Skill(bare, "stealth") + 1, Skill(worn, "stealth"));
        Assert.Equal(Skill(bare, "athletics") + 1, Skill(worn, "athletics"));
        Assert.Equal(Skill(bare, "survival") + 1, Skill(worn, "survival"));
        Assert.Equal(Save(bare, "dex") + 1, Save(worn, "dex"));
        Assert.Equal(Save(bare, "wis") + 1, Save(worn, "wis"));

    }

    [Fact]
    public void AnItemThatIsNotEquipped_AddsNothing()
    {
        var engine = Engine();
        var bare = engine.Recompute(Warden());
        var carried = engine.Recompute(Warden("far-lens", "sparrow-band", "thornwood-bracer"));

        Assert.Equal(Convert.ToInt32(bare.Derived["perception"]), Convert.ToInt32(carried.Derived["perception"]));
        Assert.Equal(bare.Skills.Single(s => s.Key == "stealth").Bonus, carried.Skills.Single(s => s.Key == "stealth").Bonus);
        Assert.Equal(bare.SavingThrows.Single(s => s.Key == "dex").Bonus, carried.SavingThrows.Single(s => s.Key == "dex").Bonus);
    }

    [Fact]
    public void WeaponRunes_NarrowEveryAttackAndDamageTarget_ToTheirWeapon()
    {
        var rune = new ContentEntry
        {
            Id = "melee-edge", Type = "rune", Name = "Melee edge", Tags = new List<string> { "homebrew" },
            Properties = new Dictionary<string, object>
            {
                ["appliesTo"] = "weapon", ["group"] = "potency", ["slots"] = 0L,
                ["modifiers"] = new List<object> { new Dictionary<string, object> { ["target"] = "attack.melee", ["amount"] = "1", ["type"] = "item" } }
            }
        };
        var engine = new SheetRuleEngine(_system, new WithRune(new PackResolver(_registry), rune));

        var evaluation = engine.Recompute(Warden("ledge-axe|equipped:1,rune.melee-edge:1", "reed-spear|equipped:1"));

        Assert.Equal(8, Strike(evaluation, "ledge-axe").AttackBonus);
        Assert.Equal(7, Strike(evaluation, "reed-spear").AttackBonus);
    }

    private sealed class WithRune(IEntryResolver inner, ContentEntry rune) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => type == "rune" && id == rune.Id ? rune : inner.Resolve(type, id);
        public IEnumerable<ContentEntry> Candidates(string type) => inner.Candidates(type);
    }

    [Fact]
    public void ARuneChoice_MeansNothingToASystemWithNoRuneType()
    {
        var srd = _loader.GetDefinition("srd5e")!;
        Assert.Null(srd.SheetRules!.Inventory!.RuneType);
        var entry = new ContentEntry { Id = "dagger", Type = srd.SheetRules.Inventory.Types[0], Name = "Dagger" };
        var engine = new SheetRuleEngine(srd, new SingleEntry(entry));
        var sheet = new Dictionary<string, object> { ["inventory"] = new List<object> { "dagger|equipped:1,rune.flaming:1" } };

        var evaluation = engine.Recompute(sheet);

        Assert.DoesNotContain(evaluation.Violations, v => v.Key?.StartsWith("Actor.Rule.Rune") == true);
        Assert.Equal("dagger", Assert.Single(evaluation.Inventory).Entry.Id);
        Assert.Empty(evaluation.Features);
    }

    private sealed class SingleEntry(ContentEntry entry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => type == entry.Type && id == entry.Id ? entry : null;
        public IEnumerable<ContentEntry> Candidates(string type) => Enumerable.Empty<ContentEntry>();
    }

    [Fact]
    public void AddingAnItem_NeverJoinsALineWithRunes_AndTakingGivesPlainCopiesFirst()
    {
        var rules = _system.SheetRules!.Inventory!;
        var raw = new List<string> { "ledge-axe|equipped:1,rune.honed-edge-1:1", "ledge-axe|qty:2", "ledge-axe|rune.flaming:1" };

        // Three copies are carried; taking two takes the plain pair, and the runed copies stay as they are.
        Assert.True(InventoryMoves.Take(raw, rules, "ledge-axe", 2));
        Assert.Equal(new[] { "ledge-axe|equipped:1,rune.honed-edge-1:1", "ledge-axe|rune.flaming:1" }, raw);

        // A part of a plain stack leaves the rest; its runes stay with the line they are on.
        var other = new List<string> { "ledge-axe|qty:3,rune.flaming:1" };
        Assert.True(InventoryMoves.Take(other, rules, "ledge-axe", 1));
        Assert.Equal(new[] { SheetRuleEngine.FormatItem(rules, "ledge-axe", 2, false, "ledge-axe|qty:3,rune.flaming:1") }, other);
        Assert.Contains("rune.flaming:1", other[0]);

        // Receiving one never adds to a line with runes: it starts a plain line, then joins that.
        var receiver = new List<string> { "ledge-axe|equipped:1,rune.honed-edge-1:1" };
        InventoryMoves.Add(receiver, rules, "ledge-axe", 1);
        InventoryMoves.Add(receiver, rules, "ledge-axe", 1);
        Assert.Equal(new[] { "ledge-axe|equipped:1,rune.honed-edge-1:1", "ledge-axe|qty:2" }, receiver);
    }

    [Fact]
    public void ChangingQuantityOrEquippedState_KeepsTheRunes()
    {
        var rules = _system.SheetRules!.Inventory!;
        var raw = SheetRuleEngine.WithRune("ledge-axe|equipped:1", "flaming");

        var kept = SheetRuleEngine.FormatItem(rules, "ledge-axe", 1, false, raw);

        Assert.Equal(new[] { "flaming" }, SheetRuleEngine.RuneIds(SheetRuleEngine.ParsePick(kept).Choices));
        Assert.Equal(0, SheetRuleEngine.ParsePick(kept).Choices.GetValueOrDefault(rules.EquippedChoice));
    }
}
