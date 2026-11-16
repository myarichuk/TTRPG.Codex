using Bunit;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Components.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>A placed hazard on a feature's notes: a pill with its state in words, and the hazard's name as a link while it is in the Library.</summary>
public class PlaceFeatureNotesTests : ComponentTestBase
{
    private IRenderedComponent<PlaceFeatureNotes> Draw(string culture, PlacedHazard hazard, string? name = "Spiked Pit", string? href = "/library/hazard/spiked-pit")
    {
        Services.AddSingleton(Localizer.For(culture));
        return Render<PlaceFeatureNotes>(p => p.Add(x => x.Feature, new PlaceFeature { Hazard = hazard }).Add(x => x.HazardName, name).Add(x => x.HazardHref, href));
    }

    [Fact]
    public void TheHazard_IsALinkWithItsStateInWords()
    {
        var cut = Draw("en", new PlacedHazard { Entry = "hazard/spiked-pit", State = HazardState.Noticed });

        Assert.Equal("/library/hazard/spiked-pit", cut.Find("[data-testid=feature-hazard-link]").GetAttribute("href"));
        Assert.Equal("Spiked Pit", cut.Find("[data-testid=feature-hazard-link]").TextContent);
        Assert.Contains("Noticed", cut.Find("[data-testid=feature-hazard-notes]").TextContent);
        Assert.Contains("Hazard", cut.Find("[data-testid=feature-hazard]").TextContent);
    }

    [Fact]
    public void AHazardGoneFromTheLibrary_ShowsItsIdWithoutALink()
    {
        var cut = Draw("en", new PlacedHazard { Entry = "hazard/old-trap", State = HazardState.Hidden }, name: null, href: null);

        Assert.Empty(cut.FindAll("[data-testid=feature-hazard-link]"));
        Assert.Contains("old-trap", cut.Find("[data-testid=feature-hazard-notes]").TextContent);
    }

    [Fact]
    public void NoSuccessesAreShown_WhenThereAreNone() =>
        Assert.DoesNotContain("successes", Draw("en", new PlacedHazard { Entry = "hazard/a" }).Find("[data-testid=feature-hazard-notes]").TextContent);

    [Fact]
    public void TheSuccessesSoFar_AreShown() =>
        Assert.Contains("2 successes so far", Draw("en", new PlacedHazard { Entry = "hazard/a", Successes = 2 }).Find("[data-testid=feature-hazard-notes]").TextContent);

    [Theory]
    [InlineData("ru", HazardState.Triggered, "Сработала")]
    [InlineData("he", HazardState.Disabled, "נוטרלה")]
    public void TheStateIsTranslated(string culture, HazardState state, string expected)
    {
        var cut = Draw(culture, new PlacedHazard { Entry = "hazard/a", State = state });

        Assert.Contains(expected, cut.Find("[data-testid=feature-hazard]").TextContent);
        Assert.Contains(expected, cut.Find("[data-testid=feature-hazard-notes]").TextContent);
    }
}
