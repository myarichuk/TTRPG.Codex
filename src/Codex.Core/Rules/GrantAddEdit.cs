using System.Globalization;

namespace Codex.Core.Rules;

/// <summary>
/// How the feature editor reads and writes one <c>adds</c> row of a ranked list (skills, saving throws): which of three kinds it is
/// (<see cref="FixedMode"/>: entries at a rank, <see cref="RaiseMode"/>: entries a step up, <see cref="ChoiceMode"/>: left to the player),
/// and the keys only a choice has (<c>choose</c>, <c>from</c>, <c>minRank</c>, <c>maxRank</c>, the choice id). They live in
/// <see cref="GrantAdd.Extra"/>, so a row the editor does not understand round-trips untouched.
/// </summary>
public static class GrantAddEdit
{
    public const string FixedMode = "fixed";
    public const string RaiseMode = "raise";
    public const string ChoiceMode = "choice";

    /// <summary>The <c>from</c> kinds of a choice: every key, these keys, or a filter on the keys' attribute (saving throws).</summary>
    public const string FromAny = "any";
    public const string FromThese = "these";
    public const string FromAttribute = "attribute";

    private static readonly string[] ChoiceKeys = { "choice", "choose", "from", "minRank", "maxRank" };

    /// <summary>A row with a <c>choice</c> or <c>choose</c> is a choice; else one with <c>raise</c> a raise; else fixed.</summary>
    public static string ModeOf(GrantAdd add) =>
        add.Extra.ContainsKey("choice") || add.Extra.ContainsKey("choose") ? ChoiceMode : add.Extra.ContainsKey("raise") ? RaiseMode : FixedMode;

    /// <summary>Makes the row <paramref name="mode"/>, taking out the keys the new kind does not use. A choice gets a count of 1 and, when
    /// <paramref name="defaultId"/> is given, that id (the row's slug and level); its entries are cleared, since the player picks them.</summary>
    public static void SetMode(GrantAdd add, string mode, string? defaultId = null)
    {
        switch (mode)
        {
            case FixedMode:
                foreach (var key in ChoiceKeys.Append("raise")) add.Extra.Remove(key);
                break;
            case RaiseMode:
                foreach (var key in ChoiceKeys.Append("rank")) add.Extra.Remove(key);
                add.Extra["raise"] = 1L;
                break;
            case ChoiceMode:
                add.Entries.Clear();
                if (!add.Extra.ContainsKey("choose")) add.Extra["choose"] = 1L;
                if (!add.Extra.ContainsKey("choice") && !string.IsNullOrWhiteSpace(defaultId)) add.Extra["choice"] = defaultId;
                break;
        }
    }

    /// <summary>A text key of the row (<c>rank</c>, <c>minRank</c>, <c>choice</c>), or empty.</summary>
    public static string Text(GrantAdd add, string key) =>
        add.Extra.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;

    /// <summary>Sets a text key, or takes it out when <paramref name="value"/> is empty.</summary>
    public static void SetText(GrantAdd add, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) add.Extra.Remove(key);
        else add.Extra[key] = value.Trim();
    }

    /// <summary>A whole-number key of the row (<c>choose</c>, <c>raise</c>), or <paramref name="fallback"/>.</summary>
    public static int Number(GrantAdd add, string key, int fallback) =>
        add.Extra.TryGetValue(key, out var value) && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (int)n : fallback;

    public static void SetNumber(GrantAdd add, string key, int value) => add.Extra[key] = (long)value;

    /// <summary>Whether a choice offers every key, listed keys, or those whose attribute is listed.</summary>
    public static string FromKind(GrantAdd add) => add.Extra.GetValueOrDefault("from") switch
    {
        null => FromAny,
        string text when text.Trim().Length == 0 || text.Trim().Equals(SheetRuleEngine.AnyKeys, StringComparison.OrdinalIgnoreCase) => FromAny,
        IDictionary<string, object> => FromAttribute,
        _ => FromThese
    };

    /// <summary>The keys listed in <c>from</c> (or in its attribute filter).</summary>
    public static List<string> FromKeys(GrantAdd add) => add.Extra.GetValueOrDefault("from") switch
    {
        IDictionary<string, object> map => map.Values.SelectMany(ListOf).ToList(),
        var other when FromKind(add) == FromThese => ListOf(other).ToList(),
        _ => new List<string>()
    };

    /// <summary>Writes <c>from</c>: <c>any</c> (left out, since it is the default), the keys, or <c>{ attribute: [keys] }</c>.</summary>
    public static void SetFrom(GrantAdd add, string kind, IEnumerable<string> keys)
    {
        var list = keys.Where(k => k.Trim().Length > 0).Select(k => k.Trim()).ToList();
        switch (kind)
        {
            case FromThese:
                add.Extra["from"] = list.Cast<object>().ToList();
                break;
            case FromAttribute:
                add.Extra["from"] = new Dictionary<string, object> { ["attribute"] = list.Cast<object>().ToList() };
                break;
            default:
                add.Extra.Remove("from");
                break;
        }
    }

    private static IEnumerable<string> ListOf(object? value) => value switch
    {
        null => Enumerable.Empty<string>(),
        string text => FeatureGrantData.SplitEntries(text),
        IEnumerable<object> many => many.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty).Where(v => v.Length > 0),
        _ => new[] { Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty }
    };

    // ---- Choices over Library entries and values (docs/ENTRY_CHOICES_DESIGN.md) ----
    //
    // `{ to: grantedSpells, choice, choose, where: [...], free }`, `{ choice, choose, from: [values] }` (a value choice, no `to`) and
    // `{ choice, choose, fromList, where, free }` (mark-only, no `to`) all live in Extra, so they round-trip untouched. The editor reads the
    // `where` conditions as a sentence and lets the author change the count (Number/SetNumber "choose") and the `free` part.

    /// <summary>No free casting on the picks.</summary>
    public const string FreeNone = "none";

    /// <summary>The picks cast at will (<c>free: atWill</c>).</summary>
    public const string FreeAtWill = "atWill";

    /// <summary>Each pick casts a limited number of times, a counter map like <c>uses</c> (<c>free: { max: 1, recoversOn: [Long Rest] }</c>).</summary>
    public const string FreeUses = "uses";

    /// <summary>One condition of a choice's <c>where</c>, as the editor shows it: the property, the comparison (<see cref="CondEquals"/> and the like)
    /// and the value as text (a list joined with commas).</summary>
    public sealed record GrantCondition(string Property, string Kind, string Value);

    public const string CondEquals = "equals";
    public const string CondIn = "in";
    public const string CondAtLeast = "gte";
    public const string CondAtMost = "lte";
    public const string CondContains = "contains";
    public const string CondContainsChoice = "containsChoice";
    public const string CondEqualsChoice = "equalsChoice";

    /// <summary>Any other comparison (a field, a lookup): shown by its property alone.</summary>
    public const string CondOther = "other";

    private static readonly string[] ComparisonKeys = { CondEquals, CondIn, CondAtLeast, CondAtMost, CondContains, CondContainsChoice, CondEqualsChoice };

    /// <summary>The <c>where</c> conditions of a row, for display. A condition with no property is left out; one with a comparison the editor has
    /// no words for is <see cref="CondOther"/>.</summary>
    public static List<GrantCondition> Conditions(GrantAdd add)
    {
        var list = new List<GrantCondition>();
        var raw = add.Extra.GetValueOrDefault("where");
        var maps = raw switch
        {
            System.Collections.IDictionary one => new object?[] { one },
            string => Array.Empty<object?>(),
            System.Collections.IEnumerable many => many.Cast<object?>(),
            _ => Array.Empty<object?>()
        };
        foreach (var item in maps)
        {
            if (item is not System.Collections.IDictionary map) continue;
            string? Get(string key) => map.Contains(key) && map[key] is { } v ? ConditionText(v) : null;
            var property = Get("property");
            if (string.IsNullOrWhiteSpace(property)) continue;
            var kind = ComparisonKeys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(Get(k)));
            list.Add(kind == null ? new GrantCondition(property, CondOther, string.Empty) : new GrantCondition(property, kind, Get(kind)!));
        }

        return list;
    }

    private static string ConditionText(object value) => value switch
    {
        string text => text.Trim(),
        System.Collections.IEnumerable many => string.Join(", ", many.Cast<object?>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty).Where(v => v.Length > 0)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
    };

    /// <summary>Whether a row is a choice that leaves entries or values to the player rather than keys of a ranked list: it has a choice and
    /// its list is not ranked (<paramref name="isRanked"/>).</summary>
    public static bool IsEntryOrValueChoice(GrantAdd add, bool isRanked) => ModeOf(add) == ChoiceMode && !isRanked;

    /// <summary>How the picks of a choice may be cast: <see cref="FreeNone"/>, <see cref="FreeAtWill"/> or <see cref="FreeUses"/>.</summary>
    public static string FreeKind(GrantAdd add) => add.Extra.GetValueOrDefault("free") switch
    {
        null => FreeNone,
        string text when text.Trim().Length == 0 => FreeNone,
        string text when text.Trim().Equals(FreeAtWill, StringComparison.OrdinalIgnoreCase) => FreeAtWill,
        _ => FreeUses
    };

    /// <summary>Sets how the picks are cast. Moving to <see cref="FreeUses"/> keeps a counter the row already has; a new one is one use.</summary>
    public static void SetFreeKind(GrantAdd add, string kind)
    {
        switch (kind)
        {
            case FreeAtWill:
                add.Extra["free"] = FreeAtWill;
                break;
            case FreeUses:
                add.Extra["free"] = FreeKind(add) != FreeUses ? new Dictionary<string, object> { ["max"] = 1L } : FreeMap(add);
                break;
            default:
                add.Extra.Remove("free");
                break;
        }
    }

    /// <summary>The uses of a free counter as text (a number or a formula); "1" when there is none yet.</summary>
    public static string FreeMax(GrantAdd add)
    {
        var map = FreeMap(add);
        return map.TryGetValue("max", out var max) && Convert.ToString(max, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : "1";
    }

    public static void SetFreeMax(GrantAdd add, string? text)
    {
        var map = FreeMap(add);
        map["max"] = FeatureGrantData.Value(string.IsNullOrWhiteSpace(text) ? "1" : text);
        add.Extra["free"] = map;
    }

    /// <summary>The rests that refill a free counter (<c>recoversOn</c>).</summary>
    public static List<string> FreeRecoversOn(GrantAdd add) =>
        FreeMap(add).TryGetValue("recoversOn", out var on) ? ListOf(on).ToList() : new List<string>();

    /// <summary>Makes a rest refill the free counter, or not. Other keys of the counter (<c>recovers</c>, <c>key</c>) stay as they are.</summary>
    public static void SetFreeRecovers(GrantAdd add, string rest, bool on)
    {
        var map = FreeMap(add);
        var rests = FreeRecoversOn(add).Where(r => !r.Equals(rest, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) rests.Add(rest);
        if (rests.Count == 0) map.Remove("recoversOn");
        else map["recoversOn"] = rests.Cast<object>().ToList();
        add.Extra["free"] = map;
    }

    /// <summary>The free counter as an editable copy: its keys, or a new map (<c>free: 2</c> is a counter of two).</summary>
    private static Dictionary<string, object> FreeMap(GrantAdd add) => add.Extra.GetValueOrDefault("free") switch
    {
        System.Collections.IDictionary map => map.Keys.Cast<object>().ToDictionary(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? string.Empty, k => map[k]!),
        null or string => new Dictionary<string, object>(),
        var number => new Dictionary<string, object> { ["max"] = number }
    };
}
