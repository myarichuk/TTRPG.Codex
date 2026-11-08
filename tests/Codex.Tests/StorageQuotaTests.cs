using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Tests;

/// <summary>The storage limit per account (docs/SERVER_ACCESS_AND_VAULT.md 9, built in 15).</summary>
public class StorageQuotaTests : IClassFixture<RavenDbFixture>
{
    private const int Kb = 1024;
    private readonly RavenDbService _db;
    private readonly RavenServerSettingsStore _settings;
    private readonly StorageQuota _quota;
    private readonly RavenMediaStore _media;
    private readonly RavenBookArtRepository _bookArt;

    public StorageQuotaTests(RavenDbFixture fixture)
    {
        _db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _settings = new RavenServerSettingsStore(_db);
        _quota = new StorageQuota(_db, _settings);
        _media = new RavenMediaStore(_db, _quota);
        _bookArt = new RavenBookArtRepository(_db, _quota);
    }

    private static string User() => "user-" + Guid.NewGuid().ToString("N");

    private async Task<CampaignDocument> CampaignAsync(string owner, params string[] players)
    {
        var campaign = new CampaignDocument { Id = "campaigns/" + Guid.NewGuid().ToString("N"), OwnerId = owner, Name = "Camp", SystemId = "srd5e" };
        campaign.Members.AddRange(players.Select(p => new CampaignMember { UserId = p, Role = CampaignRole.Player }));
        using var session = _db.Store.OpenAsyncSession();
        await session.StoreAsync(campaign);
        await session.SaveChangesAsync();
        return campaign;
    }

    private async Task<ActorDocument> CharacterAsync(CampaignDocument campaign, string player)
    {
        var actor = new ActorDocument { Id = "actors/" + Guid.NewGuid().ToString("N"), CampaignId = campaign.Id, OwnerUserId = player, Kind = ActorKind.PlayerCharacter, Name = "Hero" };
        using var session = _db.Store.OpenAsyncSession();
        await session.StoreAsync(actor);
        await session.SaveChangesAsync();
        return actor;
    }

    private Task PutAsync(CampaignAccess access, string documentId, string name, int bytes) =>
        _media.PutAsync(access, documentId, name, new MemoryStream(new byte[bytes]), "image/png");

    private Task LimitAsync(int? mb) => _settings.UpdateAsync(s => s.StorageLimitMb = mb);

    [Fact]
    public async Task ACampaignsFilesCountAgainstItsOwner_WhoeverAddsThem_AndAReplacedFileCountsOnce_Async()
    {
        var (owner, player, other) = (User(), User(), User());
        var campaign = await CampaignAsync(owner, player);
        var hero = await CharacterAsync(campaign, player);
        var elsewhere = await CampaignAsync(other);
        var gm = new CampaignAccess(campaign.Id, owner, CampaignRole.DM);
        await LimitAsync(1);
        try
        {
            await PutAsync(gm, campaign.Id, RavenMediaStore.CampaignCover, 600 * Kb);
            Assert.Equal(1024L * 1024, _quota.LimitBytes);
            Assert.Equal(600 * Kb, await _quota.UsedByAsync(owner));

            // The player's portrait would take the owner past 1 MB: refused, and the owner's use says why.
            var refused = await Assert.ThrowsAsync<LocalizedException>(() =>
                PutAsync(new CampaignAccess(campaign.Id, player, CampaignRole.Player), hero.Id, RavenMediaStore.ActorPortrait, 600 * Kb));
            Assert.Equal("Service.Storage.Full", refused.Key);
            Assert.Null(await _media.GetAsync(hero.Id, RavenMediaStore.ActorPortrait));

            // A bigger cover in place of the old one counts only the new one.
            await PutAsync(gm, campaign.Id, RavenMediaStore.CampaignCover, 900 * Kb);
            Assert.Equal(900 * Kb, await _quota.UsedByAsync(owner));
            Assert.Equal(0, await _quota.UsedByAsync(player));
            Assert.True(await _quota.HasRoomAsync(campaign.Id));

            // Another owner's campaign has its own room.
            await PutAsync(new CampaignAccess(elsewhere.Id, other, CampaignRole.DM), elsewhere.Id, RavenMediaStore.CampaignCover, 900 * Kb);
            var byOwner = await _quota.ByOwnerAsync();
            Assert.Equal((900L * Kb, 900L * Kb), (byOwner[owner], byOwner[other]));
            Assert.False(byOwner.ContainsKey(player));

            // The book's pictures count too, and are refused the same way.
            var slot = "s" + new string('a', 24);
            var picture = new BookImage(new byte[200 * Kb], "image/png", 10, 10);
            var full = await Assert.ThrowsAsync<LocalizedException>(() => _bookArt.PutAsync(gm, slot, BookArtDocument.Dm, "Map", picture));
            Assert.Equal("Service.Storage.Full", full.Key);
            Assert.True(await _bookArt.PutAsync(gm, slot, BookArtDocument.Dm, "Map", new BookImage(new byte[100 * Kb], "image/png", 10, 10)));
            Assert.Equal(1000 * Kb, await _quota.UsedByAsync(owner));

            // Exactly at the limit nothing more is drawn.
            await PutAsync(gm, campaign.Id, RavenMediaStore.CampaignCover, 924 * Kb);
            Assert.False(await _quota.HasRoomAsync(campaign.Id));
            Assert.True(await _quota.HasRoomAsync(elsewhere.Id));
        }
        finally
        {
            await LimitAsync(null);
        }
    }

    [Fact]
    public async Task WithNoLimit_NothingIsRefused_Async()
    {
        var owner = User();
        var campaign = await CampaignAsync(owner);
        await LimitAsync(null);
        await PutAsync(new CampaignAccess(campaign.Id, owner, CampaignRole.DM), campaign.Id, RavenMediaStore.CampaignCover, 5 * 1024 * Kb);
        Assert.Null(_quota.LimitBytes);
        Assert.True(await _quota.HasRoomAsync(campaign.Id));
        await _quota.EnsureRoomAsync(campaign.Id, long.MaxValue / 2);
        Assert.Equal(5L * 1024 * Kb, await _quota.UsedByAsync(owner));
    }
}
