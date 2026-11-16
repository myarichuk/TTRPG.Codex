using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.ContentImport.Degrees;

/// <summary>One supported printing of a book, by structure only (<c>Data/degrees-books.yaml</c>).</summary>
public sealed class BookFingerprint
{
    public int Printing { get; set; }
    public int Pages { get; set; }
    public List<int> Bookmarks { get; set; } = new();
    public List<string> Notice { get; set; } = new();
    public List<string> Credit { get; set; } = new();
}

public sealed class BooksConfig
{
    public int Normalization { get; set; }
    public Dictionary<string, BookFingerprint> Books { get; set; } = new();

    public static BooksConfig Parse(string yaml) => Deserializer.Deserialize<BooksConfig>(yaml) ?? new BooksConfig();

    public static BooksConfig Embedded() => Parse(EmbeddedYaml("degrees-books.yaml"));

    internal static IDeserializer Deserializer { get; } = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    internal static string EmbeddedYaml(string name)
    {
        using var stream = typeof(BooksConfig).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class FontRule
{
    public string Role { get; set; } = string.Empty;
    public List<string> Family { get; set; } = new();
    public List<double>? Size { get; set; }
    public List<double>? X { get; set; }

    /// <summary>Only pages of this parity: <c>odd</c> or <c>even</c> by PDF page number. Furniture changes sides between the two.</summary>
    public string? Parity { get; set; }
}

/// <summary>Maps a letter's font, size and left edge to a role. The first matching rule wins; no match means unclassified.</summary>
public sealed class FontRoles
{
    private static readonly Regex SubsetTag = new("^[A-Z]{6}\\+", RegexOptions.Compiled);

    public List<FontRule> Rules { get; set; } = new();

    public static FontRoles Parse(string yaml)
    {
        var roles = BooksConfig.Deserializer.Deserialize<FontRoles>(yaml) ?? new FontRoles();
        if (roles.Rules.Any(r => r.Parity is not (null or "odd" or "even")))
        {
            throw new DegreesException("A font rule's parity must be odd or even.");
        }

        return roles;
    }

    public static FontRoles Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-fonts.yaml"));

    /// <summary>The font name without the six-letter subset tag PDF producers put in front of it.</summary>
    public static string Family(string? fontName) => SubsetTag.Replace(fontName ?? "?", string.Empty);

    public string? Classify(string family, double pointSize, double left, int page = 0)
    {
        var size = Math.Round(pointSize, MidpointRounding.AwayFromZero);
        foreach (var rule in Rules)
        {
            if (!rule.Family.Any(f => family.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (rule.Parity != null && (page <= 0 || (rule.Parity == "odd") != (page % 2 == 1)))
            {
                continue;
            }

            if (rule.Size is { Count: 2 } s && (size < s[0] || size > s[1]))
            {
                continue;
            }

            if (rule.X is { Count: 2 } x && (left < x[0] || left > x[1]))
            {
                continue;
            }

            return rule.Role;
        }

        return null;
    }
}
