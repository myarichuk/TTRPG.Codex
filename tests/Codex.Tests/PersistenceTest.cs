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

public class PersistenceTest : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly CampaignRepository _campaignRepository;
    private readonly CharacterRepository _characterRepository;
    private readonly RavenUserRepository _userRepository;
    private readonly RavenSessionRepository _sessionRepository;

    public PersistenceTest(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _campaignRepository = new CampaignRepository(_dbService);
        _characterRepository = new CharacterRepository(_dbService);
        _userRepository = new RavenUserRepository(_dbService);
        _sessionRepository = new RavenSessionRepository(_dbService);
    }

    [Fact]
    public async Task SaveAndLoadCampaign_ShouldSucceed_Async()
    {
        var campaign = new CampaignDocument
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Async Campaign",
            System = "DnD5e"
        };

        await _campaignRepository.SaveAsync(campaign);
        var loaded = await _campaignRepository.GetAsync(campaign.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Async Campaign", loaded.Name);
    }

    [Fact]
    public async Task SaveAndLoadCharacter_ShouldSucceed_Async()
    {
        var character = new CharacterDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = Guid.NewGuid().ToString(),
            Name = "Test Character"
        };

        character.State["HP"] = 10;

        await _characterRepository.SaveAsync(character);
        var loaded = await _characterRepository.GetAsync(character.Id);

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
    public async Task GetAllCharactersForCampaign_ShouldReturnCorrect_Async()
    {
        var campaignId = Guid.NewGuid().ToString();

        var char1 = new CharacterDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Char 1" };
        var char2 = new CharacterDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Char 2" };
        var char3 = new CharacterDocument { Id = Guid.NewGuid().ToString(), CampaignId = Guid.NewGuid().ToString(), Name = "Other Campaign Char" };

        await _characterRepository.SaveAsync(char1);
        await _characterRepository.SaveAsync(char2);
        await _characterRepository.SaveAsync(char3);

        // Wait for RavenDB indexes to process (since we use a query)
        using var session = _dbService.Store.OpenAsyncSession();
        await session.Query<CharacterDocument>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();

        var loaded = await _characterRepository.GetAllForCampaignAsync(campaignId);

        Assert.NotNull(loaded);
        var list = loaded.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, c => c.Name == "Char 1");
        Assert.Contains(list, c => c.Name == "Char 2");
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

    public void Dispose()
    {
        _dbService.Dispose();
    }
}
