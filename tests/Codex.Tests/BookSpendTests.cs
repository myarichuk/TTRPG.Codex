using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// The edition's spend totals (<see cref="BookSpend"/>): what the draft's AI edits and merges have cost, kept in the edition's
/// <see cref="BookBuildDocument"/> (which once also held the old route's AI polish, deleted in docs/BOOK_EXPORT.md 50). The GM's alone,
/// gone with the campaign, and in neither the export nor the backup.
/// </summary>
public class BookSpendTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "CANARY_spend_dm";
    private const string PlayerUser = "CANARY_spend_player";

    private readonly RavenDbService _db;
    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly RavenWikiRepository _wiki;
    private readonly RavenBookBuildRepository _store;

    public BookSpendTests(RavenDbFixture fixture)
    {
        _db = fixture.Open(fixture.DbName);
        _actors = new ActorRepository(_db);
        _wiki = new RavenWikiRepository(_db);
        _store = new RavenBookBuildRepository(_db);
        _campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, new RavenSessionRepository(_db), new RavenNoteRepository(_db), new RegionRepository(_db),
            new FactRepository(_db), new RavenHomebrewRepository(_db), new RavenEncounterRepository(_db), _wiki, bookBuildRepository: _store);
    }

    private async Task<(CampaignAccess Dm, string Id)> SeedAsync()
    {
        var id = "spend-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Ashfall", SystemId = "srd5e", OwnerId = DmUser,
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM }, new CampaignMember { UserId = PlayerUser, Role = CampaignRole.Player } },
            ActivePacks = { "srd5e" }
        });
        return (new CampaignAccess(id, DmUser, CampaignRole.DM), id);
    }

    private static BookBudgetSnapshot Spent(long input, long output, int calls = 1) => new(10_000_000, input, output, 0, calls, 0, false, 0);

    [Fact]
    public async Task Cost_IsAccumulatedAcrossRuns_OnlyWhereThePricesWereKnown_AndARunThatSentNothingAddsNothing()
    {
        var (dm, _) = await SeedAsync();
        Assert.Null(await BookSpend.ReadAsync(_store, dm, "dm"));

        await BookSpend.AddAsync(_store, dm, "dm", Spent(3000, 1500), new AiPriceQuote(1m, 2m, "USD"));
        var totals = (await BookSpend.ReadAsync(_store, dm, "dm"))!;
        Assert.Equal(3000, totals.InputTokens);
        Assert.Equal(1500, totals.OutputTokens);
        Assert.Equal(0.006m, totals.Cost); // 3000 in at 1/Mtok plus 1500 out at 2/Mtok
        Assert.Equal(0, totals.UnpricedRuns);
        Assert.Equal("USD", totals.PriceLabel);

        await BookSpend.AddAsync(_store, dm, "dm", Spent(3000, 1500), null);
        await BookSpend.AddAsync(_store, dm, "dm", Spent(0, 0, calls: 0), null);
        totals = (await BookSpend.ReadAsync(_store, dm, "dm"))!;
        Assert.Equal(2, totals.Runs);
        Assert.Equal(2, totals.Calls);
        Assert.Equal(1, totals.UnpricedRuns); // a run with no prices is counted, never priced at zero
        Assert.Equal(0.006m, totals.Cost);
        // Each edition has its own.
        Assert.Null(await BookSpend.ReadAsync(_store, dm, "party"));
    }

    [Fact]
    public async Task OnlyTheGmReadsOrWritesTheTotals_AndOnlyTheirOwn()
    {
        var (dm, id) = await SeedAsync();
        await BookSpend.AddAsync(_store, dm, "dm", Spent(10, 10), null);

        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        var stranger = new CampaignAccess(id, "CANARY_nobody", CampaignRole.DM); // an access made for someone who is not a member

        Assert.Null(await _store.GetAsync(player, "dm"));
        Assert.Null(await _store.GetAsync(stranger, "dm"));
        Assert.Null(await BookSpend.ReadAsync(_store, player, "dm"));
        Assert.False(await _store.SaveAsync(player, new BookBuildDocument { Audience = "dm" }));
        Assert.False(await _store.DeleteAsync(player, "dm"));
        await BookSpend.AddAsync(_store, player, "dm", Spent(99, 99), null);
        Assert.Equal(10, (await BookSpend.ReadAsync(_store, dm, "dm"))!.InputTokens);
    }

    [Fact]
    public async Task DeletingTheCampaign_DeletesItsTotals_AndOnlyItsOwn()
    {
        var (dm, id) = await SeedAsync();
        var (other, otherId) = await SeedAsync();
        await BookSpend.AddAsync(_store, dm, "dm", Spent(10, 10), null);
        await BookSpend.AddAsync(_store, other, "dm", Spent(10, 10), null);

        await _campaigns.DeleteAsync(id, DmUser);

        Assert.Null(await _store.GetAsync(dm, "dm"));
        Assert.NotNull(await _store.GetAsync(other, "dm"));
        using var session = _db.Store.OpenAsyncSession();
        Assert.Empty(await session.Advanced.LoadStartingWithAsync<BookBuildDocument>(BookBuildDocument.PrefixFor(id)));
        Assert.NotEmpty(await session.Advanced.LoadStartingWithAsync<BookBuildDocument>(BookBuildDocument.PrefixFor(otherId)));
    }

    [Fact]
    public async Task TheTotals_AreInNeitherTheCampaignExportNorTheBackup()
    {
        var (dm, id) = await SeedAsync();
        // The price label exists only in the totals, so it marks them.
        await BookSpend.AddAsync(_store, dm, "dm", Spent(10, 10), new AiPriceQuote(1m, 1m, "MARKER_strolls"));
        Assert.Equal("MARKER_strolls", (await _store.GetAsync(dm, "dm"))!.PriceLabel); // a positive control

        var export = new CampaignExportService(_campaigns, _actors, new RavenSessionRepository(_db), new RavenEncounterRepository(_db), new FactRepository(_db), new RavenNoteRepository(_db), new RegionRepository(_db), new RavenHomebrewRepository(_db), _wiki);
        var json = CampaignExportService.Serialize((await export.ExportAsync(dm))!);
        Assert.DoesNotContain("MARKER_strolls", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bookbuilds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BookBuild", json, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "bb-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new JsonBackups(_db, _campaigns, export).WriteAsync(root, DateTime.UtcNow);
            var everything = string.Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.Contains(id.Replace('/', '_'))).Select(File.ReadAllText));
            Assert.NotEmpty(everything);
            Assert.DoesNotContain("MARKER_strolls", everything, StringComparison.Ordinal);
            Assert.DoesNotContain("bookbuilds", everything, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
