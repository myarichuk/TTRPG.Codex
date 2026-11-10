using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>The pictures a draft's chapters name by a web address (docs/BOOK_EXPORT.md 41.25): which addresses are read from the text, and in what form.</summary>
public class BookWebImagesTests
{
    [Fact]
    public void EveryFormOfAPicture_IsFound_InTheOrderOfTheText()
    {
        var found = BookWebImages.Addresses(
            "![Hero](https://i.imgur.example/a.png)\n\n"
            + "![Cover](https://cdn.example/b.jpg) {bottom:-7.4%,left:-416px,height:115%}\n\n"
            + "![Styled](https://cdn.example/c.webp){position:absolute,top:295px}\n\n"
            + "![Titled](https://cdn.example/d.png \"A title\")\n\n"
            + "![Angled](<https://cdn.example/e.png>)\n\n"
            + "<img src=\"https://cdn.example/f.png\" alt=\"x\">\n\n"
            + "<IMG class='k' SRC='https://cdn.example/g.png'>\n\n"
            + "![Wiki](https://wiki.example/File_(1).png)");
        Assert.Equal(
            ["https://i.imgur.example/a.png", "https://cdn.example/b.jpg", "https://cdn.example/c.webp", "https://cdn.example/d.png", "https://cdn.example/e.png",
                "https://cdn.example/f.png", "https://cdn.example/g.png", "https://wiki.example/File_(1).png"], found);
    }

    [Fact]
    public void AnAddress_IsCanonical_AndNamedOnce()
    {
        // The form the renderer looks a picture up by and the server keeps its copy under: host lower case, a path, no fragment, entities decoded.
        var found = BookWebImages.Addresses([
            "![a](HTTPS://CDN.Example/Pic.png?x=1&y=2#frag)\n\n![b](https://cdn.example/Pic.png?x=1&y=2)\n\n<img src=\"https://cdn.example/Pic.png?x=1&amp;y=2\">",
            "![c](https://cdn.example)"]);
        Assert.Equal(["https://cdn.example/Pic.png?x=1&y=2", "https://cdn.example/"], found);
    }

    [Theory]
    [InlineData("![x](art:s0123456789abcdef01234567)")]
    [InlineData("![x](http://cdn.example/a.png)")]
    [InlineData("![x](//cdn.example/a.png)")]
    [InlineData("![x](/assets/a.png)")]
    [InlineData("![x](data:image/png;base64,AAAA)")]
    [InlineData("![x](https://user:pass@cdn.example/a.png)")]
    [InlineData("![x](javascript:alert(1))")]
    [InlineData("[a link, not a picture](https://cdn.example/a.png)")]
    [InlineData("Plain https://cdn.example/a.png text, and <a href=\"https://cdn.example/a.png\">a link</a>.")]
    [InlineData("<img data-src=\"https://cdn.example/a.png\">")]
    [InlineData("")]
    public void WhatIsNotAnHttpsPicture_IsNotAnAddress(string markdown) => Assert.Empty(BookWebImages.Addresses(markdown));

    [Fact]
    public void AnUnreadableOrHugeText_CostsNothingAndIsBounded()
    {
        Assert.Empty(BookWebImages.Addresses((string?)null));
        Assert.Empty(BookWebImages.Addresses([null, ""]));
        var many = string.Concat(Enumerable.Range(0, BookWebImages.MaxAddresses + 50).Select(i => $"![i](https://cdn.example/{i}.png)\n"));
        Assert.Equal(BookWebImages.MaxAddresses, BookWebImages.Addresses(many).Count);
        // A line of open brackets does not make the reader slow (the patterns are bounded).
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(BookWebImages.Addresses("://" + string.Concat(Enumerable.Repeat("![", 20000)) + string.Concat(Enumerable.Repeat("<img src=\"", 5000))));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), started.Elapsed.ToString());
    }
}
