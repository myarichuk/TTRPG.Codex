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

/// <summary>Every test class that spins up its own embedded RavenDB instance shares this
/// collection so xUnit never runs two of them in parallel. Different classes each racing to start
/// (or query) the same underlying EmbeddedServer.Instance process is flaky under load - not a bug
/// in the classes under test, just resource contention xUnit's default per-class parallelism
/// doesn't know to avoid.</summary>
[CollectionDefinition("RavenDb", DisableParallelization = true)]
public class RavenDbCollection;

[Collection("RavenDb")]
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
    private readonly RavenEncounterRepository _encounterRepository;
    private readonly CampaignExportService _exportService;

    public PersistenceTest(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actorRepository = new ActorRepository(_dbService);
        _userRepository = new RavenUserRepository(_dbService);
        _sessionRepository = new RavenSessionRepository(_dbService);
        _noteRepository = new RavenNoteRepository(_dbService);
        _regionRepository = new RegionRepository(_dbService);
        _factRepository = new FactRepository(_dbService);
        _encounterRepository = new RavenEncounterRepository(_dbService);
        _campaignRepository = new CampaignRepository(_dbService, new NoOpSystemCatalog(), _actorRepository, _sessionRepository, _noteRepository, _regionRepository, _factRepository);
        _exportService = new CampaignExportService(_campaignRepository, _actorRepository, _sessionRepository, _encounterRepository, _factRepository, _noteRepository, _regionRepository);
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
    public async Task TrySaveAsync_AsOwner_UpdatesOwnPc_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "My PC", OwnerUserId = "player-1", State = new Dictionary<string, object> { ["HP"] = 10 } };
        await _actorRepository.SaveAsync(actor);

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        actor.State["HP"] = 8;
        Assert.True(await _actorRepository.TrySaveAsync(actor, playerAccess));

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var reloaded = await _actorRepository.GetVisibleAsync(actor.Id, dmAccess);
        Assert.Equal(8, Convert.ToInt32(reloaded!.State["HP"]?.ToString()));
    }

    [Fact]
    public async Task TrySaveAsync_AsNonOwner_IsRejected_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Someone's PC", OwnerUserId = "player-2", State = new Dictionary<string, object> { ["HP"] = 10 } };
        await _actorRepository.SaveAsync(actor);

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        actor.State["HP"] = 1;
        Assert.False(await _actorRepository.TrySaveAsync(actor, playerAccess));

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var reloaded = await _actorRepository.GetVisibleAsync(actor.Id, dmAccess);
        Assert.Equal(10, Convert.ToInt32(reloaded!.State["HP"]?.ToString()));
    }

    [Fact]
    public async Task TrySaveAsync_AsPlayer_CannotStealOwnership_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Victim PC", OwnerUserId = "player-2" };
        await _actorRepository.SaveAsync(actor);

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        actor.OwnerUserId = "player-1";
        Assert.False(await _actorRepository.TrySaveAsync(actor, playerAccess));

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var reloaded = await _actorRepository.GetVisibleAsync(actor.Id, dmAccess);
        Assert.Equal("player-2", reloaded!.OwnerUserId);
    }

    [Fact]
    public async Task TrySaveAsync_AsPlayer_CanCreateOwnPc_ButNotOthers_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);

        var own = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "New PC", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1" };
        Assert.True(await _actorRepository.TrySaveAsync(own, playerAccess));

        var someoneElses = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Fake PC", OwnerUserId = "player-2" };
        Assert.False(await _actorRepository.TrySaveAsync(someoneElses, playerAccess));

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        Assert.NotNull(await _actorRepository.GetVisibleAsync(own.Id, dmAccess));
        Assert.Null(await _actorRepository.GetVisibleAsync(someoneElses.Id, dmAccess));
    }

    [Fact]
    public async Task TrySaveAsync_AsDm_CanSaveAnythingInCampaign_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "A PC", OwnerUserId = "player-1" };
        await _actorRepository.SaveAsync(actor);

        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        actor.Name = "Renamed by DM";
        Assert.True(await _actorRepository.TrySaveAsync(actor, dmAccess));

        var otherCampaignAccess = new CampaignAccess(Guid.NewGuid().ToString(), "dm-user", CampaignRole.DM);
        actor.Name = "Cross-campaign write";
        Assert.False(await _actorRepository.TrySaveAsync(actor, otherCampaignAccess));
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

    [Fact]
    public async Task RegionRepository_AsPlayer_HidesUnrevealedLocations_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);

        var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "The Sword Coast" };
        Assert.True(await _regionRepository.SaveAsync(region, dmAccess));

        var revealed = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Waterdeep", Visibility = ActorVisibility.Known };
        var secret = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Skullport", Visibility = ActorVisibility.Hidden };
        Assert.True(await _regionRepository.UpsertLocationAsync(region.Id, revealed, dmAccess));
        Assert.True(await _regionRepository.UpsertLocationAsync(region.Id, secret, dmAccess));

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<RegionDocument, RegionsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var forPlayer = (await _regionRepository.GetVisibleForCampaignAsync(playerAccess)).Single();
        Assert.Single(forPlayer.Locations);
        Assert.Equal("Waterdeep", forPlayer.Locations[0].Name);

        var forDm = (await _regionRepository.GetVisibleForCampaignAsync(dmAccess)).Single();
        Assert.Equal(2, forDm.Locations.Count);
    }

    [Fact]
    public async Task RegionRepository_SetLocationVisibilityAsync_AsPlayer_IsRejected_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "The North" };
        await _regionRepository.SaveAsync(region, dmAccess);
        var location = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Icewind Dale", Visibility = ActorVisibility.Hidden };
        await _regionRepository.UpsertLocationAsync(region.Id, location, dmAccess);

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var result = await _regionRepository.SetLocationVisibilityAsync(region.Id, location.Id, ActorVisibility.Known, playerAccess);

        Assert.False(result);
        var stillHidden = (await _regionRepository.GetAsync(region.Id, dmAccess))!.Locations.Single();
        Assert.Equal(ActorVisibility.Hidden, stillHidden.Visibility);
    }

    [Fact]
    public async Task FactRepository_AsPlayer_SeesPublicAndOwnKnowersOnlyFacts_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        const string myActorId = "actor-mine";

        var publicFact = new FactDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "The king is dead", Visibility = FactVisibility.Public };
        var knownToMe = new FactDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "The killer's name",
            Visibility = FactVisibility.KnowersOnly, KnownBy = new List<KnowerEntry> { new(myActorId, KnowledgeLevel.Full) }
        };
        var knownToSomeoneElse = new FactDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "A rival's secret",
            Visibility = FactVisibility.KnowersOnly, KnownBy = new List<KnowerEntry> { new("actor-not-mine", KnowledgeLevel.Full) }
        };
        var dmOnly = new FactDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "The DM's plot twist", Visibility = FactVisibility.DmOnly };

        foreach (var fact in new[] { publicFact, knownToMe, knownToSomeoneElse, dmOnly })
        {
            Assert.True(await _factRepository.SaveAsync(fact, dmAccess));
        }

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<FactDocument, FactsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var visible = (await _factRepository.GetVisibleForCampaignAsync(playerAccess, new HashSet<string> { myActorId })).ToList();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, f => f.Summary == "The king is dead");
        Assert.Contains(visible, f => f.Summary == "The killer's name");
        Assert.DoesNotContain(visible, f => f.Summary == "A rival's secret");
        Assert.DoesNotContain(visible, f => f.Summary == "The DM's plot twist");
    }

    [Fact]
    public async Task FactRepository_AddKnower_RevealsKnowersOnlyFactToThatActorsPlayer_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var fact = new FactDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "The bridge is trapped", Visibility = FactVisibility.KnowersOnly };
        Assert.True(await _factRepository.SaveAsync(fact, dmAccess));

        Assert.True(await _factRepository.AddKnowerAsync(fact.Id, new KnowerEntry("actor-scout", KnowledgeLevel.Full, Source: "Scouted ahead"), dmAccess));

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<FactDocument, FactsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var scoutAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var scoutSees = (await _factRepository.GetVisibleForCampaignAsync(scoutAccess, new HashSet<string> { "actor-scout" })).ToList();
        Assert.Contains(scoutSees, f => f.Summary == "The bridge is trapped");

        var otherAccess = new CampaignAccess(campaignId, "player-2", CampaignRole.Player);
        var otherSees = (await _factRepository.GetVisibleForCampaignAsync(otherAccess, new HashSet<string> { "actor-other" })).ToList();
        Assert.DoesNotContain(otherSees, f => f.Summary == "The bridge is trapped");
    }

    [Fact]
    public async Task FactRepository_AddKnower_AsPlayer_IsRejected_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var fact = new FactDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "The vault code", Visibility = FactVisibility.KnowersOnly };
        Assert.True(await _factRepository.SaveAsync(fact, dmAccess));

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        Assert.False(await _factRepository.AddKnowerAsync(fact.Id, new KnowerEntry("actor-sneaky", KnowledgeLevel.Full), playerAccess));

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<FactDocument, FactsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var reloaded = (await _factRepository.GetVisibleForCampaignAsync(dmAccess, new HashSet<string>())).Single(f => f.Id == fact.Id);
        Assert.Empty(reloaded.KnownBy);
    }

    [Fact]
    public async Task FactRepository_AddKnower_ReplacesExistingEntryForSameActor_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var fact = new FactDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "The duke's mood", Visibility = FactVisibility.KnowersOnly };
        Assert.True(await _factRepository.SaveAsync(fact, dmAccess));

        Assert.True(await _factRepository.AddKnowerAsync(fact.Id, new KnowerEntry("actor-spy", KnowledgeLevel.Rumor), dmAccess));
        Assert.True(await _factRepository.AddKnowerAsync(fact.Id, new KnowerEntry("actor-spy", KnowledgeLevel.Full, Source: "Overheard"), dmAccess));

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<FactDocument, FactsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var reloaded = (await _factRepository.GetVisibleForCampaignAsync(dmAccess, new HashSet<string>())).Single(f => f.Id == fact.Id);
        var entry = Assert.Single(reloaded.KnownBy);
        Assert.Equal("actor-spy", entry.EntityId);
        Assert.Equal(KnowledgeLevel.Full, entry.Level);
        Assert.Equal("Overheard", entry.Source);
    }

    [Fact]
    public async Task JoinByInviteCodeAsync_AddsPlayerMember_AndIsIdempotent_Async()
    {
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "owner-3", Name = "Curse of Strahd" };
        await _campaignRepository.SaveAsync(campaign);

        var (firstResult, firstCampaignId) = await _campaignRepository.JoinByInviteCodeAsync(campaign.InviteCode, "player-2");
        Assert.Equal(CampaignJoinResult.Joined, firstResult);
        Assert.Equal(campaign.Id, firstCampaignId);

        var reloaded = await _campaignRepository.GetAsync(campaign.Id);
        Assert.Contains(reloaded!.Members, m => m.UserId == "player-2" && m.Role == CampaignRole.Player);

        var (secondResult, _) = await _campaignRepository.JoinByInviteCodeAsync(campaign.InviteCode, "player-2");
        Assert.Equal(CampaignJoinResult.AlreadyMember, secondResult);

        var (badResult, badCampaignId) = await _campaignRepository.JoinByInviteCodeAsync("not-a-real-code", "player-2");
        Assert.Equal(CampaignJoinResult.InvalidCode, badResult);
        Assert.Null(badCampaignId);
    }

    [Fact]
    public async Task CampaignExport_RoundTrip_RestoresEveryAggregate_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner-1", Name = "Export Me", SystemId = "DnD5e" };
        await _campaignRepository.SaveAsync(campaign);

        var dmAccess = new CampaignAccess(campaignId, "owner-1", CampaignRole.DM);
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1", State = new Dictionary<string, object> { ["HP"] = 10, ["Class"] = "Rogue" } };
        await _actorRepository.SaveAsync(actor);
        var sessionDoc = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Title = "First Night", Recap = "Goblins!", Events = new List<SessionEvent> { new() { Type = "Combat", Description = "Ambushed" } } };
        await _sessionRepository.SaveAsync(sessionDoc);
        var encounter = new EncounterDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, SessionId = sessionDoc.Id, Round = 3 };
        await _encounterRepository.SaveAsync(encounter);
        var fact = new FactDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = "The bridge is trapped", Visibility = FactVisibility.KnowersOnly, KnownBy = new List<KnowerEntry> { new(actor.Id, KnowledgeLevel.Full) } };
        Assert.True(await _factRepository.SaveAsync(fact, dmAccess));
        await _noteRepository.CreateNoteAsync(new NoteDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, TargetId = $"session:{sessionDoc.Id}", AuthorId = "player-1", Content = "Don't trust the bridge", Visibility = CommentVisibility.Private });
        var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "The North" };
        Assert.True(await _regionRepository.SaveAsync(region, dmAccess));

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<FactDocument, FactsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            await session.Query<SessionDocument, SessionsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            await session.Query<EncounterDocument, EncountersByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            await session.Query<RegionDocument, RegionsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        var bundle = await _exportService.ExportAsync(dmAccess);
        Assert.NotNull(bundle);
        Assert.Equal("Export Me", bundle.Campaign!.Name);
        Assert.Single(bundle.Actors);
        Assert.Single(bundle.Sessions);
        Assert.Single(bundle.Encounters);
        Assert.Single(bundle.Facts);
        Assert.Single(bundle.Notes);
        Assert.Single(bundle.Regions);

        var json = CampaignExportService.Serialize(bundle);
        var restored = CampaignExportService.Deserialize(json);
        Assert.NotNull(restored);

        Assert.Equal(CampaignDeleteResult.Deleted, await _campaignRepository.DeleteAsync(campaignId, "owner-1"));
        await _encounterRepository.DeleteAllForCampaignAsync(campaignId);
        Assert.Null(await _campaignRepository.GetAsync(campaignId));

        Assert.True(await _exportService.ImportAsync(restored, "owner-1"));

        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.Query<FactDocument, FactsByCampaignIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            await session.Query<NoteDocument, NotesByTargetIndex>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        }

        Assert.Equal("Export Me", (await _campaignRepository.GetAsync(campaignId))!.Name);
        var reloadedActor = await _actorRepository.GetVisibleAsync(actor.Id, dmAccess);
        Assert.Equal(10, Convert.ToInt32(reloadedActor!.State["HP"]?.ToString()));
        Assert.Equal("Rogue", reloadedActor.State["Class"]?.ToString());
        Assert.Equal("Goblins!", (await _sessionRepository.GetAsync(sessionDoc.Id))!.Recap);
        Assert.Equal(3, (await _encounterRepository.GetAsync(encounter.Id))!.Round);
        Assert.Contains(await _factRepository.GetVisibleForCampaignAsync(dmAccess, new HashSet<string>()), f => f.Summary == "The bridge is trapped");
        Assert.Contains(await _noteRepository.GetNotesForTargetAsync($"session:{sessionDoc.Id}", dmAccess), n => n.Content == "Don't trust the bridge");
        Assert.Equal("The North", (await _regionRepository.GetAsync(region.Id, dmAccess))!.Name);
    }

    [Fact]
    public async Task CampaignExport_AsPlayer_ReturnsNull_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        await _campaignRepository.SaveAsync(new CampaignDocument { Id = campaignId, OwnerId = "owner-1", Name = "Secret" });

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        Assert.Null(await _exportService.ExportAsync(playerAccess));
    }

    [Fact]
    public async Task CampaignImport_RejectsBadFormat_AndStrangers_Async()
    {
        Assert.False(await _exportService.ImportAsync(null, "owner-1"));
        Assert.False(await _exportService.ImportAsync(new CampaignBundle { Format = "nope" }, "owner-1"));
        Assert.False(await _exportService.ImportAsync(new CampaignBundle(), "owner-1"));
        Assert.Null(CampaignExportService.Deserialize("not json"));

        var bundle = new CampaignBundle { Campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "owner-1", Name = "X" } };
        Assert.False(await _exportService.ImportAsync(bundle, "stranger"));
        Assert.True(await _exportService.ImportAsync(bundle, "owner-1"));
        Assert.Equal("X", (await _campaignRepository.GetAsync(bundle.Campaign.Id))!.Name);
    }

    [Fact]
    public async Task ScheduledBackup_ProducesBackupFile_AndIsIdempotent_Async()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "CodexBackup_" + Guid.NewGuid().ToString("N"));
        try
        {
            await _campaignRepository.SaveAsync(new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "owner-1", Name = "Back Me Up" });

            var firstTaskId = await _dbService.EnsureScheduledBackupAsync(backupDir);
            Assert.True(firstTaskId > 0);
            var secondTaskId = await _dbService.EnsureScheduledBackupAsync(backupDir);
            Assert.Equal(firstTaskId, secondTaskId);

            await _dbService.BackupNowAsync(firstTaskId, backupDir, TimeSpan.FromSeconds(90));
            Assert.NotEmpty(Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            try
            {
                Directory.Delete(backupDir, true);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        _dbService.Dispose();
    }
}
