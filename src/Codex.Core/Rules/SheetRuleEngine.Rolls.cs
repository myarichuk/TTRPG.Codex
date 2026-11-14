using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

public sealed partial class SheetRuleEngine
{
    // ---- Roll sites: what the sheet-rules rolls read (docs/ROLL_BONUSES_DESIGN.md, "Roll targets") ----

    /// <summary>The range of an action's attack (<c>use.attack</c>): melee or ranged; null for anything else.</summary>
    private static string? RangeOf(string? attack) => attack?.Trim().ToLowerInvariant() switch
    {
        RollSiteTargets.Melee => RollSiteTargets.Melee,
        RollSiteTargets.Ranged => RollSiteTargets.Ranged,
        _ => null
    };

    /// <summary>An action of a source that reads the inventory is made with a weapon; one that reads lists, with a picked entry (a spell).</summary>
    private static string KindWord(ActionSource source) => source.Inventory != null ? RollSiteTargets.Weapon : RollSiteTargets.Spell;

    /// <summary>The attack roll of an action of <paramref name="source"/> that makes an attack of the range <paramref name="attack"/>
    /// (<c>use.attack</c>) with the entry <paramref name="entryId"/>.</summary>
    internal static RollSite AttackSiteOf(ActionSource source, string? attack, string entryId)
    {
        var kind = KindWord(source);
        var range = RangeOf(attack);
        var targets = new List<string> { RollSiteTargets.Attack, $"{RollSiteTargets.Attack}.{kind}" };
        if (range != null)
        {
            targets.Add($"{RollSiteTargets.Attack}.{range}");
            targets.Add($"{RollSiteTargets.Attack}.{kind}.{range}");
        }

        targets.Add($"{RollSiteTargets.Attack}.{RollSiteTargets.Entry}.{entryId}");
        return new RollSite(kind == RollSiteTargets.Weapon ? RollKind.WeaponAttack : RollKind.SpellAttack, targets);
    }

    /// <summary>The damage roll of an action of <paramref name="source"/>; <paramref name="attack"/> is its attack's range, when it makes an attack.</summary>
    internal static RollSite DamageSiteOf(ActionSource source, string? attack, string entryId)
    {
        var range = RangeOf(attack);
        var targets = new List<string> { RollSiteTargets.Damage, $"{RollSiteTargets.Damage}.{KindWord(source)}" };
        if (range != null) targets.Add($"{RollSiteTargets.Damage}.{range}");
        targets.Add($"{RollSiteTargets.Damage}.{RollSiteTargets.Entry}.{entryId}");
        return new RollSite(RollKind.Damage, targets);
    }

    /// <summary>The attack roll of <paramref name="action"/>: <c>attack</c>, its kind, its range (from <c>use.attack</c>), both together, and its entry.</summary>
    public RollSite AttackSite(SheetAction action) => AttackSiteOf(action.Source, action.Attack, action.Entry.Id);

    /// <summary>The damage roll of <paramref name="action"/>: <c>damage</c>, its kind, its range, and its entry.</summary>
    public RollSite DamageSite(SheetAction action) => DamageSiteOf(action.Source, action.Attack, action.Entry.Id);

    /// <summary>The sheet's own spell attack (the one the skills panel rolls, with no action behind it): <c>attack</c> and <c>attack.spell</c>.</summary>
    public RollSite SpellAttackSite() =>
        new(RollKind.SpellAttack, new[] { RollSiteTargets.Attack, $"{RollSiteTargets.Attack}.{RollSiteTargets.Spell}" });

    private string AttributeKey(string attribute) =>
        system.Attributes.FirstOrDefault(a => a.Key.Equals(attribute, StringComparison.OrdinalIgnoreCase))?.Key ?? attribute;

    /// <summary>The saving throw of <paramref name="attribute"/>: <c>save</c> and <c>save.{attribute}</c>.</summary>
    public RollSite SaveSite(string attribute) =>
        new(RollKind.Save, new[] { RollSiteTargets.Save, $"{RollSiteTargets.Save}.{AttributeKey(attribute)}" });

    /// <summary>A check of <paramref name="attribute"/> on its own: <c>check</c> and <c>check.{attribute}</c>.</summary>
    public RollSite CheckSite(string attribute) =>
        new(RollKind.Check, new[] { RollSiteTargets.Check, $"{RollSiteTargets.Check}.{AttributeKey(attribute)}" });

    /// <summary>A check of the skill <paramref name="skill"/> (its key): <c>check</c>, <c>check.{its attribute}</c> and <c>check.skill.{key}</c>.</summary>
    public RollSite SkillSite(string skill)
    {
        var found = Rules?.Skills?.List.FirstOrDefault(s => s.Key.Equals(skill, StringComparison.OrdinalIgnoreCase));
        var targets = new List<string> { RollSiteTargets.Check };
        if (found?.Attribute is { Length: > 0 } attribute) targets.Add($"{RollSiteTargets.Check}.{AttributeKey(attribute)}");
        targets.Add($"{RollSiteTargets.Check}.{RollSiteTargets.Skill}.{found?.Key ?? skill}");
        return new RollSite(RollKind.SkillCheck, targets);
    }

    // ---- What features add to a site ----

    /// <summary>The always-on part of what features add to <paramref name="site"/>: modifiers with no <c>optIn</c> whose target the site reads and whose
    /// <c>when</c> holds. Numbers are stacked once over all the site's targets together, as the system's modifier types say. Dice amounts (<c>1d4</c>,
    /// <c>-1d4</c>) are never folded into a number: they are listed, with their source, for the host to append to the roll's expression.</summary>
    private RollSiteBonus SiteBonus(IReadOnlyList<ActiveFeature> features, RollSite site, IReadOnlyDictionary<string, double> derived, Action<string>? warn)
    {
        var numbers = new List<(ActiveFeature Feature, FeatureModifier Modifier)>();
        var dice = new List<RollDice>();
        foreach (var feature in features.Where(f => !f.OnKind))
        {
            foreach (var modifier in feature.Modifiers.Where(m => m.OptIn == null && site.Reads(m.Target)))
            {
                if (!SheetModel.DiceAmount(modifier.Amount)) numbers.Add((feature, modifier));
                else if (Amount(feature, "1", modifier.When, derived, warn) != null) dice.Add(new RollDice(feature.Name, RollSiteTargets.SignedDice(modifier.Amount)));
            }
        }

        if (numbers.Count == 0 && dice.Count == 0) return RollSiteBonus.None;
        var working = new List<GrantedModifier>();
        var total = WithModifiers(0, ModifierAmounts(numbers, derived, warn), site.Kind.ToString(), working);
        var bonuses = working.Where(g => g.Counted).Select(g => new RollBonus(null, g.Feature.Name, g.Feature.Name, g.Amount, false)).ToList();
        return new RollSiteBonus(total, bonuses, dice);
    }

    /// <summary><see cref="SiteBonus(IReadOnlyList{ActiveFeature}, RollSite, IReadOnlyDictionary{string, double}, Action{string}?)"/> for the evaluation in
    /// progress: its features and its derived values, a bonus that cannot be worked out said once as the feature's warning.</summary>
    private RollSiteBonus SiteBonus(SheetEvaluation result, RollSite site)
    {
        result.SiteNumbers ??= DerivedNumbers(result);
        return SiteBonus(result.Features, site, result.SiteNumbers, name => WarnFeature(result, result.SiteWarned, name));
    }

    /// <summary>What the sheet's features add to <paramref name="site"/> that always counts: the number, who gave it, and the dice that wait for the
    /// roll. Opt-ins are never in it (<see cref="OptIns"/>).</summary>
    public RollSiteBonus Bonuses(IReadOnlyDictionary<string, object> sheet, RollSite site) =>
        SiteBonus(ActiveFeatures(sheet), site, DerivedNumbers(WorkedOutDerived(sheet)), null);

    /// <summary>The tick boxes <paramref name="site"/> offers on <paramref name="sheet"/>: each opt-in modifier of the sheet's features whose target the
    /// site reads and whose <c>when</c> holds, with the id <see cref="OptInIds"/> gives it (the index counts every modifier of the source entry, across
    /// its rows and copies, so ids never clash and survive edits to anything else). The same records a named roll offers
    /// (<see cref="SheetRolls.OptIns"/>), a <c>spends</c> already checked against the sheet's pool.</summary>
    public IReadOnlyList<RollOptIn> OptIns(IReadOnlyDictionary<string, object> sheet, RollSite site) =>
        OptInsReaching(sheet, modifier => site.Reads(modifier.Target));

    /// <summary>Every tick box the sheet's features hold for any sheet-rules roll (the dice tray's "bonuses for the next roll"): the same records as
    /// <see cref="OptIns(IReadOnlyDictionary{string, object}, RollSite)"/>, each with its <see cref="RollOptIn.Target"/>, for modifiers whose
    /// target is a name some roll reads.</summary>
    public IReadOnlyList<RollOptIn> AllOptIns(IReadOnlyDictionary<string, object> sheet) =>
        OptInsReaching(sheet, modifier => RollSiteTargets.IsRollTarget(system, modifier.Target));

    private IReadOnlyList<RollOptIn> OptInsReaching(IReadOnlyDictionary<string, object> sheet, Func<FeatureModifier, bool> reaches)
    {
        var offered = new List<RollOptIn>();
        var features = ActiveFeatures(sheet).Where(f => !f.OnKind).ToList();
        if (!features.Any(f => f.Modifiers.Any(m => m.OptIn != null && reaches(m)))) return offered;

        var derived = DerivedNumbers(WorkedOutDerived(sheet));
        var perSource = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            foreach (var modifier in feature.Modifiers)
            {
                var index = perSource[feature.Source.Id] = perSource.GetValueOrDefault(feature.Source.Id) + 1;
                if (modifier.OptIn is not { } optIn || !reaches(modifier)) continue;

                string text;
                if (SheetModel.DiceAmount(modifier.Amount))
                {
                    if (Amount(feature, "1", modifier.When, derived, null) == null) continue;
                    text = RollSiteTargets.SignedDice(modifier.Amount);
                }
                else
                {
                    if (Amount(feature, modifier.Amount, modifier.When, derived, null) is not { } amount) continue; // off, or an amount that cannot be worked out
                    text = (amount >= 0 ? "+" : string.Empty) + amount.ToString(CultureInfo.InvariantCulture);
                }

                var label = string.IsNullOrWhiteSpace(optIn.Label) ? feature.Name : optIn.Label!;
                offered.Add(SheetRolls.Offer(sheet, new OptInModifier(OptInIds.Feature(feature.Source.Id, index - 1), label, text, feature.Name, optIn, null, modifier.Target)));
            }
        }

        return offered;
    }

    /// <summary>What features add to a check of each attribute on its own (<see cref="SheetEvaluation.CheckBonuses"/>): the attributes panel rolls the
    /// attribute's modifier and this. Skill checks and saves have theirs in <see cref="SkillScore.Bonus"/>.</summary>
    private void EvaluateChecks(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules?.Skills == null || result.Features.Count == 0) return;
        foreach (var attribute in system.Attributes)
        {
            var site = SiteBonus(result, CheckSite(attribute.Key));
            if (site.Number == 0 && site.Bonuses.Count == 0) continue;
            result.CheckBonuses[attribute.Key] = site.Number;
            result.CheckSources[attribute.Key] = site.Bonuses;
        }
    }
}
