using Codex.Core;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Tests;

/// <summary>
/// A player is never shown the title of a wiki page they may not open: not as a link, not as plain text, not in a card's summary. A link
/// to such a page keeps only its own <c>|shown</c> words. The page list a player is given holds only the pages they may open, so
/// "Lich Tower" below is GM-only exactly by not being in it.
/// </summary>
public class WikiPlayerLinksTests
{
    private const string Body =
        "Seen from [[Lich Tower]], by its slug [[lich-tower]], spelled [[ lich tower ]], with an empty label [[Lich Tower| ]], "
        + "named for the reader [[Lich Tower|the old tower]]. The way to [[Docks]] is open. "
        + "Meet [[actor:a1]], not [[Actor:a2|Varro the Lich]] or [[actor:a2|Varro the Lich]].";

    private static readonly WikiPageDocument Docks = new() { Id = "w1", Slug = "docks", Title = "Docks", Visibility = WikiVisibility.Party };
    private static readonly WikiPageDocument Tower = new() { Id = "w2", Slug = "lich-tower", Title = "Lich Tower", Visibility = WikiVisibility.DmOnly };

    [Fact]
    public void ForReader_KeepsOpenLinks_GivesClosedOnesOnlyTheirLabel_AndDropsMentionLookalikes()
    {
        var text = WikiText.ForReader(Body, title => WikiLinks.TargetOfTitle(title, [Docks]) != null);

        Assert.DoesNotContain("lich", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("named for the reader the old tower.", text);
        Assert.Contains("[[Docks]]", text);
        Assert.Contains("[[actor:a1]]", text);
        Assert.Contains("an empty label , ", text);
    }

    [Fact]
    public void APlayersPage_NeverShowsTheTitleOfAPageTheyMayNotOpen_AndTheDmsShowsEveryLink()
    {
        var player = WikiRender.ToHtml(Body, [Docks], MentionDirectory.Empty, "c1", canCreate: false);

        Assert.DoesNotContain("lich", player, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the old tower", player);
        Assert.Contains("<a class=\"wiki-link\" href=\"/campaigns/c1/wiki/docks\">Docks</a>", player);

        var dm = WikiRender.ToHtml(Body, [Docks, Tower], MentionDirectory.Empty, "c1", canCreate: true, new DmView("GM only", "Party", "Players", "Create"));
        Assert.Contains(">Lich Tower</a>", dm);
    }

    [Fact]
    public void APlayersCard_NeverShowsTheTitleOfAPageTheyMayNotOpen()
    {
        var player = WikiRender.Summary(Body, [Docks], MentionDirectory.Empty, 1000);

        Assert.DoesNotContain("lich", player, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the old tower", player);
        Assert.Contains("Docks", player);

        Assert.Contains("Lich Tower", WikiRender.Summary(Body, [Docks, Tower], MentionDirectory.Empty, 1000, forDm: true));
    }
}
