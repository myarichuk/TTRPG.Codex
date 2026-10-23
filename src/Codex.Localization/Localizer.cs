using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Codex.Localization;

/// <summary>
/// Looks up UI text for the current UI culture. Keys are stable dotted ids (<c>Combat.Round</c>);
/// English is the fallback, and a key missing everywhere renders as the key so gaps are visible.
/// </summary>
public sealed class Localizer
{
    private static readonly ResourceManager Manager =
        new("Codex.Localization.Resources.Strings", typeof(Localizer).Assembly);

    private readonly CultureInfo? _culture;

    public Localizer() { }

    private Localizer(CultureInfo culture) => _culture = culture;

    /// <summary>A localizer pinned to one culture, for code that has no ambient UI culture (launcher, tests).</summary>
    public static Localizer For(string? culture) => new(SupportedCultures.Resolve(culture));

    public CultureInfo Culture => _culture ?? CultureInfo.CurrentUICulture;

    public string this[string key] => Get(key);

    public string this[string key, params object?[] args] => Get(key, args);

    public string Get(string key, params object?[] args)
    {
        var culture = Culture;
        var template = Manager.GetString(key, culture);
        if (template is null)
            return key;

        return args.Length == 0 ? Unescape(template) : MessageFormatter.Format(template, culture, args);
    }

    public bool Has(string key) => Manager.GetString(key, CultureInfo.InvariantCulture) is not null;

    /// <summary>Every key in the neutral (English) resources, for parity checks.</summary>
    public static IReadOnlyCollection<string> Keys(string? culture = null) => ResourceKeys.Read(culture);

    private static string Unescape(string template) =>
        template.Contains("{{", StringComparison.Ordinal) || template.Contains("}}", StringComparison.Ordinal)
            ? template.Replace("{{", "{").Replace("}}", "}")
            : template;

    internal static ResourceManager ResourceManager => Manager;

    internal static Assembly Assembly => typeof(Localizer).Assembly;
}

internal static class ResourceKeys
{
    public static IReadOnlyCollection<string> Read(string? culture)
    {
        var info = string.IsNullOrEmpty(culture) || culture == "en"
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(culture);
        var set = Localizer.ResourceManager.GetResourceSet(info, createIfNotExists: true, tryParents: false);
        if (set is null)
            return [];

        var keys = new List<string>();
        var e = set.GetEnumerator();
        while (e.MoveNext())
            keys.Add((string)e.Key);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}
