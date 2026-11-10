using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>
/// A stylesheet written for left-to-right pages, mirrored for a right-to-left book (docs/BOOK_EXPORT.md 41.21), the way the rtlcss tool does
/// it. Homebrewery's Blank layout, its PHB layer and Ashen Vellum's sheet use physical sides (<c>padding-left</c> on lists, <c>float: left</c>
/// drop caps, the page number at <c>right</c> on odd pages); in a Hebrew book every one of them belongs on the other side, odd and even pages
/// included, since a right-bound book's odd page is on the left.
/// <para>What is mirrored: <c>left</c> and <c>right</c> in property names (<c>margin-left</c>, <c>border-top-right-radius</c>, <c>left</c>);
/// the keywords <c>left</c> and <c>right</c> in <c>float</c>, <c>clear</c>, <c>text-align</c> and the background positions; the second and
/// fourth of four values in the box shorthands; the corners of a <c>border-radius</c>. What is not: comments, strings and <c>url(...)</c>
/// (pictures and data are kept byte for byte), percentages and lengths in a background position, transforms and shadows. Those are drawing,
/// not reading order, and are left as drawn.</para>
/// <para>Only a theme's own sheets are mirrored, never the GM's stylesheet: what the GM writes for a Hebrew book is theirs as written.</para>
/// </summary>
public static class BookRtl
{
    // Comments, strings and url(...), kept aside so nothing inside them is read as a declaration.
    private static readonly Regex Opaque = new(@"/\*.*?\*/|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|url\(\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^)]*)\s*\)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    // A declaration: after the block's opening brace or the declaration before it, a property name, its value, up to ';' or '}'.
    private static readonly Regex Declaration = new(@"(?<=[{;]\s*)(?<name>-?[a-zA-Z][-a-zA-Z]*)(?<colon>\s*:\s*)(?<value>[^;{}]*?)(?=\s*(?:!important\s*)?[;}])",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly Regex Side = new(@"\b(left|right)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> Boxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "margin", "padding", "border-width", "border-style", "border-color", "inset", "scroll-margin", "scroll-padding"
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "float", "clear", "text-align", "text-align-last", "background-position", "background-position-x", "background", "object-position",
        "mask-position", "transform-origin", "caption-side", "justify-content", "justify-items", "justify-self"
    };

    /// <summary>The sheet with left and right exchanged.</summary>
    public static string Mirror(string css)
    {
        var kept = new List<string>();
        var masked = Opaque.Replace(css, m =>
        {
            kept.Add(m.Value);
            return "\u0001" + (kept.Count - 1) + "\u0002";
        });
        var mirrored = Declaration.Replace(masked, m =>
        {
            var name = m.Groups["name"].Value;
            var value = m.Groups["value"].Value;
            var newName = Side.Replace(name, Swap);
            var newValue = value;
            if (Keywords.Contains(name))
            {
                newValue = Side.Replace(value, Swap);
            }
            else if (Boxes.Contains(name))
            {
                newValue = Four(value, (a, b, c, d) => [a, d, c, b]);
            }
            else if (string.Equals(name, "border-radius", StringComparison.OrdinalIgnoreCase) && !value.Contains('/', StringComparison.Ordinal))
            {
                // top-left top-right bottom-right bottom-left, filled in the CSS way from fewer values.
                newValue = Four(value, (a, b, c, d) => [b, a, d, c], radius: true);
            }

            return newName + m.Groups["colon"].Value + newValue;
        });
        return Regex.Replace(mirrored, "\u0001(\\d+)\u0002", m => kept[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)],
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    }

    private static string Swap(Match m)
    {
        var right = m.Value.Equals("left", StringComparison.OrdinalIgnoreCase);
        var word = right ? "right" : "left";
        return char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(word[0]) + word[1..] : word;
    }

    // Four values in the shorthand's order, reordered; fewer are filled in as CSS does. A value with a function or a variable is left alone:
    // its parts cannot be counted without computing it.
    private static string Four(string value, Func<string, string, string, string, string[]> order, bool radius = false)
    {
        if (value.Contains('(', StringComparison.Ordinal) || value.Contains('\u0001', StringComparison.Ordinal))
        {
            return value;
        }

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4)
        {
            return value;
        }

        string a = parts[0], b = parts[1];
        var c = parts.Length > 2 ? parts[2] : a;
        var d = parts.Length > 3 ? parts[3] : b;
        if (radius && parts.Length == 2)
        {
            // Two radii: top-left/bottom-right, then top-right/bottom-left.
            (c, d) = (a, b);
        }
        else if (radius && parts.Length == 3)
        {
            d = b;
        }

        var result = order(a, b, c, d);
        if (result.SequenceEqual([a, b, c, d], StringComparer.Ordinal))
        {
            // Symmetric already (0 1em): kept as written.
            return value;
        }

        var sb = new StringBuilder();
        foreach (var part in result)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(part);
        }

        return sb.ToString();
    }
}
