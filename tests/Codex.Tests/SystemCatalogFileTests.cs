using System.Security.Cryptography;
using Codex.Core.Systems;

namespace Codex.Tests;

/// <summary>The line a packed system gets in a release's systems.json, and reading the file back.</summary>
public sealed class SystemCatalogFileTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "codex-catalog-" + Guid.NewGuid().ToString("N")[..8]);

    public SystemCatalogFileTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void APackedSystem_GetsAnEntryWithItsFileSizeAndHash()
    {
        var folder = Path.Combine(_scratch, "street");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "system.yaml"),
            "id: street\nname: Street rules\nversion: 2.1.0\ncompatibility: Street compatible\ndescription: >-\n  Fast and dirty.\nlicense:\n  name: CC-BY-4.0\nentityTypes:\n  - { type: calling, label: Calling, folder: callings }\n");
        var archive = Path.Combine(_scratch, ".street.zip");
        using (var output = File.Create(archive)) Assert.Empty(SystemArchive.Write(folder, output));

        var definition = SystemDefinitionLoader.Load(Path.Combine(folder, "system.yaml"));
        var entry = SystemCatalogFile.EntryFor(definition, archive, scripts: false);

        Assert.Equal(("street", "Street rules", "2.1.0", "Fast and dirty.", "CC-BY-4.0", "Street compatible", false), (entry.Id, entry.Name, entry.Version, entry.Description, entry.License, entry.Compatibility, entry.Scripts));
        Assert.Equal("system-street-2.1.0.zip", entry.File);
        Assert.Equal(new FileInfo(archive).Length, entry.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant(), entry.Sha256);
    }

    [Fact]
    public void TheCatalog_RoundTrips_AndNoSystemsIsAnEmptyList()
    {
        var entry = new SystemCatalogEntry("a", "A", "1.0.0", null, null, null, true, "system-a-1.0.0.zip", 10, new string('a', 64));

        var read = SystemCatalogFile.TryRead(SystemCatalogFile.Write(new[] { entry }));

        Assert.Equal(1, read!.Format);
        Assert.Equal(entry, Assert.Single(read.Systems));
        Assert.Empty(SystemCatalogFile.TryRead(SystemCatalogFile.Write(Array.Empty<SystemCatalogEntry>()))!.Systems);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\":2,\"systems\":[]}")]
    [InlineData("{\"systems\":[]}")]
    [InlineData("[]")]
    public void ACatalogThisBuildCannotRead_IsNull(string json) => Assert.Null(SystemCatalogFile.TryRead(json));
}
