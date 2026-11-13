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
}
