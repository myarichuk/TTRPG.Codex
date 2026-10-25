using System.Globalization;

namespace Codex.Localization;

/// <summary>Keeps numbers, dice and signed values reading left to right inside right-to-left text.</summary>
public static class Bidi
{
    private const char LeftToRightIsolate = '⁦';
    private const char PopDirectionalIsolate = '⁩';

    /// <summary>
    /// "+2" or "7 / 34" in a Hebrew sentence would otherwise reorder ("2+", "34 / 7"). In a right-to-left UI culture the text is
    /// wrapped in a left-to-right isolate; in any other language it is returned untouched, so English output (and everything
    /// that matches on it) never changes.
    /// </summary>
    public static string Ltr(string text) => Ltr(text, CultureInfo.CurrentUICulture);

    public static string Ltr(string text, CultureInfo culture) =>
        text.Length == 0 || !SupportedCultures.IsRtl(culture)
            ? text
            : LeftToRightIsolate + text + PopDirectionalIsolate;
}
