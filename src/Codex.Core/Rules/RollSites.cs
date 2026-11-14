using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>The kinds of roll a sheet-rules system makes (docs/ROLL_BONUSES_DESIGN.md, "Roll targets"). Generic roll words, never a system's own.</summary>
public enum RollKind
{
    /// <summary>An attack made with an item (an action source that reads the inventory).</summary>
    WeaponAttack,

    /// <summary>An attack made with a picked entry (an action source that reads lists), and the sheet's own spell attack.</summary>
    SpellAttack,

    /// <summary>The damage (or healing dice) of an action.</summary>
    Damage,

    /// <summary>A saving throw.</summary>
    Save,

    /// <summary>A check of an attribute alone.</summary>
    Check,

    /// <summary>A check of a skill.</summary>
    SkillCheck
}

/// <summary>A roll of a sheet-rules system and the modifier targets it reads, from general to narrow. A modifier applies to the roll when its
/// <c>target</c> is one of <see cref="Targets"/>. Built by <see cref="SheetRuleEngine"/> for an action, an attribute, a save or a skill.</summary>
public sealed record RollSite(RollKind Kind, IReadOnlyList<string> Targets)
{
    /// <summary>Whether the roll reads <paramref name="target"/>.</summary>
    public bool Reads(string target) => Targets.Contains(target, StringComparer.Ordinal);
}

/// <summary>An always-on dice amount (<c>+1d4</c>, <c>-1d4</c>) a feature adds to a roll. It is never folded into a number: the host appends
/// <see cref="Dice"/> (sign included) to the roll's expression.</summary>
public sealed record RollDice(string Source, string Dice);

/// <summary>What the sheet's own features add to one roll site: <see cref="Number"/> (the always-on numbers, stacked as the system's modifier types say),
/// <see cref="Bonuses"/> (the numbers that counted and who gave them) and <see cref="Dice"/> (the always-on dice, waiting for the roll). Opt-ins are in
/// neither: see <see cref="SheetRuleEngine.OptIns"/>.</summary>
public sealed record RollSiteBonus(int Number, IReadOnlyList<RollBonus> Bonuses, IReadOnlyList<RollDice> Dice)
{
    public static readonly RollSiteBonus None = new(0, Array.Empty<RollBonus>(), Array.Empty<RollDice>());
}

/// <summary>What one source added to an action's attack bonus or damage: <see cref="Roll"/> is <see cref="SheetAction.AttackRoll"/> or
/// <see cref="SheetAction.DamageRoll"/>.</summary>
public sealed record ActionBonus(string Roll, string Source, int Amount);

/// <summary>The modifier targets sheet-rules rolls read (docs/ROLL_BONUSES_DESIGN.md).</summary>
public static class RollSiteTargets
{
    public const string Attack = "attack";
    public const string Damage = "damage";
    public const string Save = "save";
    public const string Check = "check";
    public const string Weapon = "weapon";
    public const string Spell = "spell";
    public const string Melee = "melee";
    public const string Ranged = "ranged";
    public const string Entry = "entry";
    public const string Skill = "skill";

    /// <summary>The fixed target names of a roll word, as the vocabulary lists them: the word, its two kinds, its two ranges and the kind-and-range pairs.
    /// <c>attack</c> and <c>damage</c> only for a system with actions; <c>save</c> and <c>check</c> only for one with skills.</summary>
    public static IEnumerable<string> Fixed(SheetRules rules)
    {
        if (rules.Actions.Count > 0)
        {
            yield return Attack;
            yield return $"{Attack}.{Weapon}";
            yield return $"{Attack}.{Spell}";
            yield return $"{Attack}.{Melee}";
            yield return $"{Attack}.{Ranged}";
            yield return $"{Attack}.{Weapon}.{Melee}";
            yield return $"{Attack}.{Weapon}.{Ranged}";
            yield return $"{Attack}.{Spell}.{Melee}";
            yield return $"{Attack}.{Spell}.{Ranged}";
            yield return Damage;
            yield return $"{Damage}.{Weapon}";
            yield return $"{Damage}.{Spell}";
            yield return $"{Damage}.{Melee}";
            yield return $"{Damage}.{Ranged}";
        }

        if (rules.Skills != null)
        {
            yield return Save;
            yield return Check;
        }
    }

    /// <summary>Whether <paramref name="target"/> is a name a sheet-rules roll reads: a fixed one, a dynamic one (<c>attack.entry.{id}</c>,
    /// <c>damage.entry.{id}</c>, <c>save.{attribute}</c>, <c>check.{attribute}</c>, <c>check.skill.{key}</c>) whose key this system has.</summary>
    public static bool IsRollTarget(SystemDefinition system, string target)
    {
        if (system.SheetRules is not { } rules || target.Length == 0) return false;
        if (Fixed(rules).Contains(target, StringComparer.Ordinal)) return true;

        string? Rest(string prefix) => target.StartsWith(prefix + ".", StringComparison.Ordinal) && target.Length > prefix.Length + 1 ? target[(prefix.Length + 1)..] : null;
        if (rules.Actions.Count > 0 && (Rest($"{Attack}.{Entry}") != null || Rest($"{Damage}.{Entry}") != null)) return true;
        if (rules.Skills is not { } skills) return false;
        if (Rest($"{Check}.{Skill}") is { } skill) return skills.List.Any(s => s.Key == skill);
        var attribute = Rest(Save) ?? Rest(Check);
        return attribute != null && system.Attributes.Any(a => a.Key == attribute);
    }

    /// <summary>Every name a feature of a sheet-rules system may use to reach a roll: the fixed ones, then each attribute's save and check, then each skill's
    /// check. The <c>.entry.{id}</c> names are not listed: they name one entry of the Library.</summary>
    public static IEnumerable<GrantName> Listed(SystemDefinition system, SheetRules rules)
    {
        foreach (var name in Fixed(rules)) yield return new GrantName(name, name);
        if (rules.Skills is not { } skills) yield break;
        foreach (var attribute in system.Attributes)
        {
            yield return new GrantName($"{Save}.{attribute.Key}", $"{Save}.{attribute.Key}");
            yield return new GrantName($"{Check}.{attribute.Key}", $"{Check}.{attribute.Key}");
        }

        foreach (var skill in skills.List) yield return new GrantName($"{Check}.{Skill}.{skill.Key}", $"{Check}.{Skill}.{skill.Key}");
    }

    /// <summary>A dice amount as the roll's expression takes it: with its sign (<c>+1d4</c>, <c>-1d4</c>), no spaces, <c>d4</c> as <c>1d4</c>.</summary>
    public static string SignedDice(string amount)
    {
        var text = amount.Replace(" ", string.Empty);
        var negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        if (text.StartsWith('d') || text.StartsWith('D')) text = "1" + text;
        return (negative ? "-" : "+") + text.ToLower(CultureInfo.InvariantCulture);
    }
}
