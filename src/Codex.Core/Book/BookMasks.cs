using System.Globalization;
using System.Text;

namespace Codex.Core.Book;

/// <summary>
/// Shapes for a book's decoration, drawn as plain SVG paths: no filter, no script, no reference to anything else, so the
/// browser keeps them as vector in the PDF (docs/BOOK_EXPORT.md 9 measured that a filtered full-page background is rasterised
/// per page and a plain path is not). Every shape comes from a seed, so the same book prints the same edges every time and a
/// shape can be reproduced, tested and changed on purpose.
/// </summary>
public static class BookMasks
{
    /// <summary>A small, stable generator (SplitMix64): the framework's seeded <c>Random</c> is not promised to stay the same across versions.</summary>
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public double Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) / (double)(1UL << 53);
        }

        public double Signed() => Next() * 2 - 1;
    }

    /// <summary>
    /// How far the torn edge cuts in, and how jagged it is, in millimetres. The edge never reaches the content: <see cref="MaxInset"/>
    /// is below the smallest page margin the themes use.
    /// </summary>
    public sealed record EdgeStyle(double Depth = 3.2, double Roughness = 1.6, double CornerChip = 7.5, int Detail = 7)
    {
        /// <summary>The deepest the line can ever go, whatever the style says: what keeps text from being cut.</summary>
        public const double MaxInset = 12;
    }

    /// <summary>
    /// A page-sized SVG whose ragged line separates the page (transparent inside) from the edge (filled with <paramref name="outside"/>,
    /// the colour of the paper the page is printed on). <paramref name="widthMm"/> by <paramref name="heightMm"/> is the page;
    /// stretching it to the page box is exact, since the units are millimetres.
    /// </summary>
    public static string PageEdge(ulong seed, double widthMm, double heightMm, string outside = "#ffffff", string line = "#b8a77c", EdgeStyle? style = null)
    {
        style ??= new EdgeStyle();
        var rng = new Rng(seed);
        var w = Math.Round(widthMm, 2);
        var h = Math.Round(heightMm, 2);

        var steps = 1 << style.Detail;
        var depth = Math.Clamp(style.Depth, 0.4, EdgeStyle.MaxInset / 2);
        var rough = Math.Clamp(style.Roughness, 0, depth * 0.9);
        var chip = Math.Clamp(style.CornerChip, depth, EdgeStyle.MaxInset);

        // One inset per corner: the corner is a single vertex (k, k) from the two sides it joins, so the sides always meet. The
        // corners differ, and each one bites in by its own amount, like a page whose corner was torn off.
        var k = new double[4];
        for (var c = 0; c < 4; c++) k[c] = depth + rng.Next() * (chip - depth);

        // Clockwise from the top left: top, right, bottom, left. Every side's line runs between its two corner vertices, a fixed
        // distance in from the page edge that slides from one corner's inset to the other's, and the tear is added on top of it.
        var points = new List<(double X, double Y)>();
        double Inset(double from, double to, double t, double tear) =>
            Math.Clamp(from + (to - from) * t + tear, Math.Min(0.35, depth), EdgeStyle.MaxInset);

        var top = Tear(rng, steps, rough);
        var right = Tear(rng, steps, rough);
        var bottom = Tear(rng, steps, rough);
        var left = Tear(rng, steps, rough);
        for (var i = 0; i < steps; i++)
        {
            var t = (double)i / steps;
            points.Add((k[0] + (w - k[1] - k[0]) * t, Inset(k[0], k[1], t, top[i])));
        }

        for (var i = 0; i < steps; i++)
        {
            var t = (double)i / steps;
            points.Add((w - Inset(k[1], k[2], t, right[i]), k[1] + (h - k[2] - k[1]) * t));
        }

        for (var i = 0; i < steps; i++)
        {
            var t = (double)i / steps;
            points.Add((w - k[2] - (w - k[2] - k[3]) * t, h - Inset(k[2], k[3], t, bottom[i])));
        }

        for (var i = 0; i < steps; i++)
        {
            var t = (double)i / steps;
            points.Add((Inset(k[3], k[0], t, left[i]), h - k[3] - (h - k[3] - k[0]) * t));
        }

        var path = new StringBuilder();
        for (var i = 0; i < points.Count; i++)
        {
            path.Append(i == 0 ? 'M' : 'L').Append(Num(points[i].X)).Append(' ').Append(Num(points[i].Y));
        }

        path.Append('Z');

        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(Num(w)).Append(' ').Append(Num(h)).Append("\" preserveAspectRatio=\"none\">");
        // The outer rectangle and the ragged shape, filled where they do not overlap: the edge is what is left between them.
        svg.Append("<path fill=\"").Append(Colour(outside)).Append("\" fill-rule=\"evenodd\" d=\"M0 0H").Append(Num(w)).Append('V').Append(Num(h)).Append("H0Z").Append(path).Append("\"/>");
        svg.Append("<path fill=\"none\" stroke=\"").Append(Colour(line)).Append("\" stroke-width=\".35\" stroke-linejoin=\"round\" d=\"").Append(path).Append("\"/>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>The shape as a CSS <c>url()</c> value.</summary>
    public static string DataUri(string svg) => "url(\"data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)) + "\")";

    // Midpoint displacement: a line that is rough at every scale, not at one, which is what a torn edge looks like. The ends stay
    // at nought, so the tear never moves a corner.
    private static double[] Tear(Rng rng, int steps, double roughness)
    {
        var offsets = new double[steps + 1];
        var amplitude = roughness;
        for (var span = steps; span > 1; span /= 2)
        {
            for (var at = 0; at + span <= steps; at += span)
            {
                offsets[at + span / 2] = (offsets[at] + offsets[at + span]) / 2 + rng.Signed() * amplitude;
            }

            amplitude *= 0.62;
        }

        return offsets;
    }

    private static string Num(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    // A colour goes into an attribute: only a plain hex colour gets through, so a theme cannot smuggle markup in.
    private static string Colour(string value) =>
        value.Length is 4 or 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit) ? value : "#ffffff";
}
