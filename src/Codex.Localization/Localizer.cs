using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;

namespace Codex.Localization;

/// <summary>
/// Looks up UI text for the current UI culture. Keys are stable dotted ids whose first segment names
/// the resource file that holds them (<c>Combat.Round</c> lives in <c>Resources/Strings.Combat.resx</c>;
/// a key whose area has no file lives in <c>Strings.resx</c>). English is the fallback and a key missing
/// everywhere renders as the key itself, so gaps are visible rather than blank.
/// </summary>
public sealed class Localizer
{
    private const string BaseName = "Codex.Localization.Resources.Strings";

    private static readonly ConcurrentDictionary<string, ResourceManager> Managers = new();
    private static readonly Lazy<string[]> AreaNames = new(DiscoverAreas);

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
        var template = ManagerFor(key).GetString(key, culture);
        if (template is null)
            return key;

        return args.Length == 0 ? Unescape(template) : MessageFormatter.Format(template, culture, args);
    }

    public bool Has(string key) => ManagerFor(key).GetString(key, CultureInfo.InvariantCulture) is not null;

    /// <summary>Every key the given language defines, across all areas, for parity checks ("en" = the neutral files).</summary>
    public static IReadOnlyCollection<string> Keys(string? culture = null)
    {
        var info = string.IsNullOrEmpty(culture) || culture == "en"
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(culture);

        var keys = new List<string>();
        foreach (var area in AreaNames.Value.Prepend(""))
        {
            var set = ManagerForArea(area).GetResourceSet(info, createIfNotExists: true, tryParents: false);
            if (set is null)
                continue;
            var e = set.GetEnumerator();
            while (e.MoveNext())
                keys.Add((string)e.Key);
        }
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static ResourceManager ManagerFor(string key)
    {
        var dot = key.IndexOf('.');
        var area = dot > 0 ? key[..dot] : "";
        return ManagerForArea(Array.IndexOf(AreaNames.Value, area) >= 0 ? area : "");
    }

    private static ResourceManager ManagerForArea(string area) =>
        Managers.GetOrAdd(area, a => new ResourceManager(a.Length == 0 ? BaseName : $"{BaseName}.{a}", typeof(Localizer).Assembly));

    /// <summary>Areas are whatever <c>Strings.&lt;Area&gt;.resx</c> files exist, so adding one needs no code change.</summary>
    private static string[] DiscoverAreas() =>
        typeof(Localizer).Assembly.GetManifestResourceNames()
            .Where(n => n != BaseName + ".resources"
                        && n.StartsWith(BaseName + ".", StringComparison.Ordinal)
                        && n.EndsWith(".resources", StringComparison.Ordinal))
            .Select(n => n[(BaseName.Length + 1)..^".resources".Length])
            .Where(a => a.Length > 0 && !a.Contains('.'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string Unescape(string template) =>
        template.Contains("{{", StringComparison.Ordinal) || template.Contains("}}", StringComparison.Ordinal)
            ? template.Replace("{{", "{").Replace("}}", "}")
            : template;
}
