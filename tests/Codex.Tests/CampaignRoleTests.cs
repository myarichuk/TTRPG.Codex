using Codex.Persistence;

namespace Codex.Tests;

/// <summary>
/// Per-user campaign roles: invites grant Player/Observer (never DM), members can pick
/// their own Player/Observer seat, DMs manage everyone else, and the owner's DM seat is
/// structural. Shares the "RavenDb" collection - embedded RavenDB is process-wide.
/// </summary>
[Collection("RavenDb")]
public class CampaignRoleTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly CampaignRepository _campaigns;
    private readonly CampaignAccessResolver _access;
    private bool _disposed;

    public CampaignRoleTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(
            Path.Combine(fixture.DbPath, "roles-" + Guid.NewGuid()),
            fixture.DbName + "-roles-" + Guid.NewGuid(),
            runInMemory: true);
        var actors = new ActorRepository(_dbService);
        var sessions = new RavenSessionRepository(_dbService);
        var notes = new RavenNoteRepository(_dbService);
        var regions = new RegionRepository(_dbService);
        var facts = new FactRepository(_dbService);
        _campaigns = new CampaignRepository(_dbService, new NoOpSystemCatalog(), actors, sessions, notes, regions, facts);
        _access = new CampaignAccessResolver(_campaigns);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dbService.Dispose();
    }

    private async Task<CampaignDocument> NewCampaignAsync(string ownerId)
    {
        var campaign = new CampaignDocument
        {
            Id = Guid.NewGuid().ToString(),
            OwnerId = ownerId,
            Name = "Role Test Campaign",
            SystemId = "DnD5e",
        };
        await _campaigns.SaveAsync(campaign);
        return campaign;
    }

    [Fact]
    public async Task Join_WithObserverRequest_GrantsObserver()
    {
        var campaign = await NewCampaignAsync("owner-1");

        var (result, id) = await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "watcher-1", CampaignRole.Observer);

        Assert.Equal(CampaignJoinResult.Joined, result);
        Assert.Equal(campaign.Id, id);
        Assert.Equal(CampaignRole.Observer, (await _access.ResolveAsync(campaign.Id, "watcher-1"))?.Role);
    }

    [Fact]
    public async Task Join_WithDmRequest_ClampsToPlayer()
    {
        var campaign = await NewCampaignAsync("owner-1");

        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "sneaky-1", CampaignRole.DM);

        // An invite link never mints a DM - that promotion belongs to an existing DM.
        Assert.Equal(CampaignRole.Player, (await _access.ResolveAsync(campaign.Id, "sneaky-1"))?.Role);
    }

    [Fact]
    public async Task Dm_PromotesPlayer_AndPlayerLosesDmOnlyVisibility()
    {
        var campaign = await NewCampaignAsync("owner-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-1");

        Assert.True(await _campaigns.SetMemberRoleAsync(campaign.Id, "player-1", CampaignRole.DM, "owner-1"));
        Assert.True((await _access.ResolveAsync(campaign.Id, "player-1"))?.IsDm == true);

        Assert.True(await _campaigns.SetMemberRoleAsync(campaign.Id, "player-1", CampaignRole.Player, "owner-1"));
        Assert.True((await _access.ResolveAsync(campaign.Id, "player-1"))?.IsDm == false);
    }

    [Fact]
    public async Task Player_CannotChangeAnotherMembersRole()
    {
        var campaign = await NewCampaignAsync("owner-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-2");

        Assert.False(await _campaigns.SetMemberRoleAsync(campaign.Id, "player-2", CampaignRole.Observer, "player-1"));
        Assert.Equal(CampaignRole.Player, (await _access.ResolveAsync(campaign.Id, "player-2"))?.Role);
    }

    [Fact]
    public async Task Member_CanSwitchOwnSeat_BetweenPlayerAndObserver()
    {
        var campaign = await NewCampaignAsync("owner-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-1");

        Assert.True(await _campaigns.SetMemberRoleAsync(campaign.Id, "player-1", CampaignRole.Observer, "player-1"));
        Assert.Equal(CampaignRole.Observer, (await _access.ResolveAsync(campaign.Id, "player-1"))?.Role);

        Assert.True(await _campaigns.SetMemberRoleAsync(campaign.Id, "player-1", CampaignRole.Player, "player-1"));
        Assert.Equal(CampaignRole.Player, (await _access.ResolveAsync(campaign.Id, "player-1"))?.Role);
    }

    [Fact]
    public async Task Member_CannotGrantSelfDm()
    {
        var campaign = await NewCampaignAsync("owner-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-1");

        Assert.False(await _campaigns.SetMemberRoleAsync(campaign.Id, "player-1", CampaignRole.DM, "player-1"));
        Assert.True((await _access.ResolveAsync(campaign.Id, "player-1"))?.IsDm == false);
    }

    [Fact]
    public async Task Owner_Role_IsLocked_AndOwner_CannotBeRemoved()
    {
        var campaign = await NewCampaignAsync("owner-1");

        Assert.False(await _campaigns.SetMemberRoleAsync(campaign.Id, "owner-1", CampaignRole.Player, "owner-1"));
        Assert.False(await _campaigns.RemoveMemberAsync(campaign.Id, "owner-1", "owner-1"));
        Assert.True((await _access.ResolveAsync(campaign.Id, "owner-1"))?.IsDm == true);
    }

    [Fact]
    public async Task Dm_RemovesMember_AndAccessResolvesToNull()
    {
        var campaign = await NewCampaignAsync("owner-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-1");

        Assert.True(await _campaigns.RemoveMemberAsync(campaign.Id, "player-1", "owner-1"));
        Assert.Null(await _access.ResolveAsync(campaign.Id, "player-1"));
    }

    [Fact]
    public async Task Member_CanLeave_ButPlayer_CannotRemoveAnother()
    {
        var campaign = await NewCampaignAsync("owner-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-1");
        await _campaigns.JoinByInviteCodeAsync(campaign.InviteCode, "player-2");

        Assert.False(await _campaigns.RemoveMemberAsync(campaign.Id, "player-2", "player-1"));

        Assert.True(await _campaigns.RemoveMemberAsync(campaign.Id, "player-1", "player-1"));
        Assert.Null(await _access.ResolveAsync(campaign.Id, "player-1"));
    }
}
