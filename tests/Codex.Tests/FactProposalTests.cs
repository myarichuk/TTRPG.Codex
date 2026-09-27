using Codex.Persistence;

namespace Codex.Tests;

/// <summary>Player lore proposals stay DM-only until approved, then follow normal visibility.</summary>
[Collection("RavenDb")]
public class FactProposalTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly FactRepository _facts;

    public FactProposalTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "Facts_" + Guid.NewGuid(), runInMemory: true);
        _facts = new FactRepository(_dbService);
    }

    public void Dispose() => _dbService.Dispose();

    private static (string campaignId, CampaignAccess dm, CampaignAccess player, CampaignAccess otherPlayer) Setup()
    {
        var campaignId = Guid.NewGuid().ToString();
        return (
            campaignId,
            new CampaignAccess(campaignId, "dm-user", CampaignRole.DM),
            new CampaignAccess(campaignId, "player-1", CampaignRole.Player),
            new CampaignAccess(campaignId, "player-2", CampaignRole.Player));
    }

    [Fact]
    public async Task PlayerProposal_IsHiddenFromOtherPlayers_UntilApproved()
    {
        var (campaignId, dm, player, otherPlayer) = Setup();

        Assert.True(await _facts.ProposeAsync(new FactDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Summary = "The bridge is trapped",
            Visibility = FactVisibility.Public // ignored for players: forced DmOnly
        }, player));

        // DM sees the proposal; other players don't, even though Public was requested.
        Assert.Single(await _facts.GetVisibleForCampaignAsync(dm, new HashSet<string>()));
        Assert.Empty(await _facts.GetVisibleForCampaignAsync(otherPlayer, new HashSet<string>()));

        var proposed = (await _facts.GetVisibleForCampaignAsync(dm, new HashSet<string>())).Single();
        Assert.Equal(FactStatus.Proposed, proposed.Status);
        Assert.Equal(FactVisibility.DmOnly, proposed.Visibility);
        Assert.Equal("player-1", proposed.AuthorId);

        Assert.True(await _facts.ApproveAsync(proposed.Id, FactVisibility.Public, dm));
        Assert.Single(await _facts.GetVisibleForCampaignAsync(otherPlayer, new HashSet<string>()));
    }

    [Fact]
    public async Task OnlyDm_CanApproveOrDelete()
    {
        var (campaignId, dm, player, _) = Setup();
        var fact = new FactDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Summary = "Secret plot"
        };
        Assert.True(await _facts.ProposeAsync(fact, player));

        Assert.False(await _facts.ApproveAsync(fact.Id, FactVisibility.Public, player));
        Assert.False(await _facts.DeleteAsync(fact.Id, player));
        Assert.Single(await _facts.GetVisibleForCampaignAsync(dm, new HashSet<string>()));

        Assert.True(await _facts.DeleteAsync(fact.Id, dm));
        Assert.Empty(await _facts.GetVisibleForCampaignAsync(dm, new HashSet<string>()));
    }

    [Fact]
    public async Task DmSave_MarksApproved_WithAuthor()
    {
        var (campaignId, dm, _, otherPlayer) = Setup();
        Assert.True(await _facts.SaveAsync(new FactDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Summary = "Canon entry",
            Visibility = FactVisibility.Public,
            AuthorId = "dm-user"
        }, dm));

        var visible = await _facts.GetVisibleForCampaignAsync(otherPlayer, new HashSet<string>());
        var entry = Assert.Single(visible);
        Assert.Equal(FactStatus.Approved, entry.Status);
    }
}
