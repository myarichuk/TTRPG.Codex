using Codex.Localization;
using Codex.Core;
using Codex.Persistence;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;

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

        // Runs killed before Dispose leave their data behind, so clear out the old ones first.
        TestTempData.SweepStale(TestTempData.Root, "TestRavenData_*");
        TestTempData.SweepOrphans(TestTempData.Root, "TestRavenData_");
        TestTempData.SweepStale(Path.Combine(AppContext.BaseDirectory, "RavenDBServer", "Temp"), "*");
        TestTempData.SweepOrphanFiles(Path.Combine(AppContext.BaseDirectory, "RavenDBServer", "Temp"));

        DbPath = Path.Combine(TestTempData.Root, TestTempData.OwnedName("TestRavenData_"));
        TestTempData.DeleteOnExit(DbPath);
        DbName = "Campaigns_" + Guid.NewGuid().ToString();
    }

    public void Dispose()
    {
        DropDatabase();
        TestTempData.DeleteDirectory(DbPath);
    }

    // The embedded server is one per process, so this class's database lives in the server's folder (the first fixture's), not in
    // DbPath, and stays there, scratch files and all, until the process ends: a full run piles up gigabytes. Dropping it when the
    // class is done keeps the footprint to one class's worth.
    private void DropDatabase()
    {
        try
        {
            using var store = EmbeddedServer.Instance.GetDocumentStore(new DatabaseOptions(DbName) { SkipCreatingDatabase = true });
            store.Maintenance.Server.Send(new DeleteDatabasesOperation(DbName, hardDelete: true));
        }
        catch (Exception ex)
        {
            // Nothing to drop (the server never started, or the class never made a database), or it is already gone.
            Console.WriteLine($"Could not drop test database {DbName}: {ex.Message}");
        }
    }
}

/// <summary>Never rejects a SystemId - persistence tests aren't exercising plugin discovery.</summary>
public class NoOpSystemCatalog : Codex.Plugin.Abstractions.ISystemCatalog
{
    public bool IsLoaded => false;
    public IReadOnlySet<string> LoadedSystemIds { get; } = new HashSet<string>();
    public IEnumerable<Codex.Plugin.Abstractions.SystemSummary> Systems => Enumerable.Empty<Codex.Plugin.Abstractions.SystemSummary>();
    public string GetDisplayName(string systemId) => systemId;
    public Codex.Plugin.Abstractions.SystemDefinition? GetDefinition(string systemId) => null;
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
    private readonly RavenHomebrewRepository _homebrewRepository;
    private readonly RavenWikiRepository _wikiRepository;
    private readonly RavenJournalRepository _journalRepository;
    private readonly RavenWorldChatRepository _worldChatRepository;
    private readonly RavenTemplateRepository _templateRepository;
    private readonly RavenMediaStore _mediaStore;
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
        _homebrewRepository = new RavenHomebrewRepository(_dbService);
        _wikiRepository = new RavenWikiRepository(_dbService);
        _journalRepository = new RavenJournalRepository(_dbService);
        _worldChatRepository = new RavenWorldChatRepository(_dbService);
        _templateRepository = new RavenTemplateRepository(_dbService);
        _mediaStore = new RavenMediaStore(_dbService);
        _campaignRepository = new CampaignRepository(_dbService, new NoOpSystemCatalog(), _actorRepository, _sessionRepository, _noteRepository, _regionRepository, _factRepository, _homebrewRepository, _encounterRepository, _wikiRepository, _journalRepository, _worldChatRepository,
            templateRepository: _templateRepository);
        _exportService = new CampaignExportService(_campaignRepository, _actorRepository, _sessionRepository, _encounterRepository, _factRepository, _noteRepository, _regionRepository, _homebrewRepository, _wikiRepository, _templateRepository);
    }

    [Fact]
    public async Task SaveAndLoadCampaign_ShouldSucceed_Async()
    {
        var campaign = new CampaignDocument
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Async Campaign",
            SystemId = "srd5e"
        };

        await _campaignRepository.SaveAsync(campaign);
        var loaded = await _campaignRepository.GetAsync(campaign.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Async Campaign", loaded.Name);
    }

    [Fact]
    public async Task ASheetNumberSavedAsADecimal_ComesBackReadableAsAnInteger()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Decimal" };
        actor.Sheet["level"] = 4.0; // the sheet form stores Number fields as doubles
        actor.Sheet["speed"] = 30L;

        await _actorRepository.SaveAsync(actor);
        var loaded = await _actorRepository.GetVisibleAsync(actor.Id, new CampaignAccess(campaignId, "dm-user", CampaignRole.DM));

        Assert.NotNull(loaded);
        Assert.Equal(4, Codex.Core.YamlValues.GetInt(loaded.Sheet, "level"));
        Assert.Equal(30, Codex.Core.YamlValues.GetInt(loaded.Sheet, "speed"));
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
    public async Task GetVisibleForCampaign_AsPlayer_LeavesHiddenActorsOut_Async()
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
    public async Task Facts_ReachPlayersWithoutTheirDmBlocks_OrTheLinksInsideThem()
    {
        var campaignId = "camp-facts-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var fact = new FactDocument
        {
            Summary = "The lighthouse is dark",
            Details = "Nobody has climbed it in years.\n:::dm\n[[actor:villain]] hides at the top.\n:::",
            Visibility = FactVisibility.Public
        };
        Assert.True(await _factRepository.SaveAsync(fact, dm));

        var seen = Assert.Single(await _factRepository.GetVisibleForCampaignAsync(player, new HashSet<string>()));
        Assert.DoesNotContain("hides", seen.Details);
        Assert.DoesNotContain("actor:villain", seen.Details);
        Assert.DoesNotContain(FactLinks.Actor("villain"), seen.RelatedEntityIds);
        Assert.Empty(FactLinks.About(new[] { seen }, FactLinks.Actor("villain")));

        // The DM still sees and links it, and the player's copy can never be saved back over the DM's.
        var dmCopy = Assert.Single(await _factRepository.GetVisibleForCampaignAsync(dm, new HashSet<string>()));
        Assert.Contains("hides", dmCopy.Details);
        Assert.Single(FactLinks.About(new[] { dmCopy }, FactLinks.Actor("villain")));
        Assert.False(await _factRepository.SaveAsync(seen, dm));
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
    public async Task TryDeleteAsync_AsPlayer_CanOnlyRetireTheirOwnCharacter_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Someone's PC", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-2", Visibility = ActorVisibility.Known };
        await _actorRepository.SaveAsync(actor);
        var npc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Owned NPC?", Kind = ActorKind.NonPlayerCharacter, OwnerUserId = "player-1" };
        await _actorRepository.SaveAsync(npc);
        var mine = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "My PC", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1" };
        await _actorRepository.SaveAsync(mine);

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var deleted = await _actorRepository.TryDeleteAsync(actor.Id, playerAccess);

        Assert.False(deleted);
        Assert.False(await _actorRepository.TryDeleteAsync(npc.Id, playerAccess));
        Assert.True(await _actorRepository.TryDeleteAsync(mine.Id, playerAccess));
        Assert.Null(await _actorRepository.GetVisibleAsync(mine.Id, playerAccess));
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
    public async Task RegionRepository_AsPlayer_HidesRegionsWithNothingRevealed_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        // A region the world builder made for a quest: its name is a spoiler until one of its places is revealed.
        var planned = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "The Drowned Bell" };
        planned.Locations.Add(new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "The Sunken Belfry", Visibility = ActorVisibility.Hidden });
        var empty = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Unmapped" };
        Assert.True(await _regionRepository.SaveAsync(planned, dmAccess));
        Assert.True(await _regionRepository.SaveAsync(empty, dmAccess));

        var playerAccess = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        Assert.Empty(await _regionRepository.GetVisibleForCampaignAsync(playerAccess));
        Assert.Null(await _regionRepository.GetAsync(planned.Id, playerAccess));
        Assert.Equal(2, (await _regionRepository.GetVisibleForCampaignAsync(dmAccess)).Count());
    }

    [Fact]
    public async Task RegionRepository_RenameAsync_ChangesOnlyTheName_AndOnlyForTheDm_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dmAccess = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "The Norht" };
        await _regionRepository.SaveAsync(region, dmAccess);
        // Added after the page loaded its copy of the region: renaming must not drop it.
        await _regionRepository.UpsertLocationAsync(region.Id, new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Icewind Dale" }, dmAccess);

        Assert.False(await _regionRepository.RenameAsync(region.Id, "Player's pick", new CampaignAccess(campaignId, "player-1", CampaignRole.Player)));
        Assert.False(await _regionRepository.RenameAsync(region.Id, "   ", dmAccess));
        Assert.False(await _regionRepository.RenameAsync(region.Id, "Elsewhere", new CampaignAccess(Guid.NewGuid().ToString(), "dm-user", CampaignRole.DM)));
        Assert.True(await _regionRepository.RenameAsync(region.Id, "  The North ", dmAccess));

        var stored = (await _regionRepository.GetAsync(region.Id, dmAccess))!;
        Assert.Equal("The North", stored.Name);
        Assert.Equal("Icewind Dale", Assert.Single(stored.Locations).Name);
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
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner-1", Name = "Export Me", SystemId = "srd5e" };
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
    public async Task CampaignImport_NamingSomeoneElsesCampaign_IsRefused_AndChangesNothing_Async()
    {
        var victimId = Guid.NewGuid().ToString();
        var victim = new CampaignDocument { Id = victimId, OwnerId = "victim", Name = "Victim table", Members = { new CampaignMember { UserId = "victim-player", Role = CampaignRole.Player } } };
        await _campaignRepository.SaveAsync(victim);
        await _campaignRepository.SaveAsync(new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "mallory", Name = "Throwaway" });

        // Mallory is DM of her own campaign and forges a bundle for the victim's id naming herself owner.
        var forged = new CampaignBundle
        {
            Campaign = new CampaignDocument { Id = victimId, OwnerId = "mallory", Name = "Taken over" },
            Actors = { new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = victimId, Name = "Injected" } }
        };

        Assert.False(await _exportService.ImportAsync(forged, "mallory"));

        var after = await _campaignRepository.GetAsync(victimId);
        Assert.Equal("victim", after!.OwnerId);
        Assert.Equal("Victim table", after.Name);
        Assert.Empty(await _actorRepository.GetVisibleForCampaignAsync(new CampaignAccess(victimId, "victim", CampaignRole.DM)));
    }

    [Fact]
    public async Task CampaignImport_IntoAnExistingCampaign_KeepsTheStoredOwnerAndMembers_Async()
    {
        var id = Guid.NewGuid().ToString();
        await _campaignRepository.SaveAsync(new CampaignDocument
        {
            Id = id, OwnerId = "owner", Name = "Before", InviteCode = "live-code",
            Members = { new CampaignMember { UserId = "co-dm", Role = CampaignRole.DM }, new CampaignMember { UserId = "late-joiner", Role = CampaignRole.Player } }
        });

        // The file is an older export: it knows neither the late joiner nor the current invite code, and claims a different owner.
        var bundle = new CampaignBundle { Campaign = new CampaignDocument { Id = id, OwnerId = "someone-else", Name = "After", InviteCode = "stale" } };

        Assert.False(await _exportService.ImportAsync(bundle, "late-joiner"));
        Assert.True(await _exportService.ImportAsync(bundle, "co-dm"));

        var stored = await _campaignRepository.GetAsync(id);
        Assert.Equal("After", stored!.Name);
        Assert.Equal("owner", stored.OwnerId);
        Assert.Equal("live-code", stored.InviteCode);
        Assert.Contains(stored.Members, m => m.UserId == "late-joiner");
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

    [Fact]
    public async Task GetForUserAsync_ReturnsOnlyCampaignsTheUserOwnsOrBelongsTo()
    {
        var mine = new CampaignDocument { Id = "camps-mine-" + Guid.NewGuid(), OwnerId = "user-a", Name = "Mine", SystemId = "srd5e" };
        var joined = new CampaignDocument { Id = "camps-joined-" + Guid.NewGuid(), OwnerId = "user-b", Name = "Joined", SystemId = "srd5e" };
        joined.Members.Add(new CampaignMember { UserId = "user-a", Role = CampaignRole.Player });
        var other = new CampaignDocument { Id = "camps-other-" + Guid.NewGuid(), OwnerId = "user-c", Name = "Other", SystemId = "srd5e" };
        await _campaignRepository.SaveAsync(mine);
        await _campaignRepository.SaveAsync(joined);
        await _campaignRepository.SaveAsync(other);

        var visible = (await _campaignRepository.GetForUserAsync("user-a")).Select(c => c.Id).ToList();

        Assert.Contains(mine.Id, visible);
        Assert.Contains(joined.Id, visible);
        Assert.DoesNotContain(other.Id, visible);
    }

    [Fact]
    public async Task Homebrew_PlayersSeeOnlyPublished_AndOnlyDmCanWrite()
    {
        var campaignId = "camp-hb-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "player", CampaignRole.Player);

        var published = new HomebrewEntryDocument { CampaignId = campaignId, SystemId = "srd5e", Type = "spell", Name = "Thunderous Yawn", IsPublished = true };
        var draft = new HomebrewEntryDocument { CampaignId = campaignId, SystemId = "srd5e", Type = "spell", Name = "Secret Draft", IsPublished = false };
        Assert.True(await _homebrewRepository.TrySaveAsync(published, dm));
        Assert.True(await _homebrewRepository.TrySaveAsync(draft, dm));
        Assert.Equal("thunderous-yawn", published.EntryId);

        var playerAttempt = new HomebrewEntryDocument { CampaignId = campaignId, SystemId = "srd5e", Type = "spell", Name = "Cheat" };
        Assert.False(await _homebrewRepository.TrySaveAsync(playerAttempt, player));

        var forPlayer = (await _homebrewRepository.GetVisibleForCampaignAsync(player)).Select(h => h.Name).ToList();
        var forDm = (await _homebrewRepository.GetVisibleForCampaignAsync(dm)).Select(h => h.Name).ToList();
        Assert.Equal(new[] { "Thunderous Yawn" }, forPlayer);
        Assert.Equal(2, forDm.Count);

        Assert.Null(await _homebrewRepository.GetVisibleAsync(draft.Id, player));
        Assert.False(await _homebrewRepository.TryDeleteAsync(published.Id, player));
        Assert.True(await _homebrewRepository.TryDeleteAsync(published.Id, dm));
    }

    [Fact]
    public async Task DeletingCampaign_CascadesToHomebrew()
    {
        var campaignId = "camp-hb-del-" + Guid.NewGuid();
        await _campaignRepository.SaveAsync(new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Doomed", SystemId = "srd5e" });
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        await _homebrewRepository.TrySaveAsync(new HomebrewEntryDocument { CampaignId = campaignId, SystemId = "srd5e", Type = "feat", Name = "Gone Soon" }, dm);

        Assert.Equal(CampaignDeleteResult.Deleted, await _campaignRepository.DeleteAsync(campaignId, "owner"));

        Assert.Empty(await _homebrewRepository.GetAllForCampaignAsync(campaignId));
    }

    [Fact]
    public async Task ExportImport_RoundTripsHomebrew()
    {
        var campaignId = "camp-hb-export-" + Guid.NewGuid();
        await _campaignRepository.SaveAsync(new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Brewery", SystemId = "srd5e" });
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        await _homebrewRepository.TrySaveAsync(new HomebrewEntryDocument
        {
            CampaignId = campaignId, SystemId = "srd5e", Type = "spell", Name = "Thunderous Yawn",
            Properties = new Dictionary<string, object> { ["level"] = 2L, ["school"] = "Evocation" }
        }, dm);

        var bundle = await _exportService.ExportAsync(dm);
        Assert.NotNull(bundle);
        Assert.Single(bundle!.Homebrew);

        var json = CampaignExportService.Serialize(bundle);
        await _campaignRepository.DeleteAsync(campaignId, "owner");
        Assert.Empty(await _homebrewRepository.GetAllForCampaignAsync(campaignId));

        Assert.True(await _exportService.ImportAsync(CampaignExportService.Deserialize(json), "owner"));
        var restored = Assert.Single(await _homebrewRepository.GetAllForCampaignAsync(campaignId));
        Assert.Equal("Thunderous Yawn", restored.Name);
        Assert.Equal(2L, Convert.ToInt64(restored.Properties["level"]));
    }

    [Fact]
    public async Task Wiki_PlayersOnlySeePartyPages_AndOnlyTheDmWrites()
    {
        var campaignId = "camp-wiki-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "p1", CampaignRole.Player);

        Assert.False(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "Nope", Body = "" }, player));
        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "The Docks", Body = "See [[The Mayor]] and [[Greyharbour]].", Visibility = WikiVisibility.Party }, dm));
        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "The Mayor", Body = "Secretly a cultist.", Visibility = WikiVisibility.DmOnly }, dm));
        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "The Docks", Body = "Different page, same title." }, dm));

        var dmPages = (await _wikiRepository.GetVisibleForCampaignAsync(dm)).ToList();
        Assert.Equal(3, dmPages.Count);
        Assert.Contains(dmPages, p => p.Slug == "the-docks");
        Assert.Contains(dmPages, p => p.Slug == "the-docks-2");
        Assert.Equal(new[] { "greyharbour", "the-mayor" }, dmPages.First(p => p.Slug == "the-docks").Links.OrderBy(l => l));

        var playerPages = (await _wikiRepository.GetVisibleForCampaignAsync(player)).ToList();
        Assert.Single(playerPages);
        Assert.Equal("the-docks", playerPages[0].Slug);
        Assert.Null(await _wikiRepository.GetVisibleBySlugAsync("the-mayor", player));
        Assert.NotNull(await _wikiRepository.GetVisibleBySlugAsync("the-mayor", dm));

        var other = new CampaignAccess("another-campaign", "dm", CampaignRole.DM);
        Assert.False(await _wikiRepository.TryDeleteAsync(dmPages[0].Id, other));
    }

    [Fact]
    public async Task Wiki_PlayersGetSharedPagesWithoutTheirDmBlocks_AndNoLinkToASecret()
    {
        var campaignId = "camp-wiki-secret-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        var body = "A quiet village near [[The Cove]].\n\n:::dm\nThe lighthouse is the lair of [[The Drowned Cult]].\n:::\n\n:::party\nThe keeper drinks.\n:::";
        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "Saltmere", Body = body, Visibility = WikiVisibility.Party }, dm));
        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "The Cove", Body = "Sand.", Visibility = WikiVisibility.Party }, dm));
        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "The Drowned Cult", Body = "Dagon.", Visibility = WikiVisibility.DmOnly }, dm));

        var dmPage = (await _wikiRepository.GetVisibleBySlugAsync("saltmere", dm))!;
        Assert.Equal(body, dmPage.Body);
        Assert.Contains("the-drowned-cult", dmPage.Links);

        foreach (var seen in new[] { (await _wikiRepository.GetVisibleForCampaignAsync(player)).Single(p => p.Slug == "saltmere"), (await _wikiRepository.GetVisibleBySlugAsync("saltmere", player))! })
        {
            Assert.DoesNotContain("lighthouse", seen.Body);
            Assert.DoesNotContain("Drowned", seen.Body);
            Assert.DoesNotContain(":::", seen.Body);
            Assert.Contains("The keeper drinks.", seen.Body);
            Assert.Equal(new[] { "the-cove" }, seen.Links);
        }

        // "Pages linking here" on the secret page must not name Saltmere for a player either: the player cannot open the secret
        // page at all, and no player copy links to it.
        Assert.DoesNotContain(await _wikiRepository.GetVisibleForCampaignAsync(player), p => p.Links.Contains("the-drowned-cult"));

        // A player's copy can never be written back over the DM's text, even with a DM's access.
        var copy = (await _wikiRepository.GetVisibleBySlugAsync("saltmere", player))!;
        Assert.False(await _wikiRepository.TrySaveAsync(copy, dm));
        Assert.Equal(body, (await _wikiRepository.GetVisibleBySlugAsync("saltmere", dm))!.Body);
    }

    [Fact]
    public async Task Templates_AreTheDms_TakeOneAbbreviationEach_AndTravelWithTheCampaign()
    {
        var l = Localizer.For("en");
        var campaign = new CampaignDocument { Id = "camp-templates-" + Guid.NewGuid(), Name = "Templates", OwnerId = "dm", SystemId = "srd5e" };
        campaign.Members.Add(new CampaignMember { UserId = "dm", Role = CampaignRole.DM });
        await _campaignRepository.SaveAsync(campaign);
        var dm = new CampaignAccess(campaign.Id, "dm", CampaignRole.DM);
        var player = new CampaignAccess(campaign.Id, "p1", CampaignRole.Player);

        // A campaign starts with the built-in ones; a player is given none, and saves none.
        var shipped = await _templateRepository.GetAsync(dm, l);
        Assert.Equal(BuiltInTemplates.For(l).Select(t => t.Id), shipped.Select(t => t.Id));
        Assert.Empty(await _templateRepository.GetAsync(player, l));
        Assert.Equal(TemplateSave.Refused, await _templateRepository.TrySaveAsync(new TextTemplate { Name = "Mine", Body = "x" }, player, l));

        var loot = new TextTemplate { Name = "Loot", Abbreviation = "loot", Body = "- $item$ ($value:10 gp$)" };
        Assert.Equal(TemplateSave.Done, await _templateRepository.TrySaveAsync(loot, dm, l));
        Assert.NotEmpty(loot.Id);
        // An abbreviation in use, by one of theirs or a built-in one, is not taken twice; nor are ill-formed ones saved.
        Assert.Equal(TemplateSave.Taken, await _templateRepository.TrySaveAsync(new TextTemplate { Name = "Other", Abbreviation = "LOOT", Body = "y" }, dm, l));
        Assert.Equal(TemplateSave.Taken, await _templateRepository.TrySaveAsync(new TextTemplate { Name = "Other", Abbreviation = "npc", Body = "y" }, dm, l));
        Assert.Equal(TemplateSave.Refused, await _templateRepository.TrySaveAsync(new TextTemplate { Name = "Other", Abbreviation = "two words", Body = "y" }, dm, l));
        Assert.Equal(TemplateSave.Refused, await _templateRepository.TrySaveAsync(new TextTemplate { Id = BuiltInTemplates.Prefix + "nope", Name = "Other", Body = "y" }, dm, l));

        // Their version of a built-in one replaces it in place; turned off, it frees its abbreviation; removed, it is back as it ships.
        var npc = shipped.Single(t => t.Abbreviation == "npc");
        npc.Body = "**$name$** the $role$";
        npc.Hidden = true;
        Assert.Equal(TemplateSave.Done, await _templateRepository.TrySaveAsync(npc, dm, l));
        var mine = await _templateRepository.GetAsync(dm, l);
        Assert.Equal(npc.Id, mine[shipped.ToList().FindIndex(t => t.Id == npc.Id)].Id);
        Assert.True(mine.Single(t => t.Id == npc.Id).Hidden);
        Assert.Equal("Loot", mine[^1].Name);
        Assert.Equal(TemplateSave.Done, await _templateRepository.TrySaveAsync(new TextTemplate { Name = "Person, short", Abbreviation = "npc", Body = "$who$", PageKind = WikiPageKind.Person }, dm, l));
        Assert.Equal("Person, short", TemplateSet.ForKind(await _templateRepository.GetAsync(dm, l), WikiPageKind.Person)!.Name);
        Assert.Equal(TemplateSave.Refused, await _templateRepository.TryRemoveAsync(npc.Id, player, l));
        // Back as it ships, it would take "npc" again: not while another template has it.
        Assert.Equal(TemplateSave.Taken, await _templateRepository.TryRemoveAsync(npc.Id, dm, l));
        var shortPerson = (await _templateRepository.GetAsync(dm, l)).Single(t => t.Name == "Person, short");
        Assert.Equal(TemplateSave.Done, await _templateRepository.TryRemoveAsync(shortPerson.Id, dm, l));
        Assert.Equal(TemplateSave.Done, await _templateRepository.TryRemoveAsync(npc.Id, dm, l));
        Assert.Equal(BuiltInTemplates.For(l).Single(t => t.Id == npc.Id).Body, (await _templateRepository.GetAsync(dm, l)).Single(t => t.Id == npc.Id).Body);

        // They go out with the campaign, come back with it, and are deleted with it.
        var bundle = CampaignExportService.Deserialize(CampaignExportService.Serialize((await _exportService.ExportAsync(dm))!))!;
        await _templateRepository.DeleteAllForCampaignAsync(campaign.Id);
        Assert.DoesNotContain(await _templateRepository.GetAsync(dm, l), t => t.Name == "Loot");
        Assert.True(await _exportService.ImportAsync(bundle, "dm"));
        Assert.Equal("- $item$ ($value:10 gp$)", (await _templateRepository.GetAsync(dm, l)).Single(t => t.Name == "Loot").Body);
        Assert.Equal(CampaignDeleteResult.Deleted, await _campaignRepository.DeleteAsync(campaign.Id, "dm"));
        Assert.Null(await _templateRepository.GetDocumentAsync(campaign.Id));
    }

    [Fact]
    public async Task Wiki_TheDmRevealsAndHidesABlock_OnlyAsTheySawIt_AndABlockForACharacterReachesOnlyTheirPlayer()
    {
        var campaignId = "camp-wiki-reveal-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var ariaPlayer = new CampaignAccess(campaignId, "p-aria", CampaignRole.Player);
        var bramPlayer = new CampaignAccess(campaignId, "p-bram", CampaignRole.Player);
        var aria = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Aria", OwnerUserId = "p-aria", Visibility = ActorVisibility.Known };
        await _actorRepository.SaveAsync(aria);
        await _actorRepository.SaveAsync(new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Bram", OwnerUserId = "p-bram", Visibility = ActorVisibility.Known });

        var page = new WikiPageDocument { Title = "Saltmere", Body = "Village.\n:::dm\nThe keeper is a cultist.\n:::\n:::dm\nAria's mother lived here.\n:::", Visibility = WikiVisibility.Party };
        Assert.True(await _wikiRepository.TrySaveAsync(page, dm));

        // A player cannot reveal anything, and the DM cannot reveal a block that is no longer what they saw.
        Assert.Equal(SecretChange.Refused, await _wikiRepository.TrySetSecretAsync(page.Id, 0, "The keeper is a cultist.", SecretAudience.Party, null, ariaPlayer));
        Assert.Equal(SecretChange.Stale, await _wikiRepository.TrySetSecretAsync(page.Id, 0, "The keeper is a smuggler.", SecretAudience.Party, null, dm));
        Assert.DoesNotContain("cultist", (await _wikiRepository.GetVisibleBySlugAsync("saltmere", bramPlayer))!.Body);

        Assert.Equal(SecretChange.Done, await _wikiRepository.TrySetSecretAsync(page.Id, 0, "The keeper is a cultist.", SecretAudience.Party, null, dm));
        Assert.Equal(SecretChange.Done, await _wikiRepository.TrySetSecretAsync(page.Id, 1, "Aria's mother lived here.", SecretAudience.Some, new[] { aria.Id }, dm));

        var forBram = (await _wikiRepository.GetVisibleBySlugAsync("saltmere", bramPlayer))!;
        Assert.Contains("cultist", forBram.Body);
        Assert.DoesNotContain("mother", forBram.Body);
        // What a player is handed says when the block was revealed, so it can be shown as new, and nothing more.
        Assert.Matches(@":::party \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\nThe keeper is a cultist\.\n:::", forBram.Body);
        Assert.Contains("mother", (await _wikiRepository.GetVisibleForCampaignAsync(ariaPlayer)).Single().Body);
        Assert.DoesNotContain("mother", (await _wikiRepository.GetVisibleForCampaignAsync(dm.PartyPreview()!)).Single().Body);

        Assert.Equal(SecretChange.Done, await _wikiRepository.TrySetSecretAsync(page.Id, 0, "The keeper is a cultist.", SecretAudience.Dm, null, dm));
        Assert.DoesNotContain("cultist", (await _wikiRepository.GetVisibleBySlugAsync("saltmere", bramPlayer))!.Body);
    }

    [Fact]
    public async Task PartyPreview_ReadsExactlyAsAPlayerWhoOwnsNothing()
    {
        var campaignId = "camp-preview-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        Assert.Null(new CampaignAccess(campaignId, "p1", CampaignRole.Player).PartyPreview());
        var preview = dm.PartyPreview()!;
        Assert.False(preview.IsDm);
        Assert.Equal(campaignId, preview.CampaignId);

        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "Open", Body = "Seen\n:::dm\nHidden\n:::", Visibility = WikiVisibility.Party }, dm));
        Assert.True(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "Shut", Body = "x", Visibility = WikiVisibility.DmOnly }, dm));
        var seen = Assert.Single(await _wikiRepository.GetVisibleForCampaignAsync(preview));
        Assert.Equal("Seen", seen.Body);
        Assert.False(await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "Nope" }, preview));
    }

    [Fact]
    public async Task PlacesAndHistories_ReachPlayersWithoutTheirDmBlocks_AndAPlayersSaveKeepsThem()
    {
        var campaignId = "camp-secret-text-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "p1", CampaignRole.Player);

        var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Coast" };
        region.Locations.Add(new LocationDocument { Id = "loc-1", Name = "Lighthouse", Description = "Tall.\n:::dm\nCult lair.\n:::", Visibility = ActorVisibility.Known });
        Assert.True(await _regionRepository.SaveAsync(region, dm));
        Assert.Equal("Tall.", (await _regionRepository.GetVisibleForCampaignAsync(player)).Single().Locations.Single().Description);
        Assert.Equal("Tall.", (await _regionRepository.GetAsync(region.Id, player))!.Locations.Single().Description);
        Assert.Contains("Cult lair.", (await _regionRepository.GetAsync(region.Id, dm))!.Locations.Single().Description);

        var pc = new ActorDocument { CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p1", Biography = "Born at sea.\n:::dm\nHer father is the cult leader.\n:::" };
        Assert.True(await _actorRepository.TrySaveAsync(pc, dm));
        var mine = (await _actorRepository.GetVisibleAsync(pc.Id, player))!;
        Assert.Equal("Born at sea.", mine.Biography);

        mine.Biography = "Born at sea, raised by smugglers.";
        Assert.True(await _actorRepository.TrySaveAsync(mine, player));
        Assert.Equal("Born at sea, raised by smugglers.", (await _actorRepository.GetVisibleAsync(pc.Id, player))!.Biography);
        var stored = (await _actorRepository.GetVisibleAsync(pc.Id, dm))!.Biography!;
        Assert.StartsWith("Born at sea, raised by smugglers.", stored);
        Assert.Single(SecretText.SecretBlocks(stored), "Her father is the cult leader.");

        // Saving again (a live table applies a save twice) does not repeat the secret.
        Assert.True(await _actorRepository.TrySaveAsync((await _actorRepository.GetVisibleAsync(pc.Id, player))!, player));
        Assert.Single(SecretText.SecretBlocks((await _actorRepository.GetVisibleAsync(pc.Id, dm))!.Biography));
    }

    [Fact]
    public async Task APlayersSave_LeavesAnUntouchedHistoryAsStored_AndIsRefusedWhenTheDmChangedItMeanwhile()
    {
        var campaignId = "camp-history-race-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        const string history = "Born at sea.\n:::party 2026-10-01T10:00:00Z\nShe saw the drowned bell.\n:::\n:::dm\nHer father leads the cult.\n:::";
        var pc = new ActorDocument { CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p1", Biography = history };
        Assert.True(await _actorRepository.TrySaveAsync(pc, dm));

        // A save that leaves the history alone (a new name) keeps the DM's text exactly: markers in place, nothing moved.
        var mine = (await _actorRepository.GetVisibleAsync(pc.Id, player))!;
        mine.Name = "Aria Vell";
        Assert.True(await _actorRepository.TrySaveAsync(mine, player));
        Assert.Equal(history, (await _actorRepository.GetVisibleAsync(pc.Id, dm))!.Biography);

        // The DM reveals the secret while the player is editing.
        var editing = (await _actorRepository.GetVisibleAsync(pc.Id, player))!;
        var dmCopy = (await _actorRepository.GetVisibleAsync(pc.Id, dm))!;
        dmCopy.Biography = history.Replace(":::dm", ":::party 2026-10-30T10:00:00Z");
        Assert.True(await _actorRepository.TrySaveAsync(dmCopy, dm));

        editing.Biography += "\nShe left on a whaler.";
        var refused = await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() => _actorRepository.TrySaveAsync(editing, player));
        Assert.Equal("Actor.Sheet.HistoryChanged", refused.Key);
        Assert.Equal(dmCopy.Biography, (await _actorRepository.GetVisibleAsync(pc.Id, dm))!.Biography);

        // Loaded again, the player sees the reveal and can save their line.
        var fresh = (await _actorRepository.GetVisibleAsync(pc.Id, player))!;
        Assert.Contains("Her father leads the cult.", fresh.Biography);
        fresh.Biography += "\nShe left on a whaler.";
        Assert.True(await _actorRepository.TrySaveAsync(fresh, player));
        fresh.Biography += "\nAnd came back.";
        Assert.True(await _actorRepository.TrySaveAsync(fresh, player)); // its own last save is not a change by someone else
    }

    [Fact]
    public async Task AFactEdit_KeepsAKnowerAddedWhileItWasOpen()
    {
        var campaignId = "camp-fact-race-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var fact = new FactDocument { Summary = "The bell is cursed", Visibility = FactVisibility.DmOnly };
        Assert.True(await _factRepository.SaveAsync(fact, dm));
        var open = new FactDocument { Id = fact.Id, Summary = fact.Summary, Visibility = fact.Visibility }; // the edit form's copy

        Assert.True(await _factRepository.AddKnowerAsync(fact.Id, new KnowerEntry("aria", KnowledgeLevel.Rumor), dm));
        open.Summary = "The bell is cursed, and rings at night";
        Assert.True(await _factRepository.SaveAsync(open, dm));

        var stored = Assert.Single(await _factRepository.GetVisibleForCampaignAsync(dm, new HashSet<string>()));
        Assert.Equal("The bell is cursed, and rings at night", stored.Summary);
        Assert.Equal("aria", Assert.Single(stored.KnownBy).EntityId);
        Assert.Equal(FactVisibility.KnowersOnly, stored.Visibility);
    }

    [Fact]
    public async Task LinkingAPageToACastMember_KeepsARevealMadeMeanwhile_AndAnEarlierLink()
    {
        var dm = new CampaignAccess("camp-promote-race-" + Guid.NewGuid(), "dm", CampaignRole.DM);
        var page = new WikiPageDocument { Title = "Wendel", Body = "A fisher.\n:::dm\nA smuggler.\n:::", Visibility = WikiVisibility.Party };
        Assert.True(await _wikiRepository.TrySaveAsync(page, dm));
        var dialogCopy = (await _wikiRepository.GetVisibleForCampaignAsync(dm)).Single(); // the promote dialog's copy

        Assert.Equal(SecretChange.Done, await _wikiRepository.TrySetSecretAsync(page.Id, 0, "A smuggler.", SecretAudience.Party, null, dm));
        Assert.Equal("actor-1", await _wikiRepository.TryLinkActorAsync(dialogCopy.Id, "actor-1", WikiPageKind.Person, dialogCopy.ActorId, dm));
        // A second promote that raced the first gets the first one's character back, not a second link.
        Assert.Equal("actor-1", await _wikiRepository.TryLinkActorAsync(dialogCopy.Id, "actor-2", WikiPageKind.Person, dialogCopy.ActorId, dm));

        var stored = (await _wikiRepository.GetVisibleForCampaignAsync(dm)).Single();
        Assert.Equal(("actor-1", WikiPageKind.Person), (stored.ActorId, stored.Kind));
        Assert.Contains(":::party", stored.Body);
    }

    [Fact]
    public async Task AnUnknownActor_ReachesPlayersAsAStranger_WithNothingButTheirPublicFace()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dm = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        var vizier = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vizier Mor", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Unknown,
            PublicName = "Hooded stranger", PublicDescription = "Pays in old silver.\n:::dm\nThe vizier in disguise.\n:::",
            Biography = "Mor poisoned the old king.", DmNotes = "Weak to fire.", BlueprintId = "srd5e:mage", WikiPageId = "page-mor",
            HasPortrait = true, Sheet = new Dictionary<string, object> { ["level"] = 9 }, State = new Dictionary<string, object> { ["hp"] = 40 }
        };
        var nameless = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Sister Vel", Visibility = ActorVisibility.Unknown };
        var hidden = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Cult Leader", Visibility = ActorVisibility.Hidden };
        foreach (var actor in new[] { vizier, nameless, hidden }) await _actorRepository.SaveAsync(actor);

        var listed = (await _actorRepository.GetVisibleForCampaignAsync(player)).ToList();
        var byId = await _actorRepository.GetVisibleAsync(vizier.Id, player);
        Assert.DoesNotContain(listed, a => a.Id == hidden.Id);
        Assert.Null(await _actorRepository.GetVisibleAsync(hidden.Id, player));
        foreach (var mask in new[] { listed.Single(a => a.Id == vizier.Id), byId! })
        {
            Assert.Equal("Hooded stranger", mask.Name);
            Assert.Equal("Hooded stranger", mask.NameFor(isDm: false));
            Assert.Equal("Pays in old silver.", mask.PublicDescription);
            Assert.Equal(ActorVisibility.Unknown, mask.Visibility);
            Assert.False(mask.HasPortrait);
            Assert.Empty(mask.Sheet);
            Assert.Empty(mask.State);
            // Every field, as it would leave for the browser: nothing of who they really are. The sheet and state are checked by
            // their keys: a number would also turn up by chance in the ids, which are random hex.
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(mask);
            foreach (var secret in new[] { "Vizier", "Mor", "poisoned", "Weak to fire", "srd5e:mage", "page-mor", "disguise", "\"hp\"", "\"level\"" })
            {
                Assert.DoesNotContain(secret, json);
            }

            // A mask is never saved over the real actor, whoever asks.
            Assert.False(await _actorRepository.TrySaveAsync(mask, player));
            Assert.False(await _actorRepository.TrySaveAsync(mask, dm));
        }

        Assert.Equal(SessionQuotes.StrangerName, listed.Single(a => a.Id == nameless.Id).Name);
        Assert.NotEqual("Sister Vel", SessionQuotes.StrangerName);
        var whole = (await _actorRepository.GetVisibleAsync(vizier.Id, dm))!;
        Assert.Equal("Vizier Mor", whole.Name);
        Assert.Equal("Mor poisoned the old king.", whole.Biography);
    }

    [Fact]
    public async Task ABlockForACharacter_InAHistory_ReachesOnlyTheirPlayer_AndNeverTheTextTheyEdit()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dm = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var arias = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        var brams = new CampaignAccess(campaignId, "p2", CampaignRole.Player);
        var aria = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p1" };
        var bram = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Bram", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p2" };
        aria.Biography = $"Born at sea.\n:::for [[actor:{aria.Id}]]\nYou were adopted.\n:::\n:::dm\nHer father leads the cult.\n:::";
        var mara = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Mara", Kind = ActorKind.NonPlayerCharacter,
            Biography = $"Keeps the lighthouse.\n:::for [[actor:{aria.Id}]]\nAria knows her as her mother.\n:::"
        };
        foreach (var actor in new[] { aria, bram, mara }) await _actorRepository.SaveAsync(actor);

        // Someone else's history is read, never saved: the block for Aria is in it for Aria's player only.
        Assert.Contains("Aria knows her as her mother.", (await _actorRepository.GetVisibleAsync(mara.Id, arias))!.Biography);
        Assert.Equal("Keeps the lighthouse.", (await _actorRepository.GetVisibleAsync(mara.Id, brams))!.Biography);
        Assert.Contains("Aria knows her as her mother.", (await _actorRepository.GetVisibleForCampaignAsync(arias)).Single(a => a.Id == mara.Id).Biography);

        // Her own history she edits: the block comes apart, read only, and a save keeps it a block.
        var mine = (await _actorRepository.GetVisibleAsync(aria.Id, arias))!;
        Assert.Equal("Born at sea.", mine.Biography);
        Assert.Equal("You were adopted.", mine.RevealedToReader);
        Assert.Null((await _actorRepository.GetVisibleAsync(aria.Id, brams))!.RevealedToReader);
        mine.Biography = "Born at sea, raised by smugglers.";
        Assert.True(await _actorRepository.TrySaveAsync(mine, arias));

        var stored = (await _actorRepository.GetVisibleAsync(aria.Id, dm))!;
        Assert.Null(stored.RevealedToReader);
        Assert.StartsWith("Born at sea, raised by smugglers.", stored.Biography);
        Assert.Contains("You were adopted.", SecretText.SecretBlocks(stored.Biography));
        Assert.Contains("Her father leads the cult.", SecretText.SecretBlocks(stored.Biography));
        Assert.DoesNotContain("adopted", (await _actorRepository.GetVisibleAsync(aria.Id, brams))!.Biography);

        // Never stored, even when a document holding it is saved: it is the reader's, worked out each time.
        await _actorRepository.SaveAsync(new ActorDocument { Id = "actors/never-stored", CampaignId = campaignId, Name = "X", RevealedToReader = "a secret" });
        using var session = _dbService.Store.OpenAsyncSession();
        var raw = await session.Advanced.AsyncRawQuery<Dictionary<string, object>>("from ActorDocuments where id() = 'actors/never-stored'").SingleAsync();
        Assert.Equal("X", raw["Name"]?.ToString());
        Assert.False(raw.ContainsKey(nameof(ActorDocument.RevealedToReader)));
    }

    [Fact]
    public async Task Facts_KnowWhatTheyMention_StayInTheirCampaign_AndARevealToACharacterShowsIt()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dm = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var otherDm = new CampaignAccess(Guid.NewGuid().ToString(), "dm-2", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        var owned = new HashSet<string> { "aria" };
        var fact = new FactDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Summary = " The keeper's daughter ", Details = "[[actor:mara]] hides her in [[Saltmere Light]]; ask #[[place:p1]]." };
        Assert.True(await _factRepository.SaveAsync(fact, dm));

        async Task<FactDocument?> AsDm() => (await _factRepository.GetVisibleForCampaignAsync(dm, new HashSet<string>())).SingleOrDefault(f => f.Id == fact.Id);
        async Task<bool> PlayerSees() => (await _factRepository.GetVisibleForCampaignAsync(player, owned)).Any(f => f.Id == fact.Id);

        var saved = (await AsDm())!;
        Assert.Equal("The keeper's daughter", saved.Summary);
        Assert.Equal(new[] { "actor:mara", "place:p1", "wiki:saltmere-light" }, saved.RelatedEntityIds.OrderBy(k => k));
        Assert.Single(FactLinks.About(new[] { saved }, FactLinks.Page("saltmere-light")));
        Assert.Empty(FactLinks.About(new[] { saved }, FactLinks.Actor("aria"), null));

        // Another campaign's DM cannot take the id over.
        Assert.False(await _factRepository.SaveAsync(new FactDocument { Id = fact.Id, Summary = "Mine now" }, otherDm));
        Assert.Equal("The keeper's daughter", (await AsDm())!.Summary);

        // A DM-only fact revealed to a character is shown to that character's player, and taken back.
        Assert.False(await PlayerSees());
        Assert.True(await _factRepository.AddKnowerAsync(fact.Id, new KnowerEntry("aria", KnowledgeLevel.Rumor), dm));
        Assert.Equal(FactVisibility.KnowersOnly, (await AsDm())!.Visibility);
        Assert.True(await PlayerSees());
        Assert.False(await _factRepository.RemoveKnowerAsync(fact.Id, "aria", player));
        Assert.True(await _factRepository.RemoveKnowerAsync(fact.Id, "aria", dm));
        Assert.False(await PlayerSees());

        // Edited from a copy (the Lore tab's): the text only; who may read it is set apart, as the Lore tab does. Then deleted.
        var edit = new FactDocument { Id = fact.Id, Summary = "The keeper's son", Details = "No links now.", Visibility = FactVisibility.Public };
        Assert.True(await _factRepository.SaveAsync(edit, dm));
        Assert.Empty((await AsDm())!.RelatedEntityIds);
        Assert.False(await PlayerSees());
        Assert.True(await _factRepository.SetVisibilityAsync(fact.Id, FactVisibility.Public, dm));
        Assert.True(await PlayerSees());
        Assert.False(await _factRepository.DeleteAsync(fact.Id, player));
        Assert.False(await _factRepository.DeleteAsync(fact.Id, otherDm));
        Assert.True(await _factRepository.DeleteAsync(fact.Id, dm));
        Assert.Null(await AsDm());
    }

    [Fact]
    public async Task APlayersVersion_GoesBesideTheSecret_OnlyFromTheDm_AndNeverBesideAnEditedOne()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dm = new CampaignAccess(campaignId, "dm-user", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        var page = new WikiPageDocument { Title = "Saltmere", Visibility = WikiVisibility.Party, Body = "A fishing village.\n:::dm\nThe keeper runs the smugglers.\n:::" };
        Assert.True(await _wikiRepository.TrySaveAsync(page, dm));
        const string secret = "The keeper runs the smugglers.";

        Assert.Equal(SecretChange.Refused, await _wikiRepository.TrySetPlayersVersionAsync(page.Id, 0, secret, "Lights at night.", player));
        Assert.Equal(SecretChange.Stale, await _wikiRepository.TrySetPlayersVersionAsync(page.Id, 0, "An older secret.", "Lights at night.", dm));
        Assert.Equal(SecretChange.Done, await _wikiRepository.TrySetPlayersVersionAsync(page.Id, 0, secret, "Lights at night, says [[Old Tom]].", dm));
        Assert.Equal(SecretChange.Done, await _wikiRepository.TrySetPlayersVersionAsync(page.Id, 0, secret, "Boats come by night, says [[Old Tom]].", dm));

        var seen = (await _wikiRepository.GetVisibleBySlugAsync("saltmere", player))!;
        Assert.Equal("A fishing village.\n\nBoats come by night, says [[Old Tom]].", seen.Body);
        Assert.Equal(new[] { "old-tom" }, seen.Links);
        var whole = (await _wikiRepository.GetVisibleBySlugAsync("saltmere", dm))!.Body;
        Assert.Contains(secret, whole);
        Assert.Single(SecretText.Blocks(whole), b => b.Marked == SecretAudience.Players);
        // A players' version is for a DM block; one beside a players' block is refused.
        Assert.Equal(SecretChange.Refused, await _wikiRepository.TrySetPlayersVersionAsync(page.Id, 1, "Boats come by night, says [[Old Tom]].", "More.", dm));
    }

    [Fact]
    public async Task Journal_IsPrivateToItsOwner_EvenFromTheDm()
    {
        var campaignId = "camp-journal-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var p1 = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        var p2 = new CampaignAccess(campaignId, "p2", CampaignRole.Player);

        var entry = new JournalEntryDocument { Title = "Session one", Body = "The mayor is lying.", SessionId = "s1" };
        Assert.True(await _journalRepository.TrySaveAsync(entry, p1));

        Assert.Single(await _journalRepository.GetMineAsync(p1));
        Assert.Empty(await _journalRepository.GetMineAsync(p2));
        Assert.Empty(await _journalRepository.GetMineAsync(dm));

        entry.Body = "edited by someone else";
        Assert.False(await _journalRepository.TrySaveAsync(entry, p2));
        Assert.False(await _journalRepository.TryDeleteAsync(entry.Id, dm));
        Assert.True(await _journalRepository.TryDeleteAsync(entry.Id, p1));
        Assert.Empty(await _journalRepository.GetMineAsync(p1));
    }

    [Fact]
    public async Task DmNotes_AreStrippedForPlayers_AndPlayersCannotOverwriteThem()
    {
        var campaignId = "camp-dmnotes-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var owner = new CampaignAccess(campaignId, "p1", CampaignRole.Player);
        var actor = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p1", Biography = "Guard.", DmNotes = "Secretly cursed." };
        await _actorRepository.SaveAsync(actor);

        var seenByOwner = await _actorRepository.GetVisibleAsync(actor.Id, owner);
        Assert.NotNull(seenByOwner);
        Assert.Null(seenByOwner!.DmNotes);
        Assert.Equal("Guard.", seenByOwner.Biography);
        Assert.All(await _actorRepository.GetVisibleForCampaignAsync(owner), a => Assert.Null(a.DmNotes));

        seenByOwner.Biography = "Guard, now a cleric.";
        seenByOwner.DmNotes = "I wrote this as a player";
        Assert.True(await _actorRepository.TrySaveAsync(seenByOwner, owner));

        var seenByDm = await _actorRepository.GetVisibleAsync(actor.Id, dm);
        Assert.Equal("Secretly cursed.", seenByDm!.DmNotes);
        Assert.Equal("Guard, now a cleric.", seenByDm.Biography);
    }

    [Fact]
    public async Task MediaStore_RoundTripsAnImage_AndRejectsSvg()
    {
        var campaignId = "camp-media-" + Guid.NewGuid();
        await _campaignRepository.SaveAsync(new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Art", SystemId = "srd5e" });

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        await _mediaStore.PutAsync(campaignId, RavenMediaStore.CampaignCover, new MemoryStream(png), "image/png");
        var stored = await _mediaStore.GetAsync(campaignId, RavenMediaStore.CampaignCover);
        Assert.NotNull(stored);
        Assert.Equal(png, stored!.Bytes);
        Assert.Equal("image/png", stored.ContentType);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _mediaStore.PutAsync(campaignId, RavenMediaStore.CampaignCover, new MemoryStream(png), "image/svg+xml"));

        await _mediaStore.DeleteAsync(campaignId, RavenMediaStore.CampaignCover);
        Assert.Null(await _mediaStore.GetAsync(campaignId, RavenMediaStore.CampaignCover));
    }

    [Fact]
    public async Task DeletingACampaign_RemovesItsWikiAndJournals()
    {
        var campaignId = "camp-cascade2-" + Guid.NewGuid();
        await _campaignRepository.SaveAsync(new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Gone", SystemId = "srd5e" });
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        await _wikiRepository.TrySaveAsync(new WikiPageDocument { Title = "Page" }, dm);
        await _journalRepository.TrySaveAsync(new JournalEntryDocument { Title = "Mine" }, dm);

        Assert.Equal(CampaignDeleteResult.Deleted, await _campaignRepository.DeleteAsync(campaignId, "owner"));
        Assert.Empty(await _wikiRepository.GetAllForCampaignAsync(campaignId));
        Assert.Empty(await _journalRepository.GetMineAsync(dm));
    }

    [Fact]
    public async Task WorldChats_ArePrivateToTheirOwner_AndComeBackWithTheirProposalTicksAndSteps()
    {
        var campaignId = "camp-worldchat-" + Guid.NewGuid();
        var dm = new CampaignAccess(campaignId, "dm", CampaignRole.DM);
        var coDm = new CampaignAccess(campaignId, "dm2", CampaignRole.DM);
        var proposal = new Codex.Core.WorldBuilder.WorldBundle { Summary = "A town.", Region = "The Coast" };
        proposal.Drafts.Add(new Codex.Core.WorldBuilder.WorldDraft
        {
            Ref = "n", Kind = "npc", Name = "Ilse", OutlineName = "Ilse", Cast = true, Selected = false,
            Stats = new Codex.Core.WorldBuilder.CreatureRef("monster", "veteran", false, "Veteran", "Ветеран"),
            SavedAs = { ["page"] = "pages/1" }
        });
        proposal.Drafts.Add(new Codex.Core.WorldBuilder.WorldDraft
        {
            Ref = "h", Kind = "homebrew", Type = "spell", Name = "Brine Ward", Tags = { "salt" },
            Properties = { ["level"] = 2L, ["components"] = new List<object> { "V", "S" }, ["ritual"] = true }
        });
        var chat = new WorldChatDocument
        {
            Title = "Salt",
            Proposal = proposal,
            ProposalInput = "{\"entries\": []}",
            Decided = { "n" },
            History =
            {
                new WorldChatMessage { Role = "user", Text = "A town." },
                new WorldChatMessage { Role = "assistant", Calls = new() { new WorldChatCall { Id = "c1", Name = "list_places", Arguments = "{}" } } },
                new WorldChatMessage { Role = "tool", Results = new() { new WorldChatResult { CallId = "c1", Result = "Region: The Coast" } } }
            },
            Lines =
            {
                new WorldChatLine(WorldChatSpeaker.Dm, "A town."),
                new WorldChatLine(WorldChatSpeaker.Model, "Here.", new List<WorldChatStep> { new() { Tool = "list_places", Arguments = "{}", Result = "Region: The Coast", Status = WorldChatStepStatus.Done, DurationMs = 12 } })
                {
                    Usage = new WorldChatUsage { Model = "m", InputTokens = 100, OutputTokens = 20, Rounds = 2, DurationMs = 900 }
                }
            },
            Waiting = 1
        };

        Assert.Equal(ChatSave.Saved, await _worldChatRepository.TrySaveAsync(chat, dm));
        Assert.StartsWith("worldchats/", chat.Id);

        var listed = Assert.Single(await _worldChatRepository.ListMineAsync(dm));
        Assert.Equal((chat.Id, "Salt", 1), (listed.Id, listed.Title, listed.Waiting));
        // Another DM of the same campaign neither sees nor changes it.
        Assert.Empty(await _worldChatRepository.ListMineAsync(coDm));
        Assert.Null(await _worldChatRepository.GetMineAsync(chat.Id, coDm));
        Assert.Equal(ChatSave.Refused, await _worldChatRepository.TrySaveAsync(chat, coDm));
        Assert.False(await _worldChatRepository.TryDeleteAsync(chat.Id, coDm));

        var loaded = await _worldChatRepository.GetMineAsync(chat.Id, dm);
        Assert.NotNull(loaded);
        var ilse = loaded!.Proposal!.Find("n")!;
        Assert.Equal((false, true, "pages/1"), (ilse.Selected, ilse.Cast, ilse.SavedAs["page"]));
        Assert.Equal(new Codex.Core.WorldBuilder.CreatureRef("monster", "veteran", false, "Veteran", "Ветеран"), ilse.Stats);
        var ward = loaded.Proposal.Find("h")!;
        Assert.Equal(2L, Convert.ToInt64(ward.Properties["level"]));
        Assert.Equal(new[] { "V", "S" }, ((System.Collections.IEnumerable)ward.Properties["components"]).Cast<object>().Select(o => o.ToString()));
        Assert.Equal(true, ward.Properties["ritual"]);
        Assert.Equal(new[] { "n" }, loaded.Decided);
        Assert.Equal("c1", loaded.History[1].Calls![0].Id);
        Assert.Equal("Region: The Coast", loaded.History[2].Results![0].Result);
        var step = Assert.Single(loaded.Lines[1].Steps!);
        Assert.Equal((WorldChatStepStatus.Done, 12L), (step.Status, step.DurationMs));
        Assert.Equal((100L, 20L, 2), (loaded.Lines[1].Usage!.InputTokens, loaded.Lines[1].Usage!.OutputTokens, loaded.Lines[1].Usage!.Rounds));

        loaded.Title = "Salt and bells";
        Assert.Equal(ChatSave.Saved, await _worldChatRepository.TrySaveAsync(loaded, dm));
        Assert.Equal("Salt and bells", (await _worldChatRepository.GetMineAsync(chat.Id, dm))!.Title);
        Assert.True(await _worldChatRepository.TryDeleteAsync(chat.Id, dm));
        Assert.Empty(await _worldChatRepository.ListMineAsync(dm));
    }

    [Fact]
    public async Task WorldChat_TwoCopiesOfOneChat_TheSecondSaveIsRefused_AndADeletedChatStaysDeleted()
    {
        var dm = new CampaignAccess("camp-chat-race-" + Guid.NewGuid(), "dm", CampaignRole.DM);
        var chat = new WorldChatDocument { Title = "Salt" };
        Assert.Equal(ChatSave.Saved, await _worldChatRepository.TrySaveAsync(chat, dm));
        var tabA = (await _worldChatRepository.GetMineAsync(chat.Id, dm))!;
        var tabB = (await _worldChatRepository.GetMineAsync(chat.Id, dm))!;

        tabA.Title = "From A";
        Assert.Equal(ChatSave.Saved, await _worldChatRepository.TrySaveAsync(tabA, dm));
        tabA.Title = "From A again"; // the copy that saved last stays current
        Assert.Equal(ChatSave.Saved, await _worldChatRepository.TrySaveAsync(tabA, dm));
        tabB.Title = "From B";
        Assert.Equal(ChatSave.Changed, await _worldChatRepository.TrySaveAsync(tabB, dm));
        Assert.Equal("From A again", (await _worldChatRepository.GetMineAsync(chat.Id, dm))!.Title);

        Assert.True(await _worldChatRepository.TryDeleteAsync(chat.Id, dm));
        Assert.Equal(ChatSave.Gone, await _worldChatRepository.TrySaveAsync(tabA, dm));
        Assert.Null(await _worldChatRepository.GetMineAsync(chat.Id, dm));
    }

    [Fact]
    public async Task DeletingACampaign_RemovesItsWorldChats()
    {
        var campaignId = "camp-cascade3-" + Guid.NewGuid();
        await _campaignRepository.SaveAsync(new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Gone", SystemId = "srd5e" });
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        await _worldChatRepository.TrySaveAsync(new WorldChatDocument { Title = "Mine" }, dm);

        Assert.Equal(CampaignDeleteResult.Deleted, await _campaignRepository.DeleteAsync(campaignId, "owner"));
        Assert.Empty(await _worldChatRepository.ListMineAsync(dm));
    }
}
