using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>Picture frames and torn page edges (docs/BOOK_EXPORT.md 46): plain paths only, the same for the same seed, and a page's edge never
/// deeper than the footer can bear.</summary>
public class BookMasksFrameTests
{
    private static readonly Regex Element = new(@"<(\w+)", RegexOptions.CultureInvariant);

    private static IEnumerable<(double X, double Y)> Points(string d) =>
        Regex.Matches(d, @"[ML](-?[\d.]+) (-?[\d.]+)").Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));

    [Fact]
    public void AFrame_AndAnEdge_ArePlainPaths_TheSameForTheSameSeed()
    {
        foreach (var svg in new[] { BookMasks.Frame(7), BookMasks.FibreEdge(7, 215.9, 279.4) })
        {
            Assert.Equal(["svg", "path"], Element.Matches(svg).Select(m => m.Groups[1].Value).Distinct());
            Assert.DoesNotMatch("filter|href|style|script|dasharray", svg);
        }

        Assert.Equal(BookMasks.Frame(7), BookMasks.Frame(7));
        Assert.NotEqual(BookMasks.Frame(7), BookMasks.Frame(8));
        Assert.Equal(BookMasks.FibreEdge(7, 210, 297), BookMasks.FibreEdge(7, 210, 297));
    }

    [Fact]
    public void ATornEdge_StaysWithinFourAndAHalfMillimetresOfThePagesEdge_AndAFrameWithinNinePercent()
    {
        var edge = BookMasks.FibreEdge(11, 210, 297);
        // The ragged line (the last path), every point of it within reach of the page's own edge, and inside the page.
        var line = Regex.Matches(edge, "d=\"([^\"]+)\"").Last().Groups[1].Value;
        foreach (var (x, y) in Points(line))
        {
            Assert.InRange(x, 0, 210);
            Assert.InRange(y, 0, 297);
            Assert.True(Math.Min(Math.Min(x, 210 - x), Math.Min(y, 297 - y)) <= 4.5, $"{x},{y}");
        }

        var frame = Regex.Match(BookMasks.Frame(11), "d=\"([^\"]+)\"").Groups[1].Value;
        Assert.All(Points(frame), p => Assert.True(Math.Min(Math.Min(p.X, 100 - p.X), Math.Min(p.Y, 100 - p.Y)) <= 9, p.ToString()));
    }

    [Fact]
    public void TheShell_HasFramesInEveryTheme_AndTornEdgesOnlyWhenAsked()
    {
        foreach (var theme in BookDraftThemes.All)
        {
            var plain = BookDraftHtml.Shell("T", "en", theme, "A4", null, true, _ => null).Html!;
            Assert.Contains(".page .block.torn img", plain);
            Assert.Contains(".page .block:is(.oval, .round)", plain);
            Assert.DoesNotContain(".page::before", plain);
            Assert.Contains(".page::before", BookDraftHtml.Shell("T", "en", theme, "A4", null, true, _ => null, edge: true).Html!);
        }
    }
}
