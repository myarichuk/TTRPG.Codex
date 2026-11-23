using Codex.Core.Models;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The encounter builder's creature search is a new reader of the Library's actors, so it splits by the <c>hazard</c> tag as HAZARDS_DESIGN.md 3.3 requires: creatures
/// in the search, complex hazards never among them (the pattern of <c>QuickAddPanelTests</c>).</summary>
public class EncounterCreatureSearchTests
{
    private readonly EncounterWorld _w = new();

    public EncounterCreatureSearchTests()
    {
        // A complex hazard as the loader makes it: an entry of the hazard type and an actor tagged `hazard`.
        _w.Registry.RegisterActor(new ActorDefinition
        {
            Id = "rolling-stone", SystemId = "demo", PackId = "base", Name = "Rolling stone", Tags = new List<string> { "hazard", "trap" },
            Metadata = new Dictionary<string, object> { ["entryType"] = "hazard" }
        }, 0);
        // A hazard that is filed under the creature type: found by the actor's tag, and by the entry's own tag.
        _w.Registry.RegisterEntry(new ContentEntry { Id = "boulder", Type = "monster", SystemId = "demo", PackId = "base", Name = "Boulder trap" }, 0);
        _w.Registry.RegisterActor(new ActorDefinition
        {
            Id = "boulder", SystemId = "demo", PackId = "base", Name = "Boulder trap", Tags = new List<string> { "hazard" },
            Metadata = new Dictionary<string, object> { ["entryType"] = "monster" }
        }, 0);
        _w.Registry.RegisterEntry(new ContentEntry { Id = "trapdoor", Type = "monster", SystemId = "demo", PackId = "base", Name = "Trapdoor", Tags = new List<string> { "hazard" } }, 0);
    }

    private async Task<string[]> NamesAsync(string query) =>
        (await _w.Service.SearchCreaturesAsync(_w.Campaign, _w.Gm, query)).Select(i => i.Entry.Name).Order().ToArray();

    [Fact]
    public async Task TheSearch_ListsCreatures_AndNeverAComplexHazard_WhateverTheQuery()
    {
        Assert.Equal(new[] { "Bandit", "Wolf" }, await NamesAsync(""));
        Assert.Empty(await NamesAsync("stone"));
        Assert.Empty(await NamesAsync("boulder"));
        Assert.Empty(await NamesAsync("trapdoor"));
        Assert.Equal(new[] { "Bandit" }, await NamesAsync("band"));
    }

    [Fact]
    public async Task TheSearch_StillListsTheCampaignsOwnHomebrewCreatures()
    {
        _w.Brews.Add(new HomebrewEntryDocument { CampaignId = "c1", SystemId = "demo", Type = "monster", EntryId = "cellar-rat", Name = "Cellar rat" });
        _w.Brews.Add(new HomebrewEntryDocument { CampaignId = "c1", SystemId = "demo", Type = "hazard", EntryId = "homebrew-net", Name = "Falling net" });

        var items = await _w.Service.SearchCreaturesAsync(_w.Campaign, _w.Gm, "");

        Assert.Equal(new[] { "Bandit", "Cellar rat", "Wolf" }, items.Select(i => i.Entry.Name).Order());
        Assert.True(items.Single(i => i.Entry.Id == "cellar-rat").IsHomebrew);
        Assert.DoesNotContain(items, i => i.Entry.Type == "hazard");
    }
}
