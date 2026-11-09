using System.IO.Compression;
using System.Security.Claims;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>
/// Installing systems from the app (overhaul step 10, section 4 rule 7): a system zip is unpacked safely and checked as the
/// server would load it; never compiled code; the app replaces and removes only what it installed; rights are the admin's.
/// </summary>
public sealed class SystemInstallTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "codex-install-" + Guid.NewGuid().ToString("N")[..8]);

    public SystemInstallTests() => Directory.CreateDirectory(_scratch);

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

    private static string System(string id, string version = "1.0.0", string extra = "") =>
        $"id: {id}\nname: {id} rules\nversion: {version}\nentityTypes:\n  - {{ type: calling, label: Calling, folder: callings }}\n{extra}";

    private static MemoryStream Zip(params (string Name, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static (string, string)[] Street(string version = "1.0.0", string folder = "street/") => new[]
    {
        ($"{folder}system.yaml", System("street", version)),
        ($"{folder}packs/core/manifest.yaml", "id: street-core\nname: Street core\nversion: 1.0.0\nsystemId: street\n"),
        ($"{folder}packs/core/callings/callings.yaml", "- id: fixer\n  name: Fixer\n"),
    };

    private string Staging() => Path.Combine(_scratch, "staging-" + Guid.NewGuid().ToString("N")[..6]);

    // ---- The archive ----

    [Fact]
    public async Task ASystemZip_IsUnpackedWithoutItsFolder_AndCheckedAsTheServerLoadsIt()
    {
        var check = await SystemArchive.CheckAsync(Zip(Street()), Staging(), scriptsAllowed: true);

        Assert.True(check.CanInstall, string.Join(" ", check.Errors));
        Assert.Equal(("street", "street rules", "1.0.0", 1), (check.Id, check.Name, check.Version, check.Packs));
        Assert.True(File.Exists(Path.Combine(check.Folder, "system.yaml")));
        Assert.True(File.Exists(Path.Combine(check.Folder, "packs", "core", "manifest.yaml")));
        Assert.Contains("system.yaml", check.Fingerprint!.Files.Keys);
    }

    [Theory]
    [InlineData("../evil.yaml", "outside the system's folder")]
    [InlineData("/etc/evil.yaml", "outside the system's folder")]
    [InlineData("C:/evil.yaml", "outside the system's folder")]
    [InlineData("Street.dll", "compiled code")]
    [InlineData("tools/run.sh", "compiled code")]
    public async Task AnEntryThatEscapesOrIsCode_RefusesTheArchive(string entry, string why)
    {
        var check = await SystemArchive.CheckAsync(Zip(("system.yaml", System("street")), (entry, "x")), Staging(), scriptsAllowed: true);

        Assert.False(check.CanInstall);
        Assert.Contains(check.Errors, e => e.Contains(why));
        Assert.False(File.Exists(Path.Combine(_scratch, "evil.yaml")));
    }

    [Fact]
    public async Task ALink_RefusesTheArchive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("system.yaml").Open())) writer.Write(System("street"));
            var link = archive.CreateEntry("rules.js");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using (var writer = new StreamWriter(link.Open())) writer.Write("/etc/passwd");
        }

        stream.Position = 0;
        var check = await SystemArchive.CheckAsync(stream, Staging(), scriptsAllowed: true);

        Assert.Contains(check.Errors, e => e.Contains("is a link"));
    }

    [Fact]
    public async Task WhatIsNotOneLoadableSystem_IsRefused_SayingWhy()
    {
        async Task<IReadOnlyList<string>> Errors(MemoryStream zip) => (await SystemArchive.CheckAsync(zip, Staging(), scriptsAllowed: true)).Errors;

        Assert.Contains(await Errors(new MemoryStream(new byte[] { 1, 2, 3 })), e => e.Contains("not a zip"));
        Assert.Contains(await Errors(Zip(("readme.md", "hello"))), e => e.Contains("no system.yaml"));
        Assert.Contains(await Errors(Zip(("system.yaml", System("a")), ("b/system.yaml", System("b")))), e => e.Contains("more than one system"));
        Assert.Contains(await Errors(Zip(("system.yaml", "id: broken\nname: Broken\nattributes:\n  - key: a\n  - key: a\n"))), e => e.Contains("declared more than once"));
        Assert.Contains(await Errors(Zip(("system.yaml", System("street")), ("packs/x/manifest.yaml", "id: x\nname: X\nversion: 1.0.0\nsystemId: other\n"))),
            e => e.Contains("for system 'other'"));
    }

    [Fact]
    public async Task Scripts_WhenTheServerHasThemOff_RefuseTheArchive()
    {
        var zip = new[] { ("system.yaml", System("street", extra: "functions:\n  twice: { script: rules.js#twice }\n")), ("rules.js", "export function twice(x) { return 2 * x; }\n") };

        var on = await SystemArchive.CheckAsync(Zip(zip), Staging(), scriptsAllowed: true);
        var off = await SystemArchive.CheckAsync(Zip(zip), Staging(), scriptsAllowed: false);

        Assert.True(on.CanInstall);
        Assert.True(on.NamesScripts);
        Assert.Contains(off.Errors, e => e.Contains("rules.js") && e.Contains("scripts turned off"));
    }

    [Fact]
    public async Task AFileThatUnpacksTooBig_StopsTheUnpacking()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("system.yaml").Open())) writer.Write(System("street"));
            using var big = archive.CreateEntry("packs/big.yaml", CompressionLevel.Optimal).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i <= SystemArchive.MaxEntryBytes / zeros.Length; i++) big.Write(zeros);
        }

        stream.Position = 0;
        var check = await SystemArchive.CheckAsync(stream, Staging(), scriptsAllowed: true);

        Assert.Contains(check.Errors, e => e.Contains("MB"));
    }

    [Fact]
    public async Task Write_MakesTheArchiveTheInstallTakes_AndRefusesCompiledCode()
    {
        var folder = Path.Combine(_scratch, "street");
        foreach (var (name, text) in Street(folder: ""))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, name))!);
            File.WriteAllText(Path.Combine(folder, name), text);
        }

        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), "ref");
        using var zip = new MemoryStream();
        Assert.Empty(SystemArchive.Write(folder, zip));
        zip.Position = 0;
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(new[] { "packs/core/callings/callings.yaml", "packs/core/manifest.yaml", "system.yaml" }, archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        zip.Position = 0;
        Assert.True((await SystemArchive.CheckAsync(zip, Staging(), scriptsAllowed: true)).CanInstall);

        File.WriteAllBytes(Path.Combine(folder, "Street.dll"), new byte[] { 1 });
        Assert.Contains("Street.dll", Assert.Single(SystemArchive.Write(folder, new MemoryStream())));
    }

    // ---- The installer ----

    private static ClaimsPrincipal User(string id, params string[] roles) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, id) }.Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))), "test"));

    private static readonly ClaimsPrincipal Admin = User("admin", UserRoles.ServerAdmin);

    private async Task<(SystemInstaller Installer, PluginLoader Loader, string Shipped)> Server(Dictionary<string, string?>? settings = null, params CampaignDocument[] campaigns)
    {
        var shipped = Path.Combine(_scratch, "content");
        Directory.CreateDirectory(Path.Combine(shipped, "srd"));
        File.WriteAllText(Path.Combine(shipped, "srd", "system.yaml"), System("srd"));
        var repository = Substitute.For<ICampaignRepository>();
        repository.GetAllAsync().Returns(campaigns);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
        var installer = new SystemInstaller(loader, repository, configuration, NullLogger<SystemInstaller>.Instance, Path.Combine(_scratch, "Systems"));
        installer.Prepare();
        await loader.LoadAndInitializeAsync(new[] { shipped, installer.Root });
        return (installer, loader, shipped);
    }

    [Fact]
    public async Task OnlyTheAdmin_InstallsAndReloads_UnlessGmInstallIsOn_AndNeverWhileOpenToTheInternet()
    {
        var gm = User("gm");
        var (closed, _, _) = await Server();
        Assert.True(closed.MayInstall(Admin));
        Assert.False(closed.MayInstall(gm));
        Assert.Null(await closed.CheckAsync(Zip(Street()), gm));
        Assert.False((await closed.ReloadAsync(gm)).Succeeded);

        var (open, _, _) = await Server(new() { [SystemInstaller.GmInstallKey] = "true" });
        Assert.True(open.MayInstall(gm));
        Assert.False(open.MayReload(gm));
        Assert.False(open.MayInstall(new ClaimsPrincipal(new ClaimsIdentity())));

        var (exposed, _, _) = await Server(new() { [SystemInstaller.GmInstallKey] = "true", [Codex.Web.ExposurePolicy.ConfigKey] = "true" });
        Assert.False(exposed.MayInstall(gm));
        Assert.True(exposed.MayInstall(Admin));
    }

    [Fact]
    public async Task Installing_LoadsTheSystem_AndReplacing_SaysWhatChangesAndWhoUsesIt()
    {
        var (installer, loader, _) = await Server(campaigns: new[]
        {
            new CampaignDocument { Id = "c1", SystemId = "street" }, new CampaignDocument { Id = "c2", SystemId = "street" }, new CampaignDocument { Id = "c3", SystemId = "srd" }
        });

        var first = (await installer.CheckAsync(Zip(Street("1.0.0")), Admin))!;
        Assert.True(first.CanInstall);
        Assert.Null(first.Replaces);
        var installed = await installer.InstallAsync(first.Token, Admin);
        Assert.True(installed.Succeeded, installed.Error);
        Assert.Equal(new[] { "street" }, installed.Reload!.Added);
        Assert.True(installer.IsInstalled("street"));
        Assert.Equal(Path.Combine(installer.Root, "street", "system.yaml"), loader.GetSource("street"));
        Assert.False(Directory.Exists(first.Check.Folder));

        var second = (await installer.CheckAsync(Zip(Street("1.1.0", folder: "")), Admin))!;
        Assert.Equal("1.0.0", second.Replaces);
        Assert.Equal(2, second.CampaignsUsing);
        Assert.Contains(second.Changes, c => c is { Kind: SystemChangeKind.Version, Before: "1.0.0", After: "1.1.0" });
        Assert.Contains(second.Changes, c => c is { Kind: SystemChangeKind.Changed, File: "system.yaml" });
        Assert.True((await installer.InstallAsync(second.Token, Admin)).Succeeded);
        Assert.Equal("1.1.0", loader.GetDefinition("street")!.Version);
        Assert.Equal(new[] { "street" }, Directory.GetDirectories(installer.Root).Select(Path.GetFileName).Where(n => !n!.StartsWith('.')));
    }

    [Fact]
    public async Task ASystemTheAppDidNotInstall_CannotBeReplacedOrRemovedFromIt()
    {
        var (installer, _, _) = await Server();

        var plan = (await installer.CheckAsync(Zip(("system.yaml", System("srd", "9.0.0"))), Admin))!;

        Assert.False(plan.CanInstall);
        Assert.Contains("outside the app's systems folder", plan.Conflict);
        Assert.False((await installer.InstallAsync(plan.Token, Admin)).Succeeded);
        Assert.False(installer.IsInstalled("srd"));
        Assert.False((await installer.RemoveAsync("srd", Admin)).Succeeded);
    }

    [Fact]
    public async Task AnUpload_IsInstalledOnlyByWhoSentIt_AndOnlyOnce()
    {
        var (installer, _, _) = await Server(new() { [SystemInstaller.GmInstallKey] = "true" });
        var plan = (await installer.CheckAsync(Zip(Street()), User("gm")))!;

        Assert.False((await installer.InstallAsync(plan.Token, User("someone-else"))).Succeeded);
        Assert.False((await installer.InstallAsync(plan.Token, User("gm"))).Succeeded);
    }

    [Fact]
    public async Task Removing_UnloadsTheSystem_AndItsFolderGoes()
    {
        var (installer, loader, _) = await Server();
        var plan = (await installer.CheckAsync(Zip(Street()), Admin))!;
        await installer.InstallAsync(plan.Token, Admin);

        var removed = await installer.RemoveAsync("street", Admin);

        Assert.True(removed.Succeeded, removed.Error);
        Assert.Equal(new[] { "street" }, removed.Reload!.Removed);
        Assert.DoesNotContain("street", loader.LoadedSystemIds);
        Assert.False(Directory.Exists(Path.Combine(installer.Root, "street")));
        Assert.False(Directory.Exists(Path.Combine(installer.Root, ".trash")) && Directory.EnumerateFileSystemEntries(Path.Combine(installer.Root, ".trash")).Any());
    }

    [Fact]
    public async Task Discarding_DeletesTheStagedUpload()
    {
        var (installer, _, _) = await Server();
        var plan = (await installer.CheckAsync(Zip(Street()), Admin))!;

        installer.Discard(plan.Token);

        Assert.False(Directory.Exists(plan.Check.Folder));
        Assert.False((await installer.InstallAsync(plan.Token, Admin)).Succeeded);
    }
}
