using Codex.Core;
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
    public void ARevealInTheSameSecondAsTheLastLook_IsStillNew()
    {
        // Reveals are stamped to the second, the last look to the tick: seen at 14:05:07.700, revealed at 14:05:07.900.
        var revealedAt = new DateTime(2026, 10, 29, 14, 5, 7, 900, DateTimeKind.Utc);
        var text = SecretText.SetAudience("Old.\n\n:::dm\nNews.\n:::", 0, "News.", SecretAudience.Party, null, revealedAt)!;
        Assert.Contains(":::party 2026-10-29T14:05:07Z", text);
        var since = Codex.Web.Components.Campaign.WikiPageText.SeenBefore(new DateTime(2026, 10, 29, 14, 5, 7, 700, DateTimeKind.Utc).ToString("O"));
        Assert.Contains("<div class=\"secret-new\">", SecretRender.Html(text, Plain, null, since, "New"));

        // A look in a later second still leaves it as read, and nothing stored reads as never looked.
        var later = Codex.Web.Components.Campaign.WikiPageText.SeenBefore("2026-10-29T14:05:08.0000000Z");
        Assert.Equal(Plain("Old.\n\nNews."), SecretRender.Html(text, Plain, null, later, "New"));
        Assert.Null(Codex.Web.Components.Campaign.WikiPageText.SeenBefore(null));
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
