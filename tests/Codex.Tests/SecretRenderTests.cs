using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Tests;

public class SecretRenderTests
{
    private static string Plain(string text) => $"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>";

    [Fact]
    public void TheDmGetsRevealAndHideButtons_OnlyOnBlocksTheyCanChange()
    {
        var dm = DmView.From(Localizer.For("en"), actions: true);
        var html = SecretRender.Html(":::dm\nouter\n:::party\ninner\n:::\n:::\n:::party 2026-10-29T14:05Z\nshown\n:::\n:::for [[actor:aria]]\nhers\n:::\n:::players\ntold\n:::", Plain, dm);

        Assert.Contains("data-secret-block=\"0\" data-secret-to=\"party\"", html);
        // Block 1 is inside a DM block: revealing it would show nothing, so it has no button.
        Assert.DoesNotContain("data-secret-block=\"1\"", html);
        Assert.Contains("data-secret-block=\"2\" data-secret-to=\"dm\"", html);
        Assert.Contains("data-secret-block=\"3\" data-secret-to=\"party\"", html);
        Assert.Contains("data-secret-block=\"3\" data-secret-to=\"dm\"", html);
        // The players' version is already the players': nothing to reveal or hide.
        Assert.DoesNotContain("data-secret-block=\"4\"", html);
        // One id per block, though block 0 is drawn in two parts around the one inside it.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "id=\"secret-0\""));
    }

    [Fact]
    public void APlayerSeesWhatWasRevealedSinceTheyLastLooked_AsNew()
    {
        const string text = "Old.\n\n:::party 2026-10-29T14:05Z\nNews.\n:::\n\n:::party 2026-01-01T00:00Z\nOld news.\n:::";
        var since = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var html = SecretRender.Html(text, Plain, null, since, "New");
        Assert.Contains("<div class=\"secret-new\">", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "secret-new"));
        Assert.Contains("<p>News.</p>", html);
        Assert.DoesNotContain(":::", html);
        // Not looked before, or nothing newer: the text as one, no marks.
        Assert.Equal(Plain("Old.\n\nNews.\n\nOld news."), SecretRender.Html(text, Plain, null));
        Assert.Equal(Plain("Old.\n\nNews.\n\nOld news."), SecretRender.Html(text, Plain, null, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "New"));
    }

    [Fact]
    public void APlayersRender_LeavesOutBlocksTheyMayNotRead_EvenWhenSomeAreNew()
    {
        var html = SecretRender.Html("a\n:::dm\nsecret\n:::\n:::party 2026-10-29T14:05Z\nnews\n:::\n:::for [[actor:x]] 2026-10-29T14:05Z\nhers\n:::", Plain, null, DateTime.MinValue, "New");
        Assert.DoesNotContain("secret", html.Replace("secret-new", "").Replace("secret-label", ""));
        Assert.DoesNotContain("hers", html);
        Assert.Contains("news", html);
    }
}
