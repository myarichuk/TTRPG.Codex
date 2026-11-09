using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Versioning (overhaul section 4, rule 8, step 10): a campaign records the system it plays under, version and file hashes;
/// a change to the loaded system is listed against that record until the GM accepts it.
/// </summary>
[Collection("RavenDb")]
public sealed class SystemStampTests(RavenDbFixture fixture) : IClassFixture<RavenDbFixture>
{
    /// <summary>A loaded catalog whose systems' fingerprints a test changes, as a reload would.</summary>
    private sealed class Catalog : ISystemCatalog
    {
        public Dictionary<string, SystemFingerprint> Fingerprints { get; } = new();
        public bool IsLoaded => true;
        public IReadOnlySet<string> LoadedSystemIds => Fingerprints.Keys.ToHashSet();
        public IEnumerable<SystemSummary> Systems => Enumerable.Empty<SystemSummary>();
        public string GetDisplayName(string systemId) => systemId;
        public SystemDefinition? GetDefinition(string systemId) => null;
        public SystemFingerprint? GetFingerprint(string systemId) => Fingerprints.GetValueOrDefault(systemId);
        public IEnumerable<UISchema> GetUISchemas(string systemId) => Enumerable.Empty<UISchema>();
    }

    private static SystemFingerprint Print(string version, string yaml, string? script = null)
    {
        var files = new Dictionary<string, string> { ["system.yaml"] = yaml };
        if (script != null) files["rules.js"] = script;
        return new SystemFingerprint(version, files);
    }

    private RavenDbService Database() => new(fixture.DbPath, "stamps-" + Guid.NewGuid().ToString("N")[..8], runInMemory: true);

    private static CampaignRepository Campaigns(RavenDbService db, ISystemCatalog catalog) =>
        new(db, catalog, null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task ACampaign_RecordsItsSystem_WhenMadeAndWhenSwitched_AndAChangeIsListedUntilAccepted()
    {
        using var db = Database();
        var catalog = new Catalog();
        catalog.Fingerprints["street"] = Print("1.0.0", "a", "b");
        catalog.Fingerprints["crews"] = Print("2.0.0", "c");
        var campaigns = Campaigns(db, catalog);
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "gm", Name = "Night City", SystemId = "street" };

        await campaigns.SaveAsync(campaign);
        var stored = (await campaigns.GetAsync(campaign.Id))!;
        Assert.Equal("street", stored.SystemStamp!.SystemId);
        Assert.Equal("1.0.0", stored.SystemStamp.Version);
        Assert.Empty(stored.SystemStamp.ChangesTo("street", catalog.GetFingerprint("street")));

        // An edit that is not a switch keeps the record: a change since then is still a change.
        catalog.Fingerprints["street"] = Print("1.1.0", "a", "B");
        await campaigns.UpdateAsync(campaign.Id, c => c.Name = "Night City 2077");
        stored = (await campaigns.GetAsync(campaign.Id))!;
        Assert.Equal(new[]
        {
            new SystemChange(SystemChangeKind.Version, null, "1.0.0", "1.1.0"),
            new SystemChange(SystemChangeKind.Changed, "rules.js", "b", "B"),
        }, stored.SystemStamp!.ChangesTo("street", catalog.GetFingerprint("street")));

        var dm = new CampaignAccess(campaign.Id, "gm", CampaignRole.DM, false);
        await campaigns.AcceptSystemAsync(dm);
        stored = (await campaigns.GetAsync(campaign.Id))!;
        Assert.Equal("1.1.0", stored.SystemStamp!.Version);
        Assert.Empty(stored.SystemStamp.ChangesTo("street", catalog.GetFingerprint("street")));

        await campaigns.UpdateAsync(campaign.Id, c => c.SystemId = "crews");
        stored = (await campaigns.GetAsync(campaign.Id))!;
        Assert.Equal("crews", stored.SystemStamp!.SystemId);
        Assert.Equal("2.0.0", stored.SystemStamp.Version);
        // A record of another system says nothing about this one.
        Assert.Empty(stored.SystemStamp.ChangesTo("street", catalog.GetFingerprint("street")));
    }

    [Fact]
    public async Task OnlyTheGm_AcceptsTheSystemsChanges()
    {
        using var db = Database();
        var catalog = new Catalog();
        catalog.Fingerprints["street"] = Print("1.0.0", "a");
        var campaigns = Campaigns(db, catalog);
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "gm", Name = "Night City", SystemId = "street" };
        campaign.Members.Add(new CampaignMember { UserId = "player", Role = CampaignRole.Player });
        await campaigns.SaveAsync(campaign);
        catalog.Fingerprints["street"] = Print("1.1.0", "a");

        await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() =>
            campaigns.AcceptSystemAsync(new CampaignAccess(campaign.Id, "player", CampaignRole.DM, false)));

        Assert.Equal("1.0.0", (await campaigns.GetAsync(campaign.Id))!.SystemStamp!.Version);
    }

    [Fact]
    public void TheUpgrade_StampsCampaignsStoredBeforeStamps_AndWaitsForASystemThatIsNotLoaded()
    {
        using var db = Database();
        var root = Path.Combine(Path.GetTempPath(), "codex-stamps-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "system.yaml"), "id: street\nname: Street\nversion: 3.1.0\n");
            var street = SystemDefinitionLoader.Load(Path.Combine(root, "system.yaml"));
            using (var session = db.Store.OpenSession())
            {
                session.Store(new CampaignDocument { Id = "campaigns/old", OwnerId = "gm", Name = "Old", SystemId = "street" });
                session.Store(new CampaignDocument { Id = "campaigns/elsewhere", OwnerId = "gm", Name = "Elsewhere", SystemId = "gone" });
                session.SaveChanges();
            }

            var log = DatabaseUpgrades.Run(db.Store, id => id == "street" ? street : null);

            Assert.Contains(log, l => l.Contains("1 campaign(s)"));
            Assert.Contains(log, l => l.Contains("Elsewhere") && l.Contains("gone"));
            using (var session = db.Store.OpenSession())
            {
                var old = session.Load<CampaignDocument>("campaigns/old");
                Assert.Equal("3.1.0", old.SystemStamp!.Version);
                Assert.Equal(street.Fingerprint!.Files["system.yaml"], old.SystemStamp.Files["system.yaml"]);
                Assert.Null(session.Load<CampaignDocument>("campaigns/elsewhere").SystemStamp);
                Assert.DoesNotContain(DatabaseUpgrades.SystemStamps, session.Load<DatabaseUpgrades.Ledger>(DatabaseUpgrades.LedgerId)!.Applied);
            }

            // Once the system is there, the next start finishes the upgrade.
            DatabaseUpgrades.Run(db.Store, id => street);
            using (var session = db.Store.OpenSession())
            {
                Assert.NotNull(session.Load<CampaignDocument>("campaigns/elsewhere").SystemStamp);
                Assert.Contains(DatabaseUpgrades.SystemStamps, session.Load<DatabaseUpgrades.Ledger>(DatabaseUpgrades.LedgerId)!.Applied);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
