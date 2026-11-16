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
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 14L;
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
        // Strength 14 (+2) and trained in martial weapons at level 3 (3 + 2).
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

        // A save: the resilience rune and a cloak are both item bonuses to the Constitution save, so only the higher counts.
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
