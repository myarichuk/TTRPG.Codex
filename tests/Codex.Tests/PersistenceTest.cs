using Codex.Persistence;
using Raven.Client.Documents;

namespace Codex.Tests;

public class RavenDbFixture : IDisposable
{
    public string DbPath { get; }
    public string DbName { get; }

    public RavenDbFixture()
    {
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX", "2");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_PRE_RELEASE", "1");

        DbPath = Path.Combine(Path.GetTempPath(), "TestRavenData_" + Guid.NewGuid());
        DbName = "Campaigns_" + Guid.NewGuid().ToString();
    }

    public void Dispose()
    {
        // Give RavenDB a moment to release file handles
        Task.Delay(1000).Wait();

        try
        {
            if (Directory.Exists(DbPath))
            {
                Directory.Delete(DbPath, true);
            }
        }
        catch { }
    }
}

/// <summary>Never rejects a SystemId - persistence tests aren't exercising plugin discovery.</summary>
public class NoOpSystemCatalog : Codex.Plugin.Abstractions.ISystemCatalog
{
    public bool IsLoaded => false;
    public IReadOnlySet<string> LoadedSystemIds { get; } = new HashSet<string>();
    public IEnumerable<Codex.Plugin.Abstractions.UISchema> GetUISchemas(string systemId) => Enumerable.Empty<Codex.Plugin.Abstractions.UISchema>();
}

public class PersistenceTest : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly CampaignRepository _campaignRepository;
    private readonly ActorRepository _actorRepository;
    private readonly RavenUserRepository _userRepository;
    private readonly RavenSessionRepository _sessionRepository;
    private readonly RavenNoteRepository _noteRepository;
    private readonly RegionRepository _regionRepository;
    private readonly FactRepository _factRepository;

    public PersistenceTest(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actorRepository = new ActorRepository(_dbService);
        _userRepository = new RavenUserRepository(_dbService);
        _sessionRepository = new RavenSessionRepository(_dbService);
        _noteRepository = new RavenNoteRepository(_dbService);
        _regionRepository = new RegionRepository(_dbService);
        _factRepository = new FactRepository(_dbService);
        _campaignRepository = new CampaignRepository(_dbService, new NoOpSystemCatalog(), _actorRepository, _sessionRepository, _noteRepository, _regionRepository, _factRepository);
    }

    [Fact]
    public async Task SaveAndLoadCampaign_ShouldSucceed_Async()
    {
        var campaign = new CampaignDocument
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Async Campaign",
            SystemId = "DnD5e"
        };

        await _campaignRepository.SaveAsync(campaign);
        var loaded = await _campaignRepository.GetAsync(campaign.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Async Campaign", loaded.Name);
    }

    [Fact]
    public async Task SaveAndLoadActor_ShouldSucceed_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Name = "Test Character"
        };

        actor.State["HP"] = 10;

        await _actorRepository.SaveAsync(actor);
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var loaded = await _actorRepository.GetVisibleAsync(actor.Id, dmAccess);

        Assert.NotNull(loaded);
        Assert.Equal("Test Character", loaded.Name);
        Assert.Equal(10, Convert.ToInt32(loaded.State["HP"]?.ToString()));
    }

    [Fact]
    public async Task SaveAndLoadSession_ShouldSucceed_Async()
    {
        var sessionDoc = new SessionDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = Guid.NewGuid().ToString(),
            Title = "Test Session",
            Recap = "This is a recap."
        };

        await _sessionRepository.SaveAsync(sessionDoc);
        var loaded = await _sessionRepository.GetAsync(sessionDoc.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Test Session", loaded.Title);
        Assert.Equal("This is a recap.", loaded.Recap);
    }

    [Fact]
    public async Task GetAllSessionsForCampaign_ShouldReturnCorrect_Async()
    {
        var campaignId = Guid.NewGuid().ToString();

        var session1 = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Title = "Session 1" };
        var session2 = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Title = "Session 2" };
        var session3 = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = Guid.NewGuid().ToString(), Title = "Other Campaign Session" };

        await _sessionRepository.SaveAsync(session1);
        await _sessionRepository.SaveAsync(session2);
        await _sessionRepository.SaveAsync(session3);

        // Wait for RavenDB indexes to process (since we use a query)
        using var session = _dbService.Store.OpenAsyncSession();
        await session.Query<SessionDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();

        var loaded = await _sessionRepository.GetAllForCampaignAsync(campaignId);

        Assert.NotNull(loaded);
        var list = loaded.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Title == "Session 1");
        Assert.Contains(list, s => s.Title == "Session 2");
    }

    [Fact]
    public async Task GetVisibleForCampaign_AsDm_ReturnsCorrect_Async()
    {
        var campaignId = Guid.NewGuid().ToString();

        var char1 = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Char 1" };
        var char2 = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Char 2" };
        var char3 = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = Guid.NewGuid().ToString(), Name = "Other Campaign Char" };

        await _actorRepository.SaveAsync(char1);
        await _actorRepository.SaveAsync(char2);
        await _actorRepository.SaveAsync(char3);

        // Wait for RavenDB indexes to process (since we use a query)
        using var session = _dbService.Store.OpenAsyncSession();
        await session.Query<ActorDocument, ActorsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var loaded = await _actorRepository.GetVisibleForCampaignAsync(dmAccess);

        Assert.NotNull(loaded);
        var list = loaded.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, c => c.Name == "Char 1");
        Assert.Contains(list, c => c.Name == "Char 2");
    }

    [Fact]
    public async Task GetVisibleForCampaign_AsPlayer_HidesUnownedUnknownActors_Async()
    {
        var campaignId = Guid.NewGuid().ToString();

        var known = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Known NPC", Visibility = ActorVisibility.Known };
        var owned = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "My PC", Visibility = ActorVisibility.Hidden, OwnerUserId = "player-1" };
        var hidden = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Secret Villain", Visibility = ActorVisibility.Hidden };

        await _actorRepository.SaveAsync(known);
        await _actorRepository.SaveAsync(owned);
        await _actorRepository.SaveAsync(hidden);

        using var session = _dbService.Store.OpenAsyncSession();
        await session.Query<ActorDocument, ActorsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var loaded = (await _actorRepository.GetVisibleForCampaignAsync(playerAccess)).ToList();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, a => a.Name == "Known NPC");
        Assert.Contains(loaded, a => a.Name == "My PC");
        Assert.DoesNotContain(loaded, a => a.Name == "Secret Villain");
    }

    [Fact]
    public async Task GetVisibleAsync_AsPlayer_CannotLoadHiddenActorById_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hidden = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Secret Villain", Visibility = ActorVisibility.Hidden };
        await _actorRepository.SaveAsync(hidden);

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var loaded = await _actorRepository.GetVisibleAsync(hidden.Id, playerAccess);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task TryDeleteAsync_AsPlayer_CannotDeleteAnything_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Someone's PC", Visibility = ActorVisibility.Known };
        await _actorRepository.SaveAsync(actor);

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var deleted = await _actorRepository.TryDeleteAsync(actor.Id, playerAccess);

        Assert.False(deleted);
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        Assert.NotNull(await _actorRepository.GetVisibleAsync(actor.Id, dmAccess));
    }

    [Fact]
    public async Task TryDeleteAsync_AsDm_DeletesActor_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Doomed NPC" };
        await _actorRepository.SaveAsync(actor);

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var deleted = await _actorRepository.TryDeleteAsync(actor.Id, dmAccess);

        Assert.True(deleted);
        Assert.Null(await _actorRepository.GetVisibleAsync(actor.Id, dmAccess));
    }

    [Fact]
    public async Task GetNotesForTarget_AsPlayer_CannotSeeAnotherPlayersPrivateNote_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var targetId = Guid.NewGuid().ToString();

        var privateFromOther = new NoteDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            TargetId = targetId,
            AuthorId = "player-2",
            Content = "Player 2's secret theory",
            Visibility = CommentVisibility.Private
        };
        var ownPrivate = new NoteDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            TargetId = targetId,
            AuthorId = "player-1",
            Content = "My own secret theory",
            Visibility = CommentVisibility.Private
        };
        var publicNote = new NoteDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            TargetId = targetId,
            AuthorId = "player-2",
            Content = "Publicly known fact",
            Visibility = CommentVisibility.Public
        };
        await _noteRepository.CreateNoteAsync(privateFromOther);
        await _noteRepository.CreateNoteAsync(ownPrivate);
        await _noteRepository.CreateNoteAsync(publicNote);

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<NoteDocument, NotesByTargetIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var visible = (await _noteRepository.GetNotesForTargetAsync(targetId, playerAccess)).ToList();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, n => n.Content == "My own secret theory");
        Assert.Contains(visible, n => n.Content == "Publicly known fact");
        Assert.DoesNotContain(visible, n => n.Content == "Player 2's secret theory");

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var dmVisible = (await _noteRepository.GetNotesForTargetAsync(targetId, dmAccess)).ToList();
        Assert.Equal(3, dmVisible.Count);
    }

    [Fact]
    public async Task SaveAndLoadUser_ShouldSucceed_Async()
    {
        var user = new UserDocument
        {
            Id = Guid.NewGuid().ToString(),
            Username = "testuser",
            PasswordHash = "hashedpassword"
        };
        user.Roles.Add("Player");

        await _userRepository.CreateUserAsync(user);
        var loaded = await _userRepository.GetUserByIdAsync(user.Id);

        Assert.NotNull(loaded);
        Assert.Equal("testuser", loaded.Username);
        Assert.Contains("Player", loaded.Roles);
    }

    [Fact]
    public async Task GetUserByUsername_ShouldReturnCorrect_Async()
    {
        var user = new UserDocument
        {
            Id = Guid.NewGuid().ToString(),
            Username = "uniqueuser123",
            PasswordHash = "hash"
        };

        await _userRepository.CreateUserAsync(user);

        // Wait for RavenDB indexes to process (since we use a query)
        using var session = _dbService.Store.OpenAsyncSession();
        await session.Query<UserDocument>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();

        var loaded = await _userRepository.GetUserByUsernameAsync("uniqueuser123");

        Assert.NotNull(loaded);
        Assert.Equal(user.Id, loaded.Id);
    }

    [Fact]
    public async Task UpdateUser_ShouldSucceed_Async()
    {
        var user = new UserDocument
        {
            Id = Guid.NewGuid().ToString(),
            Username = "updateme",
            PasswordHash = "oldhash"
        };

        await _userRepository.CreateUserAsync(user);

        user.PasswordHash = "newhash";
        user.Roles.Add("DM");
        await _userRepository.UpdateUserAsync(user);

        var loaded = await _userRepository.GetUserByIdAsync(user.Id);

        Assert.NotNull(loaded);
        Assert.Equal("newhash", loaded.PasswordHash);
        Assert.Contains("DM", loaded.Roles);
    }

    [Fact]
    public async Task DeleteCampaign_ByNonOwner_ShouldBeForbidden_Async()
    {
        var campaign = new CampaignDocument
        {
            Id = Guid.NewGuid().ToString(),
            OwnerId = "owner-1",
            Name = "Owned Campaign"
        };
        await _campaignRepository.SaveAsync(campaign);

        var result = await _campaignRepository.DeleteAsync(campaign.Id, "someone-else");

        Assert.Equal(CampaignDeleteResult.Forbidden, result);
        Assert.NotNull(await _campaignRepository.GetAsync(campaign.Id));
    }

    [Fact]
    public async Task DeleteCampaign_ByOwner_CascadesToScopedDocuments_Async()
    {
        var campaign = new CampaignDocument
        {
            Id = Guid.NewGuid().ToString(),
            OwnerId = "owner-2",
            Name = "Cascade Campaign"
        };
        await _campaignRepository.SaveAsync(campaign);

        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaign.Id, Name = "Orphan Candidate" };
        var sessionDoc = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaign.Id, Title = "Orphan Session" };
        await _actorRepository.SaveAsync(actor);
        await _sessionRepository.SaveAsync(sessionDoc);

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<ActorDocument, ActorsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            await session.Query<SessionDocument, SessionsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var result = await _campaignRepository.DeleteAsync(campaign.Id, "owner-2");

        Assert.Equal(CampaignDeleteResult.Deleted, result);
        Assert.Null(await _campaignRepository.GetAsync(campaign.Id));
        var dmAccess = new CampaignAccess(campaign.Id, "owner-2", CampaignRole.DM);
        Assert.Null(await _actorRepository.GetVisibleAsync(actor.Id, dmAccess));
        Assert.Null(await _sessionRepository.GetAsync(sessionDoc.Id));
    }

    public void Dispose()
    {
        _dbService.Dispose();
    }
}
