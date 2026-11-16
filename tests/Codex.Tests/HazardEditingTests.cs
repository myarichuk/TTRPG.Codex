using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>What the homebrew wizard's hazard mode leans on in the core (docs/HAZARDS_DESIGN.md 11, H7): the system's guide row for a level, rows read from any
/// store form, and the tidy that keeps a blank row from being saved while keeping every word a real row holds.</summary>
public class HazardEditingTests
{
    private static readonly SystemDefinition Srd = new BookTestCatalog().GetDefinition("srd5e")!;
    private static readonly ContentTypeDefinition Type = Srd.EntityTypes.Single(t => t.Type == "hazard");

    private static Dictionary<string, object> Row(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("1", "13", "2d10")]
    [InlineData("1-4", "13", "2d10")]
    [InlineData("5-10", "15", "4d10")]
    [InlineData("11", "17", "10d10")]
    [InlineData("20", "19", "18d10")]
    [InlineData("0", "13", "2d10")]
    public void TheGuideRow_IsTheHighestRowAtOrBelowTheLowestLevel_OrTheFirst(string level, string saveDc, string damage)
    {
        var row = HazardNumbers.GuideRow(level, Srd)!;

        Assert.Equal(saveDc, row["saveDc"]);
        Assert.Equal(damage, row["damage"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadly")]
    public void ALevelWithNoNumber_HasNoGuideRow(string? level) => Assert.Null(HazardNumbers.GuideRow(level, Srd));

    [Fact]
    public void RowsAreReadFromEveryFormTheStoreGivesThem_AndCopied()
    {
        var typed = new Dictionary<string, object> { ["name"] = "a" };
        var loose = new Dictionary<object, object> { ["name"] = "b", [1] = "c" };
        var rows = HazardNumbers.EditableRows(new List<object> { typed, loose, "not a row" });

        Assert.Equal(["a", "b"], rows.Select(r => r["name"]));
        rows[0]["name"] = "changed";
        Assert.Equal("a", typed["name"]);
        Assert.Empty(HazardNumbers.EditableRows(null));
        Assert.Empty(HazardNumbers.EditableRows("text"));
    }

    [Fact]
    public void Tidy_DropsBlankRowsAndEmptyLists_AndKeepsEveryWordARealRowHolds()
    {
        var properties = new Dictionary<string, object>
        {
            ["detect"] = new List<object> { Row(), Row(("name", "  ")), Row(("name", "Look"), ("passive", false), ("text", "")) },
            ["disarm"] = new List<object> { Row(("name", "Cut"), ("triggersOn", new List<object> { "failure" })) },
            ["damage"] = new List<object> { Row(), Row(("dice", "")) },
            ["higherLevels"] = new List<object>
            {
                Row(("levels", "5-10"), ("damage", new List<object> { Row(), Row(("dice", "2d10")) }), ("methods", new List<object> { Row() })),
                Row(("levels", ""))
            },
            ["level"] = "1-4"
        };

        HazardNumbers.Tidy(properties, Type);

        var detect = Assert.Single(HazardNumbers.EditableRows(properties["detect"]));
        Assert.Equal("Look", detect["name"]);
        Assert.Equal(false, detect["passive"]);
        Assert.DoesNotContain("text", detect.Keys);
        Assert.Equal(["failure"], ((IEnumerable<object>)Assert.Single(HazardNumbers.EditableRows(properties["disarm"]))["triggersOn"]).Select(o => o.ToString()));
        Assert.DoesNotContain("damage", properties.Keys);
        var band = Assert.Single(HazardNumbers.EditableRows(properties["higherLevels"]));
        Assert.Equal("2d10", Assert.Single(HazardNumbers.EditableRows(band["damage"]))["dice"]);
        Assert.DoesNotContain("methods", band.Keys);
        Assert.Equal("1-4", properties["level"]);
    }

    [Fact]
    public void ATypeWithNoHazardBlock_IsLeftAlone()
    {
        var properties = new Dictionary<string, object> { ["detect"] = new List<object> { Row() } };

        HazardNumbers.Tidy(properties, new ContentTypeDefinition { Type = "monster" });

        Assert.Contains("detect", properties.Keys);
    }

    // The player's app Library (not only the book): a hazard the GM published is still the GM's, by path and by search.
    [Fact]
    public async Task APlayersLibrary_NeverHoldsAHazard_ThoughTheGmPublishedIt()
    {
        var published = new HomebrewEntryDocument { Id = "h1", CampaignId = "c1", EntryId = "needle", Type = "hazard", Name = "Needle", IsPublished = true };
        var homebrew = NSubstitute.Substitute.For<Codex.Persistence.IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(NSubstitute.Arg.Any<Codex.Persistence.CampaignAccess>())
            .Returns(Task.FromResult<IEnumerable<HomebrewEntryDocument>>([published]));
        var library = new Codex.Web.Services.CampaignLibrary(new Codex.Core.ContentRegistry(), homebrew, new BookTestCatalog());
        var campaign = new Codex.Persistence.CampaignDocument { Id = "c1", SystemId = "srd5e", Name = "C" };
        var player = new Codex.Persistence.CampaignAccess("c1", "player", Codex.Persistence.CampaignRole.Player);
        var dm = new Codex.Persistence.CampaignAccess("c1", "dm", Codex.Persistence.CampaignRole.DM);

        Assert.False(library.CanRead(campaign, player, "hazard"));
        Assert.True(library.CanRead(campaign, dm, "hazard"));
        Assert.Null(await library.GetAsync(campaign, player, "hazard", "needle", isHomebrew: true));
        Assert.Null(await library.GetAsync(campaign, player, "hazard", "needle"));
        Assert.NotNull(await library.GetAsync(campaign, dm, "hazard", "needle", isHomebrew: true));
    }
}
