using Bunit;
using Codex.Persistence;
using Codex.Persistence.Runtime;
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

    // ---- Complex hazards that are actors (H5) ----

    private static SystemDefinition ComplexSystem() => new()
    {
        EntityTypes =
        {
            new ContentTypeDefinition
            {
                Type = "hazard",
                Hazard = new HazardDefinition
                {
                    Routine = "routine", Complex = "complex",
                    Effect = new HazardEffect { Save = "saveAttribute", SaveDc = "saveDc", Damage = "damage" }
                }
            }
        }
    };

    private static ContentEntry StoneEntry(bool withEffect = true)
    {
        var properties = new Dictionary<string, object>
        {
            ["complex"] = true,
            ["routine"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "Roll", ["description"] = "Moves." },
                new Dictionary<string, object> { ["name"] = "Crush", ["description"] = "Hits.", ["strikes"] = true }
            }
        };
        if (withEffect)
        {
            properties["saveAttribute"] = "Dexterity";
            properties["saveDc"] = 15;
            properties["damage"] = new List<object> { new Dictionary<string, object> { ["dice"] = "10d10", ["type"] = "bludgeoning" } };
        }

        return new ContentEntry { Id = "stone", Type = "hazard", Name = "Rolling Stone", PackId = "p", Properties = properties };
    }

    private static readonly ActorDocument Hero = new() { Id = "hero", Name = "Aria", Kind = ActorKind.PlayerCharacter };
    private static ActorDocument StoneActor(HazardActorLink link, bool retired = false, string id = "stone1") => new() { Id = id, Name = "Rolling Stone", Kind = ActorKind.Monster, Hazard = link, Retired = retired };
    private static HazardActorLink Placed() => new() { RegionId = "r", LocationId = "p1", Entry = "hazard/stone" };
    private static HazardActorLink FromLibrary() => new() { Entry = "hazard/stone" };

    private static RuntimeSnapshot Running(string? current = null, string? place = null) => RuntimeSnapshot.Empty with
    {
        Encounter = new EncounterSnapshot("e1", 1, 0, new[]
        {
            new ParticipantSnapshot(current ?? "stone1", 20, 0, TurnState.Normal, false), new ParticipantSnapshot("hero", 10, 0, TurnState.Normal, false)
        }, place)
    };

    private IRenderedComponent<HazardsTable> DrawComplex(ContentEntry entry, IEnumerable<ActorDocument> actors, RuntimeSnapshot snapshot, string placeAt = "p1", string? russianName = null)
    {
        var registry = Substitute.For<IContentRegistry>();
        registry.GetEntry("srd5e", "hazard", "stone").Returns(entry);
        registry.Localize(Arg.Any<ContentEntry>(), Arg.Any<string?>()).Returns(call => call.Arg<ContentEntry>());
        if (russianName != null)
        {
            // The page is in Russian, and the pack has a translation of the hazard's name.
            Services.AddSingleton(Codex.Localization.Localizer.For("ru"));
            registry.Localize(Arg.Any<ContentEntry>(), Arg.Is<string?>(c => c != null && c.StartsWith("ru"))).Returns(call =>
            {
                var shown = call.Arg<ContentEntry>().Clone();
                shown.Name = russianName;
                return shown;
            });
        }

        _regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(new[]
        {
            new RegionDocument
            {
                Id = "r", CampaignId = "c", Name = "Dungeon",
                Locations =
                {
                    new LocationDocument { Id = "other", Name = "Elsewhere" },
                    new LocationDocument { Id = placeAt, Name = "Passage", Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/stone" } } }
                }
            }
        });
        Services.AddSingleton(_regions);
        Services.AddSingleton(new CampaignLibrary(registry, Substitute.For<IHomebrewRepository>(), Substitute.For<ISystemCatalog>()));
        Services.AddSingleton<ILogger<HazardsTable>>(NullLogger<HazardsTable>.Instance);
        return Render<HazardsTable>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "c", SystemId = "srd5e" })
            .Add(x => x.Access, new CampaignAccess("c", "user-1", CampaignRole.DM))
            .Add(x => x.Definition, ComplexSystem())
            .Add(x => x.Actors, actors)
            .Add(x => x.Snapshot, snapshot));
    }

    [Fact]
    public void ThePlacedHazardsActor_ShowsItsRoutineOnItsPlacesRow_WithItsTurnAndOnlyTheStrikingStepOnCreatures()
    {
        var cut = DrawComplex(StoneEntry(), new[] { Hero, StoneActor(Placed()) }, Running(place: "p1"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Id("hazard-0-routine"))));
        Assert.Contains("In the initiative order", cut.Find(Id("hazard-0-order")).TextContent);
        Assert.Contains("Its turn now", cut.Find(Id("hazard-0-its-turn")).TextContent);
        // Roll is a note; only Crush (strikes: true) is offered on the ticked creatures.
        Assert.Empty(cut.FindAll(Id("hazard-0-step-0-on")));
        Assert.NotEmpty(cut.FindAll(Id("hazard-0-step-1-on")));
        // The hazard's own actor is not somebody the hazard can be tried on; the character is.
        Assert.NotEmpty(cut.FindAll(Id("hazard-who-hero")));
        Assert.Empty(cut.FindAll(Id("hazard-who-stone1")));
    }

    [Fact]
    public void InRussian_ThePanelShowsTheHazardByItsTranslatedName_AndInEnglishByItsOwn()
    {
        var russian = DrawComplex(StoneEntry(), new[] { Hero, StoneActor(Placed()) }, Running(place: "p1"), russianName: "Катящийся камень");
        russian.WaitForAssertion(() => Assert.NotEmpty(russian.FindAll(Id("hazard-0"))), Patience);
        Assert.Contains("Катящийся камень", russian.Find(Id("hazard-0")).TextContent);
        Assert.DoesNotContain("Rolling Stone", russian.Find(Id("hazard-0")).TextContent);
    }

    [Fact]
    public void InEnglish_ThePanelShowsTheHazardByItsOwnName()
    {
        var english = DrawComplex(StoneEntry(), new[] { Hero, StoneActor(Placed()) }, Running(place: "p1"));
        english.WaitForAssertion(() => Assert.NotEmpty(english.FindAll(Id("hazard-0"))), Patience);
        Assert.Contains("Rolling Stone", english.Find(Id("hazard-0")).TextContent);
    }

    [Fact]
    public void AHazardWhoseEntryHasNoSaveAttackOrDamage_HasNoEffect_EvenThoughTheTypeNamesTheFields()
    {
        var cut = DrawComplex(StoneEntry(withEffect: false), new[] { Hero, StoneActor(Placed()) }, Running(place: "p1"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Id("hazard-0-routine"))));
        Assert.NotEmpty(cut.FindAll(Id("hazard-0-step-1-note")));
        Assert.Empty(cut.FindAll(Id("hazard-0-step-1-on")));
    }

    [Fact]
    public void ALibraryOnlyHazard_IsShownWhateverPlaceThePanelIsNarrowedTo_ButOnlyWhileAnEncounterRuns()
    {
        var actors = new[] { Hero, StoneActor(FromLibrary()) };

        // The panel is narrowed to the Passage (the encounter's place); the hazard that stands on no place is still listed, with its routine.
        var running = DrawComplex(StoneEntry(), actors, Running(place: "p1"));
        running.WaitForAssertion(() => Assert.Contains("Taken from the Library", running.Markup));
        Assert.Equal("p1", running.Find("#hazard-place").GetAttribute("value"));
        Assert.Single(running.FindAll("[data-testid$='-routine']"));
    }

    [Fact]
    public void WithNoEncounter_OrOnceTheActorIsRetired_NoHazardActorsRowIsShown()
    {
        var idle = DrawComplex(StoneEntry(), new[] { Hero, StoneActor(FromLibrary()) }, RuntimeSnapshot.Empty);
        idle.WaitForAssertion(() => Assert.NotEmpty(idle.FindAll(Id("hazard-0"))));
        Assert.DoesNotContain("Taken from the Library", idle.Markup);
        Assert.Empty(idle.FindAll(Id("hazard-0-routine")));
    }

    [Fact]
    public void ARetiredHazardActor_OfAnEarlierEncounter_IsNotShownInTheNextOne()
    {
        var cut = DrawComplex(StoneEntry(), new[] { Hero, StoneActor(FromLibrary(), retired: true, id: "old1"), StoneActor(Placed(), retired: true, id: "old2") }, Running(current: "hero", place: "p1"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Id("hazard-0"))));
        Assert.DoesNotContain("Taken from the Library", cut.Markup);
        Assert.Empty(cut.FindAll(Id("hazard-0-routine")));
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

    // ---- The panel follows the encounter's place (B3: a plan's start sets it) ----

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static RuntimeSnapshot With(string id, string? place, string? plan) => RuntimeSnapshot.Empty with
    {
        Encounter = new EncounterSnapshot(id, 1, 0, Array.Empty<ParticipantSnapshot>(), place, plan)
    };

    private IRenderedComponent<HazardsTable> DrawFollowing(RuntimeSnapshot snapshot, CampaignRuntime? runtime = null)
    {
        _regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(new[]
        {
            new RegionDocument
            {
                Id = "r", CampaignId = "c", Name = "Dungeon",
                Locations =
                {
                    new LocationDocument { Id = "p1", Name = "Pit room", Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/old-trap" } } },
                    new LocationDocument { Id = "p2", Name = "Gallery", Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/old-trap" } } }
                }
            }
        });
        Services.AddSingleton(_regions);
        Services.AddSingleton(new CampaignLibrary(Substitute.For<IContentRegistry>(), Substitute.For<IHomebrewRepository>(), Substitute.For<ISystemCatalog>()));
        Services.AddSingleton<ILogger<HazardsTable>>(NullLogger<HazardsTable>.Instance);
        var cut = Render<HazardsTable>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "c", SystemId = "srd5e" })
            .Add(x => x.Access, new CampaignAccess("c", "user-1", CampaignRole.DM))
            .Add(x => x.Definition, SystemWith(true))
            .Add(x => x.Snapshot, snapshot)
            .Add(x => x.Runtime, runtime!));
        cut.WaitForState(() => cut.FindAll("#hazard-place").Count > 0, Patience);
        return cut;
    }

    private static string Picked(IRenderedComponent<HazardsTable> cut) => cut.Find("#hazard-place").GetAttribute("value") ?? string.Empty;

    [Fact]
    public void ANewEncountersPlace_MovesThePanel_WhenItWasAlreadyLoaded()
    {
        var cut = DrawFollowing(RuntimeSnapshot.Empty);
        Assert.Equal(string.Empty, Picked(cut));

        cut.Render(p => p.Add(x => x.Snapshot, With("e2", "p1", null)));

        cut.WaitForAssertion(() => Assert.Equal("p1", Picked(cut)), Patience);
    }

    [Fact]
    public void APlanStartedInTheEmptyEncounter_MovesThePanel()
    {
        var cut = DrawFollowing(With("e1", null, null));
        Assert.Equal(string.Empty, Picked(cut));

        // The same encounter (it was empty and is reused), now with a plan and its place.
        cut.Render(p => p.Add(x => x.Snapshot, With("e1", "p1", "plan-1")));

        cut.WaitForAssertion(() => Assert.Equal("p1", Picked(cut)), Patience);
    }

    [Fact]
    public void AConsoleEncounterWithNoPlace_KeepsThePickedPlace()
    {
        var cut = DrawFollowing(RuntimeSnapshot.Empty);
        cut.Find("#hazard-place").Change("p1");
        Assert.Equal("p1", Picked(cut));

        cut.Render(p => p.Add(x => x.Snapshot, With("e2", null, null)));

        cut.WaitForAssertion(() => Assert.Equal("p1", Picked(cut)), Patience);
    }

    [Fact]
    public async Task ThePickerStillOverrides_AfterThePanelFollowed()
    {
        await using var runtime = new CampaignRuntime("c", Substitute.For<IActorRepository>(), new ComponentRegistry(), NullLogger.Instance,
            activeEncounter: new EncounterDocument { Id = "e1", CampaignId = "c", LocationId = "p1", PreparedEncounterId = "plan-1" });
        var cut = DrawFollowing(With("e1", "p1", "plan-1"), runtime);
        Assert.Equal("p1", Picked(cut));

        cut.Find("#hazard-place").Change("p2");

        cut.WaitForAssertion(() => Assert.Equal("p2", runtime.ActiveEncounter!.LocationId), Patience);
        // The same encounter and plan, still carrying the place it started at: the pick is not undone.
        cut.Render(p => p.Add(x => x.Snapshot, With("e1", "p1", "plan-1")));
        cut.WaitForAssertion(() => Assert.Equal("p2", Picked(cut)), Patience);
    }
}
