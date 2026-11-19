using System.Text.Json;
using Codex.Core.Consistency;
using Codex.Persistence;

namespace Codex.Tests.Consistency;

/// <summary>The v2 checks through the service the chat reads: the noisy ones stay out unless asked for, and homebrew reaches the checks.</summary>
public class V2ThroughTheServiceTests
{
    private static WikiPageDocument Page(string id, string title, string body) =>
        new() { Id = id, Slug = Codex.Core.WikiText.Slugify(title), Title = title, Body = body, Visibility = WikiVisibility.Party };

    private static string[] Ids(string listing) => JsonDocument.Parse(listing).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();

    private static Task<string> ListAsync(WorldBuilderTests.Saving saving, string? scope = null) =>
        saving.Service.IssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, scope, null);

    private static WorldBuilderTests.Saving World() => WorldBuilderTests.ServiceWithPages(new[]
    {
        Page("wikipages/1-A", "Road", "Leads to [[Linked]]."), Page("wikipages/2-A", "Linked", "A place."), Page("wikipages/3-A", "Lonely", "Nobody links here.")
    });

    [Fact]
    public async Task TheNoisyChecks_AreNotInTheDefaultList_NorInTheCount_ButAreListedForTheNoiseScope()
    {
        var saving = World();

        Assert.StartsWith("[]", await ListAsync(saving));
        Assert.StartsWith("[]", await ListAsync(saving, "all"));
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        var noise = Ids(await ListAsync(saving, "noise"));
        Assert.Equal(new[] { "orphan-page:page:wikipages/1-A:links", "orphan-page:page:wikipages/3-A:links" }, noise);
        Assert.Equal(noise, Ids(await ListAsync(saving, "orphan-page")));

        // Asking for noise changes nothing about the open issues afterwards.
        Assert.StartsWith("[]", await ListAsync(saving));
        Assert.Empty((await saving.Service.OpenIssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm))!);
    }

    [Fact]
    public async Task TheHomebrewOfTheCampaign_ReachesTheChecks()
    {
        var saving = World();
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/1", Type = "spell", EntryId = "dup", Name = "One", SystemId = "srd5e" });
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/2", Type = "spell", EntryId = "dup", Name = "Two", SystemId = "srd5e" });

        var ids = Ids(await ListAsync(saving, "homebrew-duplicate"));

        Assert.Equal(new[] { "homebrew-duplicate:homebrew:spell/dup:entryId:hb-1", "homebrew-duplicate:homebrew:spell/dup:entryId:hb-2" }, ids);
        Assert.Equal(2, (await saving.Service.OpenIssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm))!.Count);
        Assert.Equal(ids, Ids(await ListAsync(saving, "homebrew:spell/dup")));
    }

    [Fact]
    public async Task TheCountNote_StillCostsOneSnapshot_AndTheV2ChecksAreInIt()
    {
        var saving = World();
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/1", Type = "spell", EntryId = "dup", Name = "One", SystemId = "srd5e" });
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/2", Type = "spell", EntryId = "dup", Name = "Two", SystemId = "srd5e" });
        var before = saving.Snapshots;

        var note = await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

        Assert.Equal(1, saving.Snapshots - before);
        Assert.Contains("2 open issues (2 errors, 0 warnings, 0 info)", note);
    }

    [Fact]
    public async Task TheCountNote_IsLeftOutSilently_WhenTheCheckTakesLongerThanItsBudget_AndNeverHoldsUpTheMessage()
    {
        var saving = World();
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/1", Type = "spell", EntryId = "dup", Name = "One", SystemId = "srd5e" });
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/2", Type = "spell", EntryId = "dup", Name = "Two", SystemId = "srd5e" });

        saving.Service.IssueSummaryBudget = TimeSpan.Zero; // already spent when the check starts
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        saving.Service.IssueSummaryBudget = TimeSpan.FromSeconds(30);
        Assert.Contains("2 open issues", await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
    }

    [Fact]
    public void TheChecker_LooksAtTheTokenBetweenChecks_AndStopsWithACancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConsistencyChecker.Run(Fx.Snap(), cancel: cancelled.Token));
        Assert.Empty(ConsistencyChecker.Run(Fx.Snap(), cancel: CancellationToken.None));
    }
}
