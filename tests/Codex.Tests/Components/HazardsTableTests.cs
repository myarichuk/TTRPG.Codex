using Bunit;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>The Hazards panel at the table is the game master's alone: a player's page draws nothing of it, and neither does a system with no hazard type.</summary>
public class HazardsTableTests : ComponentTestBase
{
    private readonly IRegionRepository _regions = Substitute.For<IRegionRepository>();

    private static SystemDefinition SystemWith(bool hazards) => new()
    {
        EntityTypes = { new ContentTypeDefinition { Type = "hazard", Hazard = hazards ? new HazardDefinition() : null } }
    };

    private IRenderedComponent<HazardsTable> Draw(CampaignRole role, bool hazards = true)
    {
        _regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(new[]
        {
            new RegionDocument
            {
                Id = "r", CampaignId = "c", Name = "Dungeon",
                Locations = { new LocationDocument { Id = "p1", Name = "Pit room", Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/old-trap" } } } }
            }
        });
        Services.AddSingleton(_regions);
        Services.AddSingleton(new CampaignLibrary(Substitute.For<IContentRegistry>(), Substitute.For<IHomebrewRepository>(), Substitute.For<ISystemCatalog>()));
        Services.AddSingleton<ILogger<HazardsTable>>(NullLogger<HazardsTable>.Instance);
        return Render<HazardsTable>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "c", SystemId = "srd5e" })
            .Add(x => x.Access, new CampaignAccess("c", "user-1", role))
            .Add(x => x.Definition, SystemWith(hazards)));
    }

    [Fact]
    public void ForAPlayer_NothingIsDrawn_AndNothingIsRead()
    {
        var cut = Draw(CampaignRole.Player);

        Assert.Empty(cut.FindAll("[data-testid=hazards-panel]"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
        _regions.DidNotReceive().GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>());
    }

    [Fact]
    public void ForASystemWithNoHazardType_NothingIsDrawn() => Assert.Empty(Draw(CampaignRole.DM, hazards: false).FindAll("[data-testid=hazards-panel]"));

    [Fact]
    public void ForTheGameMaster_APlacedHazardGoneFromTheLibrary_IsListedWithItsIdAndNoAction()
    {
        var cut = Draw(CampaignRole.DM);

        cut.WaitForAssertion(() => Assert.Contains("old-trap", cut.Find("[data-testid=hazard-0]").TextContent));
        Assert.Contains("no longer in the Library", cut.Find("[data-testid=hazard-0]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=hazard-0-notice]"));
    }
}
