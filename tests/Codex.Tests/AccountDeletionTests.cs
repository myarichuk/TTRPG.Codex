using Codex.Persistence;

namespace Codex.Tests;

/// <summary>Deleting an account (docs/SERVER_ACCESS_AND_VAULT.md 9, built in 15).</summary>
public class AccountDeletionTests : IClassFixture<RavenDbFixture>
{
    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;
    private readonly RavenVaultRepository _vault;
    private readonly CampaignRepository _campaigns;
    private readonly RavenUserRepository _users;
    private readonly AccountDeletion _deletion;

    public AccountDeletionTests(RavenDbFixture fixture)
    {
        _db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actors = new ActorRepository(_db);
        _vault = new RavenVaultRepository(_db);
        _users = new RavenUserRepository(_db);
        _campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, new RavenSessionRepository(_db), new RavenNoteRepository(_db),
            new RegionRepository(_db), new FactRepository(_db), new RavenHomebrewRepository(_db), new RavenEncounterRepository(_db), vaultRepository: _vault);
        _deletion = new AccountDeletion(_db, _campaigns, _vault, _users);
    }

    private async Task<string> UserAsync()
    {
        var user = new UserDocument { Username = "u" + Guid.NewGuid().ToString("N")[..10], Status = AccountStatus.Approved };
        await _users.CreateUserAsync(user);
        return user.Id;
    }

    private async Task<CampaignDocument> CampaignAsync(string owner, params (string User, CampaignRole Role)[] members)
    {
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = owner, Name = "Camp " + Guid.NewGuid().ToString("N")[..4], SystemId = "srd5e" };
        campaign.Members.Add(new CampaignMember { UserId = owner, Role = CampaignRole.DM });
        campaign.Members.AddRange(members.Select(m => new CampaignMember { UserId = m.User, Role = m.Role }));
        await _campaigns.SaveAsync(campaign);
        return campaign;
    }

    private async Task StoreAsync(object document)
    {
        using var session = _db.Store.OpenAsyncSession();
        await session.StoreAsync(document);
        await session.SaveChangesAsync();
    }

    private async Task<int> CountAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> where)
    {
        using var session = _db.Store.OpenAsyncSession();
        return await Raven.Client.Documents.LinqExtensions.CountAsync(session.Query<T>().Customize(x => x.WaitForNonStaleResults()).Where(where));
    }

    [Fact]
    public async Task ItsCampaignsPassOn_ItLeavesTheRest_AndWhatIsItsAloneGoesWithIt_Async()
    {
        var (gone, admin, coGm, other, player) = (await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync());
        var withCoGm = await CampaignAsync(gone, (coGm, CampaignRole.DM), (player, CampaignRole.Player));
        var alone = await CampaignAsync(gone, (player, CampaignRole.Player));
        var wrongPick = await CampaignAsync(gone, (coGm, CampaignRole.DM), (player, CampaignRole.Player));
        var theirs = await CampaignAsync(other, (gone, CampaignRole.Player), (player, CampaignRole.Player));
        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = theirs.Id, OwnerUserId = gone, Kind = ActorKind.PlayerCharacter, Name = "Brann" };
        await _actors.SaveAsync(hero);
        var kept = await _vault.CreateAsync(gone, "srd5e", "Kept");
        await _vault.CreateAsync(gone, "srd5e", "Also kept");
        var someoneElses = await _vault.CreateAsync(player, "srd5e", "Not theirs");
        await _vault.AskAsync(new CampaignAccess(theirs.Id, gone, CampaignRole.Player), new CharacterRequestDocument { VaultActorId = kept.Id });
        await StoreAsync(new JournalEntryDocument { CampaignId = theirs.Id, OwnerUserId = gone, Title = "Mine" });
        await StoreAsync(new JournalEntryDocument { CampaignId = theirs.Id, OwnerUserId = player, Title = "Theirs" });
        await StoreAsync(new WorldChatDocument { CampaignId = alone.Id, OwnerUserId = gone, Title = "Chat" });
        await StoreAsync(new BookBuildDocument { CampaignId = alone.Id, OwnerUserId = gone, Audience = "dm" });
        await StoreAsync(new BookBuildDocument { CampaignId = withCoGm.Id, OwnerUserId = coGm, Audience = "dm" });

        var plan = await _deletion.PlanAsync(gone);
        Assert.Equal(3, plan.Owned.Count);
        Assert.Equal(new[] { coGm }, plan.Owned.Single(o => o.CampaignId == withCoGm.Id).CoGms);
        Assert.Empty(plan.Owned.Single(o => o.CampaignId == alone.Id).CoGms);
        Assert.Equal((1, 2), (plan.Memberships, plan.VaultCharacters));

        var result = await _deletion.DeleteAsync(gone, admin, new Dictionary<string, string> { [withCoGm.Id] = coGm, [wrongPick.Id] = player });
        Assert.Equal(AccountDeletionResult.Deleted, result);

        // The chosen co-GM takes the first; the one with no co-GM goes to the admin, made its GM; a pick who is no co-GM is not obeyed.
        var first = (await _campaigns.GetAsync(withCoGm.Id))!;
        Assert.Equal(coGm, first.OwnerId);
        Assert.DoesNotContain(first.Members, m => m.UserId == gone);
        var second = (await _campaigns.GetAsync(alone.Id))!;
        Assert.Equal(admin, second.OwnerId);
        Assert.Contains(second.Members, m => m.UserId == admin && m.Role == CampaignRole.DM);
        Assert.Contains(second.Members, m => m.UserId == player && m.Role == CampaignRole.Player);
        Assert.Equal(admin, (await _campaigns.GetAsync(wrongPick.Id))!.OwnerId);

        // It left the other campaign; the character it played there stays in that cast, and its request went.
        var left = (await _campaigns.GetAsync(theirs.Id))!;
        Assert.DoesNotContain(left.Members, m => m.UserId == gone);
        Assert.NotNull(await _actors.GetVisibleAsync(hero.Id, new CampaignAccess(theirs.Id, other, CampaignRole.DM)));
        Assert.Empty(await _vault.GetRequestsAsync(new CampaignAccess(theirs.Id, other, CampaignRole.DM)));

        // Its vault, journals, chats and builds are gone; nobody else's are.
        Assert.Empty(await _vault.GetAllAsync(gone));
        Assert.NotNull(await _vault.GetAsync(someoneElses.Id, player));
        Assert.Equal(0, await CountAsync<JournalEntryDocument>(j => j.OwnerUserId == gone));
        Assert.Equal(1, await CountAsync<JournalEntryDocument>(j => j.OwnerUserId == player));
        Assert.Equal(0, await CountAsync<WorldChatDocument>(c => c.OwnerUserId == gone));
        Assert.Equal(0, await CountAsync<BookBuildDocument>(b => b.OwnerUserId == gone));
        Assert.Equal(1, await CountAsync<BookBuildDocument>(b => b.OwnerUserId == coGm));

        Assert.Null(await _users.GetUserByIdAsync(gone));
        Assert.Equal(AccountDeletionResult.NotFound, await _deletion.DeleteAsync(gone, admin));
    }

    [Fact]
    public async Task AHandOver_OnlyMovesACampaignItsOwnerStillOwns_SoDeletingAgainIsSafe_Async()
    {
        var (gone, admin, coGm) = (await UserAsync(), await UserAsync(), await UserAsync());
        var campaign = await CampaignAsync(gone, (coGm, CampaignRole.DM), (admin, CampaignRole.Player));

        // An earlier attempt passed it on and stopped: the next one leaves it with its new owner.
        await _campaigns.UpdateAsync(campaign.Id, c => AccountDeletion.HandOver(c, gone, coGm, admin));
        await _campaigns.UpdateAsync(campaign.Id, c => AccountDeletion.HandOver(c, gone, null, admin));
        Assert.Equal(coGm, (await _campaigns.GetAsync(campaign.Id))!.OwnerId);

        Assert.Equal(AccountDeletionResult.Deleted, await _deletion.DeleteAsync(gone, admin));
        var stored = (await _campaigns.GetAsync(campaign.Id))!;
        Assert.Equal(coGm, stored.OwnerId);
        Assert.Equal(CampaignRole.Player, stored.Members.Single(m => m.UserId == admin).Role);
        Assert.DoesNotContain(stored.Members, m => m.UserId == gone);

        // Handed to the admin who already plays in it, the admin becomes its GM rather than a second member.
        var another = await CampaignAsync(coGm, (admin, CampaignRole.Player));
        await _campaigns.UpdateAsync(another.Id, c => AccountDeletion.HandOver(c, coGm, null, admin));
        var taken = (await _campaigns.GetAsync(another.Id))!;
        Assert.Equal(admin, taken.OwnerId);
        Assert.Equal(CampaignRole.DM, taken.Members.Single(m => m.UserId == admin).Role);

        await Assert.ThrowsAsync<ArgumentException>(() => _deletion.DeleteAsync(admin, admin));
    }
}
