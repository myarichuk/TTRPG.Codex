namespace Codex.Localization;

/// <summary>CLDR plural categories for the supported languages, for whole numbers.</summary>
public static class PluralRules
{
    public static string Category(string language, long n)
    {
        var i = Math.Abs(n);
        switch (SupportedCultures.Normalize(language))
        {
            case "ru":
                var mod10 = i % 10;
                var mod100 = i % 100;
                if (mod10 == 1 && mod100 != 11) return "one";
                if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return "few";
                return "many";
            case "he":
                return i switch { 1 => "one", 2 => "two", _ => "other" };
            default:
                return i == 1 ? "one" : "other";
        }
    }

    /// <summary>Fractions are always "other" in en/he and "other" in ru, so only whole numbers need the rules.</summary>
    public static string Category(string language, double n) =>
        n == Math.Floor(n) && Math.Abs(n) < long.MaxValue ? Category(language, (long)n) : "other";
}
