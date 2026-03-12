using Codex.Persistence;
using System.Text.Json.Nodes;

namespace Codex.Tests;

public class PersistenceTest : IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly RavenCampaignRepository _campaignRepository;

    public PersistenceTest()
    {
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX", "2");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_PRE_RELEASE", "1");

        _dbService = new RavenDbService("TestRavenData_" + Guid.NewGuid());
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
    }
}
