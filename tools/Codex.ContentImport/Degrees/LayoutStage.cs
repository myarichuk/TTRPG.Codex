using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Codex.ContentImport.Degrees;

/// <summary>One word of the book with the font role of its first letter. Positions are PDF points, origin bottom left.</summary>
public sealed record LayoutWord(int Page, string Text, string Font, double Size, double Left, double Right, double Bottom, double Top, string Role);

public sealed record LayoutPage(int Number, double Width, double Height, List<LayoutWord> Words);

/// <summary>Letters with no font role: counted by font and size, with the first few pages, and never with their text.</summary>
public sealed record UnclassifiedFont(string Font, double Size, int Letters, List<int> FirstPages);

public sealed record LayoutResult(List<LayoutPage> Pages, int Letters, Dictionary<string, int> LettersByRole, List<UnclassifiedFont> Unclassified);

/// <summary>Stage 1: every word of the book with its font, size, box and role, and the check that every letter has a role.</summary>
public static class LayoutStage
{
    public static LayoutResult Run(PdfDocument doc, FontRoles roles)
    {
        var pages = new List<LayoutPage>();
        var lettersByRole = new Dictionary<string, int>();
        var unknown = new Dictionary<(string, double), (int Letters, List<int> Pages)>();
        static (int Letters, List<int> Pages) Seen((int Letters, List<int> Pages) e, int page)
        {
            if (e.Pages.Count < 5 && !e.Pages.Contains(page))
            {
                e.Pages.Add(page);
            }

            return (e.Letters + 1, e.Pages);
        }

        var letters = 0;

        for (var n = 1; n <= doc.NumberOfPages; n++)
        {
            var page = doc.GetPage(n);
            foreach (var l in page.Letters)
            {
                letters++;
                var family = FontRoles.Family(l.FontName);
                var role = roles.Classify(family, l.PointSize, l.GlyphRectangle.Left);
                if (role != null)
                {
                    lettersByRole[role] = lettersByRole.GetValueOrDefault(role) + 1;
                    continue;
                }

                var key = (family, Math.Round(l.PointSize, 1));
                unknown[key] = Seen(unknown.TryGetValue(key, out var e) ? e : (0, new List<int>()), n);
            }

            var words = new List<LayoutWord>();
            foreach (var w in page.GetWords(NearestNeighbourWordExtractor.Instance))
            {
                var first = w.Letters[0];
                var family = FontRoles.Family(first.FontName);
                var role = roles.Classify(family, first.PointSize, first.GlyphRectangle.Left) ?? "unclassified";
                var box = w.BoundingBox;
                words.Add(new LayoutWord(n, w.Text, family, Math.Round(first.PointSize, 1), box.Left, box.Right, box.Bottom, box.Top, role));
            }

            pages.Add(new LayoutPage(n, page.Width, page.Height, words));
        }

        var unclassified = unknown
            .OrderByDescending(k => k.Value.Letters)
            .Select(k => new UnclassifiedFont(k.Key.Item1, k.Key.Item2, k.Value.Letters, k.Value.Pages))
            .ToList();
        return new LayoutResult(pages, letters, lettersByRole, unclassified);
    }
}
