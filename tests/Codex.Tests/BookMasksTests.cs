using System.Globalization;
using System.Xml.Linq;
using Codex.Core.Book;

namespace Codex.Tests;

public class BookMasksTests
{
    private static string Edge(ulong seed = 7) => BookMasks.PageEdge(seed, 210, 297);

    [Fact]
    public void SameSeed_SameShape_AndADifferentSeed_AnotherOne()
    {
        Assert.Equal(Edge(7), Edge(7));
        Assert.NotEqual(Edge(7), Edge(8));
        if (Environment.GetEnvironmentVariable("CODEX_DUMP_SVG") is { Length: > 0 } dump) File.WriteAllText(dump, Edge(7));
    }

    [Theory]
    [InlineData(1UL, 210.0, 297.0)]
    [InlineData(2UL, 215.9, 279.4)]
    [InlineData(ulong.MaxValue, 100.0, 100.0)]
    public void TheShape_IsWellFormed_PlainPaths_AndNeverLeavesThePageOrReachesTheContent(ulong seed, double w, double h)
    {
        var svg = BookMasks.PageEdge(seed, w, h, "#ffffff", "#b8a77c", new BookMasks.EdgeStyle(Depth: 50, Roughness: 50, CornerChip: 500));
        var doc = XDocument.Parse(svg); // throws unless it is well-formed
        var elements = doc.Descendants().Select(e => e.Name.LocalName).Distinct().Order().ToList();
        Assert.Equal(new[] { "path", "svg" }, elements);
        // Nothing that can fetch, run or restyle: no href, style, script, filter, image, use.
        Assert.DoesNotContain(doc.Descendants().SelectMany(e => e.Attributes()), a => a.Name.LocalName is "href" or "style" or "onload" or "filter" or "mask" or "clip-path");
        Assert.DoesNotContain("url(", svg, StringComparison.Ordinal);

        // Every point of the ragged line stays on the page, and never deeper than the limit however wild the style said.
        var d = doc.Descendants().First(e => e.Name.LocalName == "path" && e.Attribute("fill-rule") != null).Attribute("d")!.Value;
        var inner = d[(d.IndexOf("Z", StringComparison.Ordinal) + 1)..];
        var points = System.Text.RegularExpressions.Regex.Matches(inner, @"[ML]([\d.]+) ([\d.]+)").Select(m => (X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        Assert.True(points.Count >= 400);
        Assert.All(points, p =>
        {
            Assert.InRange(p.X, 0, w);
            Assert.InRange(p.Y, 0, h);
        });
        var limit = BookMasks.EdgeStyle.MaxInset + 0.01;
        Assert.All(points.Where(p => p.X > limit && p.X < w - limit), p => Assert.True(p.Y <= limit || p.Y >= h - limit, $"({p.X},{p.Y})"));
    }

    [Fact]
    public void AColourIsAHexColourOrItIsIgnored()
    {
        var svg = BookMasks.PageEdge(1, 210, 297, "\"/><script>alert(1)</script>", "red");
        Assert.DoesNotContain("script", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#ffffff\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCssValueHoldsNoQuoteOrBracketThatCouldEndTheUrl()
    {
        var value = BookMasks.DataUri(Edge());
        Assert.StartsWith("url(\"data:image/svg+xml;base64,", value, StringComparison.Ordinal);
        Assert.Equal(1, value.Count(c => c == ')'));
        Assert.Equal(2, value.Count(c => c == '"'));
    }

    [Fact]
    public void TheEdgeIsPulledBackByTheMargins_ThePlainPageActuallyUses()
    {
        // Chromium paints an @page image from the corner of the content box. If the margins change and this does not, the whole
        // edge slides off the page, and nothing else would notice, so the two numbers are tied together here.
        var layout = new BookLayout("A4", false);
        var plain = BookThemes.Plain.Css(layout);
        var parchment = BookThemes.Parchment.Css(layout);
        Assert.Contains("margin: 2cm 1.9cm 2.2cm", plain, StringComparison.Ordinal);
        Assert.Contains("background-position: -19mm -20mm", parchment, StringComparison.Ordinal);
        Assert.Contains("background-size: 210mm 297mm", parchment, StringComparison.Ordinal);
        Assert.Contains("background-size: 215.9mm 279.4mm", BookThemes.Parchment.Css(new BookLayout("Letter", false)), StringComparison.Ordinal);
    }

    [Fact]
    public void Parchment_PrintsWhatPlainPrints_AndNothingMore()
    {
        Assert.Equal(BookThemes.Plain.Languages.Order(), BookThemes.Parchment.Languages.Order());
        Assert.Equal(BookThemes.Plain.Fonts, BookThemes.Parchment.Fonts);
        Assert.Same(BookThemes.Parchment, BookThemes.Find("PARCHMENT"));
    }
}
