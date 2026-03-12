using Codex.Persistence;
using System.Text.Json.Nodes;

namespace Codex.Tests;

public class PersistenceTest : IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly RavenCampaignRepository _campaignRepository;
    private readonly string _testDbPath;

    public PersistenceTest()
    {
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX", "2");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_PRE_RELEASE", "1");

        _testDbPath = Path.Combine(Path.GetTempPath(), "TestRavenData_" + Guid.NewGuid());
        _dbService = new RavenDbService(_testDbPath);
        _campaignRepository = new RavenCampaignRepository(_dbService);
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

    public void Dispose()
    {
        _dbService.Dispose();

        try
        {
            if (Directory.Exists(_testDbPath))
            {
                Directory.Delete(_testDbPath, true);
            }
        }
        catch { }
    }
}
