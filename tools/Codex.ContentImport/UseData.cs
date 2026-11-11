using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.ContentImport;

/// <summary>
/// The <c>use</c> property of a spell or a weapon: what using it rolls, as data the sheet's actions read
/// (<c>sheetRules.actions</c>): an attack, a saving throw, the dice, and how the dice grow. The source says some of it in
/// fields and the rest in the rules text, so the text is read for the sentences the SRD always writes the same way.
/// </summary>
public static class UseData
{
    private static readonly (string Name, string Key)[] Abilities =
    {
        ("Strength", "str"), ("Dexterity", "dex"), ("Constitution", "con"), ("Intelligence", "int"), ("Wisdom", "wis"), ("Charisma", "cha")
    };

    private static readonly HashSet<string> DamageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Acid", "Bludgeoning", "Cold", "Fire", "Force", "Lightning", "Necrotic", "Piercing", "Poison", "Psychic", "Radiant", "Slashing", "Thunder"
    };

    private const string Dice = @"\d+d\d+";
    private static readonly Regex PlainDice = new($@"^{Dice}(\+\d+)?$", RegexOptions.Compiled);
    private static readonly Regex SpellAttack = new(@"\b(melee|ranged) spell attack", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Save = new($@"\b({string.Join('|', Abilities.Select(a => a.Name))}) saving throw", RegexOptions.Compiled);
    private static readonly Regex DamageDice = new($@"({Dice}(?:\s*\+\s*\d+)?) (?:([A-Z][a-z]+) )?damage", RegexOptions.Compiled);
    private static readonly Regex DamageEqual = new($@"(?:([A-Z][a-z]+) )?damage equal to ({Dice})", RegexOptions.Compiled);
    private static readonly Regex Healing = new($@"(?:regains?|restor\w+) (?:a number of )?Hit Points equal to ({Dice})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlusAbility = new($@"{Dice} (?:plus|\+) your spellcasting ability modifier", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PerSlot = new($@"(?:damage|healing)[^.]*? increases by ({Dice}) for (?:each|every) (?:spell )?slot level above", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AtLevels = new($@"levels? 5 \(({Dice})\), 11 \(({Dice})\),? and 17 \(({Dice})\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A spell's use, or null when it rolls nothing. <paramref name="text"/> is its rules text with the
    /// higher-level paragraph; the last three are the source's own fields, where it has them.</summary>
    public static Dictionary<string, object>? ForSpell(string text, string? attackType = null, string? sourceDice = null, string? sourceDamageType = null)
    {
        var use = new Dictionary<string, object>();

        var attack = attackType?.ToLowerInvariant() ?? (SpellAttack.Match(text) is { Success: true } a ? a.Groups[1].Value.ToLowerInvariant() : null);
        if (attack is "melee" or "ranged") use["attack"] = attack;

        if (Save.Match(text) is { Success: true } save) use["save"] = Abilities.First(x => x.Name == save.Groups[1].Value).Key;

        string? dice = null, type = sourceDamageType;
        var heals = false;
        var compact = sourceDice?.Replace(" ", "");
        if (compact != null && PlainDice.IsMatch(compact)) dice = compact;
        if (dice == null && DamageDice.Match(text) is { Success: true } damage)
        {
            dice = damage.Groups[1].Value.Replace(" ", "");
            if (type == null && DamageTypes.Contains(damage.Groups[2].Value)) type = damage.Groups[2].Value;
        }

        if (dice == null && DamageEqual.Match(text) is { Success: true } equal)
        {
            dice = equal.Groups[2].Value;
            if (type == null && DamageTypes.Contains(equal.Groups[1].Value)) type = equal.Groups[1].Value;
        }

        if (dice == null && Healing.Match(text) is { Success: true } healing)
        {
            dice = healing.Groups[1].Value;
            heals = true;
            type = null;
        }

        if (dice != null)
        {
            use["damage"] = dice;
            if (heals) use["heals"] = true;
            else if (type != null) use["damageType"] = type;
            if (PlusAbility.IsMatch(text)) use["addAbility"] = true;
            if (PerSlot.Match(text) is { Success: true } perSlot) use["perSlot"] = perSlot.Groups[1].Value;
            if (AtLevels.Match(text) is { Success: true } levels)
            {
                use["atLevels"] = new Dictionary<string, object> { ["5"] = levels.Groups[1].Value, ["11"] = levels.Groups[2].Value, ["17"] = levels.Groups[3].Value };
            }
        }

        return use.Count == 0 ? null : use;
    }

    /// <summary>A weapon's use: an attack with Strength, with Dexterity when it is a ranged weapon, with the better of the
    /// two when it has Finesse; its dice; and what it deals held in two hands, when that differs.</summary>
    public static Dictionary<string, object>? ForWeapon(string? dice, string? damageType, string? twoHandedDice, IReadOnlyCollection<string> properties, bool ranged)
    {
        if (string.IsNullOrWhiteSpace(dice)) return null;
        var finesse = properties.Contains("Finesse", StringComparer.OrdinalIgnoreCase);
        var use = new Dictionary<string, object>
        {
            ["attack"] = ranged ? "ranged" : "melee",
            ["abilities"] = (ranged ? new[] { "dex" } : finesse ? new[] { "str", "dex" } : new[] { "str" }).Cast<object>().ToList()
        };

        // A blowgun deals 1 damage: no die, a plain number.
        use["damage"] = int.TryParse(dice, NumberStyles.None, CultureInfo.InvariantCulture, out var flat) ? (long)flat : dice.Replace(" ", "");
        if (!string.IsNullOrWhiteSpace(damageType)) use["damageType"] = damageType;
        if (!string.IsNullOrWhiteSpace(twoHandedDice)) use["alternatives"] = new Dictionary<string, object> { ["two-handed"] = twoHandedDice.Replace(" ", "") };
        return use;
    }
}
