using Codex.Core;
using Codex.Core.Scripting;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>The detail path honours the same player-facing rule as the browser's chips and search.</summary>
public class CampaignLibraryTests
{
    private static readonly SystemDefinition Demo = Codex.Core.Systems.SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        entityTypes:
          - type: spell
            label: Spell
            folder: spells
          - type: monster
            label: Creature
            folder: monsters
            playerFacing: false
        """);

    private static readonly CampaignDocument Campaign = new() { Id = "c1", SystemId = "demo" };

    private static CampaignLibrary Build(params HomebrewEntryDocument[] brews)
    {
        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        registry.RegisterEntry(new ContentEntry { Id = "bolt", Type = "spell", SystemId = "demo", PackId = "base", Name = "Bolt" }, 0);
        registry.RegisterEntry(new ContentEntry { Id = "dragon", Type = "monster", SystemId = "demo", PackId = "base", Name = "Dragon" }, 0);

        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(brews.AsEnumerable());
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetDefinition("demo").Returns(Demo);
        return new CampaignLibrary(registry, homebrew, catalog);
    }

    private static readonly CampaignAccess Dm = new("c1", "dm", CampaignRole.DM);
    private static readonly CampaignAccess Player = new("c1", "p", CampaignRole.Player);

    [Fact]
    public async Task APlayer_CannotOpenAStatBlock_ByUrl_ButTheDmCan()
    {
        var library = Build();

        Assert.Null(await library.GetAsync(Campaign, Player, "monster", "dragon"));
        Assert.NotNull(await library.GetAsync(Campaign, Dm, "monster", "dragon"));
    }

    [Fact]
    public async Task APlayer_CannotOpenAPublishedHomebrewCreature_Either()
    {
        var brew = new HomebrewEntryDocument { CampaignId = "c1", SystemId = "demo", Type = "monster", EntryId = "my-beast", Name = "My Beast" };
        var library = Build(brew);

        Assert.Null(await library.GetAsync(Campaign, Player, "monster", "my-beast"));
        Assert.NotNull(await library.GetAsync(Campaign, Dm, "monster", "my-beast"));
    }

    [Fact]
    public async Task APlayer_CanStillOpenPlayerFacingEntries()
    {
        var library = Build();

        Assert.NotNull(await library.GetAsync(Campaign, Player, "spell", "bolt"));
    }
}
