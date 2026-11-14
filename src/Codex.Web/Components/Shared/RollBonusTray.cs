using System.Globalization;
using System.Text;
using Codex.Core.Rules;

namespace Codex.Web.Components.Shared;

/// <summary>A sheet-rules roll as the host will make it: the expression with the bonuses' dice and numbers appended, the label saying which came from
/// dice or a tick (<c>Stealth check (+1d6 Lucky charm)</c>), and the ticked opt-ins that counted (<see cref="Applied"/>: they are paid for and unticked).</summary>
public sealed record BuiltRoll(string Expression, string Label, IReadOnlyList<RollOptIn> Applied);

/// <summary>
/// The sheet's "bonuses for the next roll" (docs/ROLL_BONUSES_DESIGN.md): which opt-ins the player ticked, and how a roll that reads a
/// <see cref="RollSite"/> takes them. A ticked one applies to the next roll it fits and is then unticked; one that does not fit stays ticked.
/// One of these lives beside each sheet's dice tray and each table card, so the hosts build rolls the same way.
/// </summary>
public sealed class RollBonusTray
{
    private readonly HashSet<string> _ticked = new(StringComparer.Ordinal);

    /// <summary>The ids ticked now.</summary>
    public IReadOnlyCollection<string> Ticked => _ticked;

    public bool IsTicked(string id) => _ticked.Contains(id);

    public void Set(string id, bool on)
    {
        if (on) _ticked.Add(id);
        else _ticked.Remove(id);
    }

    /// <summary>Leaves ticked only what is still on offer and can be paid (a feature that went away, a pool that ran dry, a <c>when</c> that stopped holding).</summary>
    public void Keep(IEnumerable<RollOptIn> offered) => _ticked.IntersectWith(offered.Where(o => o.CanPay).Select(o => o.Id));

    /// <summary>The kind of roll an action's dice are: damage, but not for healing, which the engine's damage targets do not reach either.
    /// Null means the roll reads no site.</summary>
    public static RollKind? DamageKind(SheetAction action) => action.Heals ? null : RollKind.Damage;

    /// <summary>Whether a tick box fits a roll a table card makes: the card rolls attacks and damage only.</summary>
    public static bool FitsCard(RollOptIn optIn) =>
        optIn.Target is { } target && (target == RollSiteTargets.Attack || target == RollSiteTargets.Damage
            || target.StartsWith(RollSiteTargets.Attack + ".", StringComparison.Ordinal) || target.StartsWith(RollSiteTargets.Damage + ".", StringComparison.Ordinal));

    /// <summary>Takes a roll's base expression and label and adds what the sheet's features give the roll at <paramref name="site"/>: the always-on dice,
    /// and the ticked opt-ins that fit (dice or numbers, as each says). <paramref name="addNumber"/> adds the always-on numbers too, for a roll whose
    /// own bonus does not hold them already (the sheet's spell attack). Nothing changes without an engine, a sheet or a site.</summary>
    public BuiltRoll Build(SheetRuleEngine? engine, IReadOnlyDictionary<string, object>? sheet, RollSite? site, string expression, string label, bool addNumber = false)
    {
        if (engine == null || sheet == null || site == null) return new BuiltRoll(expression, label, Array.Empty<RollOptIn>());

        var always = engine.Bonuses(sheet, site);
        var text = new StringBuilder(expression);
        var parts = new List<string>();
        if (addNumber && always.Number != 0)
        {
            text.Append(Signed(always.Number));
            parts.AddRange(always.Bonuses.Select(b => $"{Signed(b.Amount)} {b.Source}"));
        }

        foreach (var dice in always.Dice)
        {
            text.Append(dice.Dice);
            parts.Add($"{dice.Dice} {dice.Source}");
        }

        var applied = _ticked.Count == 0
            ? new List<RollOptIn>()
            : engine.OptIns(sheet, site).Where(o => _ticked.Contains(o.Id) && o.CanPay).ToList();
        foreach (var optIn in applied)
        {
            text.Append(optIn.Amount);
            parts.Add($"{optIn.Amount} {optIn.Label}");
        }

        return new BuiltRoll(text.ToString(), parts.Count == 0 ? label : $"{label} ({string.Join(", ", parts)})", applied);
    }

    /// <summary>Pays what the applied opt-ins spend, one after the other, through the host's own spend (the sheet's, or the live table's command).
    /// Returns false at the first refusal: the roll is not made and nothing more is spent. After a roll that was made, call <see cref="Used"/>.</summary>
    public static async Task<bool> PayAsync(IEnumerable<RollOptIn> applied, Func<(string Key, int Delta, string Label), Task<bool>> spend)
    {
        foreach (var optIn in applied)
        {
            if (optIn.Spends is not { } cost) continue;
            if (!await spend((cost.Key, -(int)Math.Round(cost.Amount), optIn.Label))) return false;
        }

        return true;
    }

    /// <summary>Unticks the opt-ins a roll took.</summary>
    public void Used(IEnumerable<RollOptIn> applied)
    {
        foreach (var optIn in applied) _ticked.Remove(optIn.Id);
    }

    private static string Signed(int number) => (number >= 0 ? "+" : string.Empty) + number.ToString(CultureInfo.InvariantCulture);

    private static string Signed(double number) => (number >= 0 ? "+" : string.Empty) + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The roll site a request asks for, on <paramref name="engine"/>: its action's attack or damage, the sheet's own spell attack, a save, a
    /// check or a skill check. Null for a roll that reads none (a plain dice roll).</summary>
    public static RollSite? SiteOf(SheetRuleEngine? engine, RollKind? kind, string? key, SheetAction? action)
    {
        if (engine == null || kind == null) return null;
        return kind switch
        {
            RollKind.WeaponAttack or RollKind.SpellAttack when action != null => engine.AttackSite(action),
            RollKind.SpellAttack => engine.SpellAttackSite(),
            RollKind.Damage when action != null => engine.DamageSite(action),
            RollKind.Save when key != null => engine.SaveSite(key),
            RollKind.Check when key != null => engine.CheckSite(key),
            RollKind.SkillCheck when key != null => engine.SkillSite(key),
            _ => null
        };
    }

    /// <summary>The resource key (and its one argument, when it takes one) that says which rolls a tick box on <paramref name="target"/> fits:
    /// <c>attack.weapon.ranged</c> is <c>Actor.Bonus.On.AttackWeaponRanged</c>; <c>check.skill.stealth</c> is <c>Actor.Bonus.On.CheckSkill</c> with
    /// <c>stealth</c>.</summary>
    public static (string Key, string? Argument) AppliesTo(string target)
    {
        const string prefix = "Actor.Bonus.On.";
        var parts = target.Split('.');
        if (parts.Length >= 3 && parts[1] == RollSiteTargets.Entry && parts[0] is RollSiteTargets.Attack or RollSiteTargets.Damage)
        {
            return (prefix + Pascal(parts[0]) + "Entry", string.Join('.', parts[2..]));
        }

        if (parts.Length >= 3 && parts[0] == RollSiteTargets.Check && parts[1] == RollSiteTargets.Skill) return (prefix + "CheckSkill", string.Join('.', parts[2..]));
        if (parts.Length == 2 && parts[0] is RollSiteTargets.Save or RollSiteTargets.Check && parts[1] is not (RollSiteTargets.Weapon or RollSiteTargets.Spell)) return (prefix + Pascal(parts[0]) + "Of", parts[1]);
        return (prefix + string.Concat(parts.Select(Pascal)), null);
    }

    private static string Pascal(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
