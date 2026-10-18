using Codex.Core;

namespace Codex.Tests;

public class WikiTextTests
{
    [Fact]
    public void ExtractLinks_FindsTitlesAndAliases_Deduplicated()
    {
        var links = WikiText.ExtractLinks("Go to [[Greyharbour]] then [[The Docks|the docks]] and back to [[greyharbour]].");

        Assert.Equal(new[] { "Greyharbour", "The Docks" }, links);
    }

    [Fact]
    public void Slugify_IsStableAndUrlSafe()
    {
        Assert.Equal("the-docks", WikiText.Slugify("  The Docks! "));
        Assert.Equal("page", WikiText.Slugify("???"));
    }

    [Fact]
    public void ToHtml_RendersExistingLinks_MissingLinksAndEncodesEverythingElse()
    {
        var html = WikiText.ToHtml(
            "See [[Greyharbour]] and [[Nowhere|a place]] <script>x</script> **bold**",
            title => title == "Greyharbour" ? "/c/1/wiki/greyharbour" : null,
            title => "/c/1/wiki/new?title=" + Uri.EscapeDataString(title));

        Assert.Contains("<a class=\"wiki-link\" href=\"/c/1/wiki/greyharbour\">Greyharbour</a>", html);
        Assert.Contains("wiki-missing", html);
        Assert.Contains(">a place</a>", html);
        Assert.Contains("title=Nowhere", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("<strong>bold</strong>", html);
    }

    [Fact]
    public void ToHtml_WithoutCreateLinks_RendersMissingAsPlainSpan()
    {
        var html = WikiText.ToHtml("[[Secret page]]", _ => null);

        Assert.Contains("<span class=\"wiki-missing\">Secret page</span>", html);
        Assert.DoesNotContain("<a", html);
    }
}
