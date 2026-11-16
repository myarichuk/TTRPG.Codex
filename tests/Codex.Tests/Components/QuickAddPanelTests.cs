using Bunit;
using Codex.Core;
using Codex.Core.Models;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>The quick-add panel splits the registry's actors by the `hazard` tag (HAZARDS_DESIGN.md 3.3): creatures as before, complex hazards in a group of their own, and the
/// hazards of the encounter's place first, with or without a search.</summary>
public class QuickAddPanelTests : ComponentTestBase
{
    private static readonly ContentTypeDefinition HazardType = new()
    {
        Type = "hazard",
        Hazard = new HazardDefinition { Initiative = "initiative", Complex = "complex", Routine = "routine" }
    };

    private static ActorDefinition Goblin() => new() { Id = "goblin", SystemId = "t", Name = "Goblin", Tags = new List<string> { "monster" } };

    private static ActorDefinition Stone() => new()
    {
        Id = "rolling-stone", SystemId = "t", Name = "Rolling Stone", Tags = new List<string> { "hazard", "trap" },
        Properties = new Dictionary<string, object> { ["initiative"] = 8L }
    };

    private static HazardOffer Offer(string id, string name, string place) =>
        new(new ActorDefinition { Id = id, SystemId = "t", Name = name, Tags = new List<string> { "hazard" } },
            new HazardActorLink { RegionId = "r", LocationId = "p", Entry = "hazard/" + id }, 8, place);

    private IRenderedComponent<QuickAddPanel> Draw(string culture = "en", IReadOnlyList<HazardOffer>? onPlace = null, Func<IActorDefinition, Task<bool>>? onAdd = null,
        Func<HazardOffer, Task<bool>>? onAddHazard = null, bool hazardsEnabled = true)
    {
        var registry = new ContentRegistry();
        registry.RegisterActor(Goblin(), 0);
        registry.RegisterActor(Stone(), 0);
        Services.AddSingleton<IContentRegistry>(registry);
        Services.AddSingleton(Localizer.For(culture));
        return Render<QuickAddPanel>(p => p
            .Add(x => x.SystemId, "t")
            .Add(x => x.OnAdd, onAdd ?? (_ => Task.FromResult(true)))
            .Add(x => x.OnAddHazard, hazardsEnabled ? onAddHazard ?? (_ => Task.FromResult(true)) : null)
            .Add(x => x.HazardType, HazardType)
            .Add(x => x.PlaceHazards, onPlace ?? Array.Empty<HazardOffer>())
            .Add(x => x.PlaceName, "The long passage"));
    }

    [Fact]
    public void ASearch_ShowsCreaturesAndHazardsInTwoGroupsWithHeadings_AHazardNeverAmongTheCreatures()
    {
        var cut = Draw();

        cut.Find("#monster-search").Input("o");

        var creatures = cut.Find("#quick-add-creatures-heading");
        var hazards = cut.Find("#quick-add-hazards-heading");
        Assert.Equal("Creatures", creatures.TextContent);
        Assert.Equal("Hazards", hazards.TextContent);
        var creatureList = cut.Find("ul[aria-labelledby=quick-add-creatures-heading]");
        var hazardList = cut.Find("ul[aria-labelledby=quick-add-hazards-heading]");
        Assert.Contains("Goblin", creatureList.TextContent);
        Assert.DoesNotContain("Rolling Stone", creatureList.TextContent);
        Assert.Contains("Rolling Stone", hazardList.TextContent);
        Assert.Contains("(initiative +8)", hazardList.TextContent);
        Assert.Equal("Add Rolling Stone to the initiative order", hazardList.QuerySelector("button")!.GetAttribute("aria-label"));
    }

    [Fact]
    public void AddingACreature_StillPassesThatCreature_AndClearsTheBox()
    {
        IActorDefinition? added = null;
        var cut = Draw(onAdd: d =>
        {
            added = d;
            return Task.FromResult(true);
        });
        cut.Find("#monster-search").Input("gob");

        cut.Find("ul[aria-labelledby=quick-add-creatures-heading] button").Click();

        Assert.Equal("goblin", added!.Id);
        Assert.Equal(string.Empty, cut.Find("#monster-search").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void AddingAHazard_PassesTheOfferWithItsLinkToTheLibraryAlone()
    {
        HazardOffer? added = null;
        var cut = Draw(onAddHazard: o =>
        {
            added = o;
            return Task.FromResult(true);
        });
        cut.Find("#monster-search").Input("stone");

        cut.Find("ul[aria-labelledby=quick-add-hazards-heading] button").Click();

        Assert.Equal("hazard/rolling-stone", added!.Link.Entry);
        Assert.False(added.Link.IsPlaced);
        Assert.Equal(8, added.Initiative);
    }

    [Fact]
    public void TheHazardsOfTheEncountersPlace_AreOfferedFirst_WithNoSearch_AndNotAgainAmongTheLibrarys()
    {
        var cut = Draw(onPlace: new[] { Offer("rolling-stone", "Rolling Stone", "The long passage") });

        Assert.Equal("Hazards on The long passage", cut.Find("#quick-add-place-heading").TextContent);
        Assert.Contains("Rolling Stone", cut.Find("ul[aria-labelledby=quick-add-place-heading]").TextContent);
        Assert.Empty(cut.FindAll("#quick-add-creatures-heading"));

        cut.Find("#monster-search").Input("stone");

        // The same hazard is offered once: from the place.
        Assert.Empty(cut.FindAll("#quick-add-hazards-heading"));
        Assert.Contains("Rolling Stone", cut.Find("ul[aria-labelledby=quick-add-place-heading]").TextContent);
        // The place's group stays above the creatures' group.
        Assert.True(cut.Markup.IndexOf("quick-add-place-heading", StringComparison.Ordinal) < cut.Markup.IndexOf("quick-add-creatures-heading", StringComparison.Ordinal));
    }

    [Fact]
    public void AHazardOfThePlace_IsAddedThroughTheHazardCallback_AndNeverTheCreatureOne()
    {
        var hazards = new List<string>();
        var creatures = new List<string>();
        var cut = Draw(onPlace: new[] { Offer("rolling-stone", "Rolling Stone", "Passage") }, onAdd: d =>
        {
            creatures.Add(d.Id);
            return Task.FromResult(true);
        }, onAddHazard: o =>
        {
            hazards.Add(o.Link.LocationId);
            return Task.FromResult(true);
        });

        cut.Find("ul[aria-labelledby=quick-add-place-heading] button").Click();

        Assert.Equal(new[] { "p" }, hazards);
        Assert.Empty(creatures);
    }

    [Fact]
    public void WithoutAHazardHandler_NoHazardIsOffered()
    {
        var cut = Draw(onPlace: new[] { Offer("rolling-stone", "Rolling Stone", "Passage") }, hazardsEnabled: false);
        cut.Find("#monster-search").Input("stone");

        Assert.Empty(cut.FindAll("#quick-add-place-heading"));
        Assert.Empty(cut.FindAll("#quick-add-hazards-heading"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("he")]
    public void EveryControlHasALabel_AndEveryWordIsTranslated(string culture)
    {
        var cut = Draw(culture, new[] { Offer("rolling-stone", "Rolling Stone", "Passage") });
        cut.Find("#monster-search").Input("o");

        Assert.NotEmpty(cut.FindAll("label[for=monster-search]"));
        Assert.All(cut.FindAll("button"), b => Assert.False(string.IsNullOrWhiteSpace(b.TextContent)));
        Assert.DoesNotContain("Combat.", cut.Markup);
    }
}
