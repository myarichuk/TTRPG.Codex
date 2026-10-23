using System.Globalization;

namespace Codex.Localization;

/// <summary>The languages the app ships and the rules for choosing between them.</summary>
public static class SupportedCultures
{
    public const string Default = "en";

    /// <summary>Language tag and the language's own name (never translated, so a lost user can find theirs).</summary>
    public static readonly IReadOnlyList<(string Tag, string NativeName)> All =
    [
        ("en", "English"),
        ("ru", "Русский"),
        ("he", "עברית"),
    ];

    public static IReadOnlyList<string> Tags { get; } = All.Select(c => c.Tag).ToArray();

    /// <summary>Maps any culture name (<c>ru-RU</c>, <c>iw</c>, <c>HE</c>) to a supported tag, or the default.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Default;

        var language = name.Trim().Split('-', '_')[0].ToLowerInvariant();
        if (language == "iw")
            language = "he";

        return Tags.Contains(language) ? language : Default;
    }

    public static bool IsSupported(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Tags.Contains(name.Trim().ToLowerInvariant());

    public static CultureInfo Resolve(string? name) => CultureInfo.GetCultureInfo(Normalize(name));

    public static bool IsRtl(CultureInfo culture) => Normalize(culture.Name) == "he";

    public static bool IsRtl(string? name) => Normalize(name) == "he";

    public static string Direction(string? name) => IsRtl(name) ? "rtl" : "ltr";
}
