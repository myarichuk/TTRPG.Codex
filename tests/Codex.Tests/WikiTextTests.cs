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

    [Fact]
    public void ExtractLinks_IgnoresStoredMentions()
    {
        var body = "Meet [[actor:abc]] at [[place:def]] with [[item:weapon/sword]], then see [[Greyharbour]].";

        Assert.Equal(new[] { "Greyharbour" }, WikiText.ExtractLinks(body));
        Assert.True(WikiText.HasMentions(body));
        Assert.False(WikiText.HasMentions("Only [[Greyharbour]] here."));
    }

    [Fact]
    public void ToHtml_RendersMentionsThroughTheCallback_NotAsMissingPages()
    {
        var html = WikiText.ToHtml(
            "Ask [[actor:abc]] about [[Greyharbour]] and [[place:d1]].",
            title => title == "Greyharbour" ? "/w/greyharbour" : null,
            title => "/w/new?title=" + title,
            (kind, id) => $"<a class=\"mention\">{kind}:{id}</a>");

        Assert.Contains("<a class=\"mention\">actor:abc</a>", html);
        Assert.Contains("<a class=\"mention\">place:d1</a>", html);
        Assert.Contains("<a class=\"wiki-link\" href=\"/w/greyharbour\">Greyharbour</a>", html);
        Assert.DoesNotContain("wiki-missing", html);
        Assert.DoesNotContain("[[", html);
    }

    [Fact]
    public void ToHtml_WithoutAMentionCallback_DropsTheTokenRatherThanShowingIt()
    {
        var html = WikiText.ToHtml("Hello [[actor:abc]].", _ => null);

        Assert.DoesNotContain("actor:abc", html);
        Assert.DoesNotContain("wiki-missing", html);
    }
}
