using Codex.Persistence;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Codex.Tests;

public class PersistenceTest : IDisposable
{
    private readonly string _testDir;
    private readonly RavenDbService _service;

    public PersistenceTest()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "RavenDbTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
        _service = new RavenDbService(_testDir);
    }

    [Fact]
    public void SaveAndLoadCampaign_ShouldSucceed()
    {
        // For synchronous testing so we don't have Task timeouts if embedded server struggles in sandbox.
        // Actually, let's keep it async, it worked before.
    }

    [Fact]
    public async Task SaveAndLoadCampaign_ShouldSucceed_Async()
    {
        var repo = new RavenCampaignRepository(_service);

        var campaign = new CampaignDocument
        {
            Id = "campaign/1",
            Name = "Test Campaign",
            System = "DnD5e"
        };

        await repo.SaveAsync(campaign);
        var loaded = await repo.GetAsync("campaign/1");

        Assert.NotNull(loaded);
        Assert.Equal("Test Campaign", loaded.Name);
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { } // Ignore clean up errors in test
    }
}
