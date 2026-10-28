using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using NSubstitute;

namespace Codex.Tests;

/// <summary>"Export pack" output can be imported back into a campaign of the same system, as drafts.</summary>
public class HomebrewPackImportTests
{
    private static readonly CampaignDocument Campaign = new() { Id = "c1", SystemId = "demo" };
    private static readonly CampaignAccess Dm = new("c1", "u1", CampaignRole.DM);

    private static (HomebrewImportService Service, List<HomebrewEntryDocument> Saved) Build(params HomebrewEntryDocument[] existing)
    {
        var saved = new List<HomebrewEntryDocument>();
        var repo = Substitute.For<IHomebrewRepository>();
        repo.GetAllForCampaignAsync("c1").Returns(_ => Task.FromResult<IEnumerable<HomebrewEntryDocument>>(existing.Concat(saved).ToList()));
        repo.TrySaveAsync(Arg.Any<HomebrewEntryDocument>(), Arg.Any<CampaignAccess>()).Returns(call =>
        {
            saved.Add(call.Arg<HomebrewEntryDocument>());
            return Task.FromResult(true);
        });
        return (new HomebrewImportService(repo, Substitute.For<ICampaignRepository>(), Substitute.For<ICampaignAccessResolver>()), saved);
    }

    private static async Task<MemoryStream> PackAsync(string systemId)
    {
        var entry = new ContentEntry
        {
            Id = "flame-blade",
            Type = "spell",
            Name = "Flame Blade",
            Description = "A blade of fire.",
            Tags = new List<string> { "fire" },
            Properties = new Dictionary<string, object> { ["level"] = 2L, ["nested"] = new Dictionary<string, object> { ["k"] = "v" } }
        };
        var manifest = new PackManifest { Id = "homebrew-x", Name = "X homebrew", SystemId = systemId, Kind = "homebrew" };
        var bytes = await new ContentPackExporter().ExportToZipBytesAsync(manifest, new[] { entry });
        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task ExportedPackImportsAsDrafts()
    {
        var (service, saved) = Build();
        var result = await service.ImportPackAsync(await PackAsync("demo"), Campaign, Dm);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Imported);
        var doc = Assert.Single(saved);
        Assert.Equal("flame-blade", doc.EntryId);
        Assert.Equal("spell", doc.Type);
        Assert.False(doc.IsPublished);
        Assert.Contains("fire", doc.Tags);
        Assert.IsType<Dictionary<string, object>>(doc.Properties["nested"]);
    }

    [Fact]
    public async Task APackOfSomeTags_CarriesOnlyThoseEntries_WithTheirTagsAndFields()
    {
        // What Library's "Export by tag" does: pick the tagged homebrew of one campaign, export it, import it in another.
        var source = new[]
        {
            new HomebrewEntryDocument { CampaignId = "c0", Type = "spell", EntryId = "brine-ward", Name = "Brine Ward", Tags = { "Grey Coast", "salt" }, Properties = { ["level"] = 1L, ["ritual"] = true } },
            new HomebrewEntryDocument { CampaignId = "c0", Type = "item", EntryId = "gull-feather", Name = "Gull Feather", Tags = { "grey coast" } },
            new HomebrewEntryDocument { CampaignId = "c0", Type = "spell", EntryId = "rimeburst", Name = "Rimeburst", Tags = { "cold" } }
        };
        var manifest = new PackManifest { Id = "homebrew-grey-coast", Name = "Grey homebrew (grey coast)", SystemId = "demo", Kind = "homebrew" };
        var bytes = await new ContentPackExporter().ExportToZipBytesAsync(manifest, HomebrewTags.Tagged(source, new[] { "grey coast" }).Select(h => h.ToEntry()));

        var (service, saved) = Build();
        var result = await service.ImportPackAsync(new MemoryStream(bytes), Campaign, Dm);

        Assert.Equal(2, result.Imported);
        Assert.Equal(new[] { "brine-ward", "gull-feather" }, saved.Select(d => d.EntryId).Order());
        var ward = saved.Single(d => d.EntryId == "brine-ward");
        Assert.Equal(new[] { "Grey Coast", "salt" }, ward.Tags);
        Assert.Equal(1L, Convert.ToInt64(ward.Properties["level"]));
        Assert.True(Convert.ToBoolean(ward.Properties["ritual"]));
        Assert.All(saved, d => Assert.Equal("c1", d.CampaignId));
        Assert.Equal("item", saved.Single(d => d.EntryId == "gull-feather").Type);
    }

    [Fact]
    public async Task ExistingEntriesAreSkipped()
    {
        var (service, saved) = Build(new HomebrewEntryDocument { Type = "spell", EntryId = "flame-blade" });
        var result = await service.ImportPackAsync(await PackAsync("demo"), Campaign, Dm);

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task PackForAnotherSystemIsRejected()
    {
        var (service, saved) = Build();
        var result = await service.ImportPackAsync(await PackAsync("other"), Campaign, Dm);

        Assert.Equal(PackImportError.WrongSystem, result.Error);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task NonPackAndNonDmAreRejected()
    {
        var (service, _) = Build();
        var junk = await service.ImportPackAsync(new MemoryStream(new byte[] { 1, 2, 3 }), Campaign, Dm);
        Assert.Equal(PackImportError.NotAPack, junk.Error);

        var player = await service.ImportPackAsync(await PackAsync("demo"), Campaign, new CampaignAccess("c1", "u2", CampaignRole.Player));
        Assert.Equal(PackImportError.NotAllowed, player.Error);
    }
}
