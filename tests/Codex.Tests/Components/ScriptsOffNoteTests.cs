using System.IO.Compression;
using System.Security.Claims;
using Bunit;
using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>What a person is told when a system's scripts are left out because the server has them off: the sentence, as a status, with a
/// pill in the systems list, a link to the switch for the admin only, and said once in the install panel.</summary>
public sealed class ScriptsOffNoteTests : ComponentTestBase, IDisposable
{
    private const string Sentence = "Some of this system's rules are done by hand, because scripts are turned off on this server.";
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "codex-scripts-off-" + Guid.NewGuid().ToString("N")[..8]);

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void InTheSystemsList_ItIsAPillAndTheSentence_WithTheLinkForTheAdminOnly()
    {
        var admin = Render<ScriptsOffNote>(p => p.Add(c => c.Pill, true).Add(c => c.Link, true).Add(c => c.TestId, "about-system-scripts-off"));
        var player = Render<ScriptsOffNote>(p => p.Add(c => c.Pill, true).Add(c => c.Link, false).Add(c => c.TestId, "about-system-scripts-off"));

        Assert.Equal("status", admin.Find(Id("about-system-scripts-off")).GetAttribute("role"));
        Assert.Contains("Scripts off", admin.Find(".pill").TextContent);
        Assert.Equal(Sentence, admin.Find(Id("about-system-scripts-off-text")).TextContent.Trim());
        var link = admin.Find(Id("about-system-scripts-off-turn-on"));
        Assert.Equal("/admin#settings-scripts", link.GetAttribute("href"));
        Assert.Equal("Turn on scripts", link.TextContent.Trim());
        Assert.Empty(player.FindAll("a"));
        Assert.Equal(Sentence, player.Find(Id("about-system-scripts-off-text")).TextContent.Trim());
    }

    [Fact]
    public void BesideTheNewCampaignChoice_ItIsOneLineOfTheSentence_WithNoPillAndNoLink()
    {
        var cut = Render<ScriptsOffNote>(p => p.Add(c => c.Class, "field-hint").Add(c => c.TestId, "new-campaign-scripts-off"));

        Assert.Equal("status", cut.Find(Id("new-campaign-scripts-off")).GetAttribute("role"));
        Assert.Contains(Sentence, cut.Find(Id("new-campaign-scripts-off")).TextContent);
        Assert.Empty(cut.FindAll(".pill"));
        Assert.Empty(cut.FindAll("a"));
    }

    private sealed class Signed(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    [Fact]
    public async Task InTheInstallPanel_AnUploadNamingScripts_SaysItOnce_ForTheAdminWithTheLink()
    {
        Directory.CreateDirectory(_scratch);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry())) { ScriptsAllowed = false };
        var repository = Substitute.For<ICampaignRepository>();
        repository.GetAllAsync().Returns(Array.Empty<CampaignDocument>());
        var installer = new SystemInstaller(loader, repository, new ConfigurationBuilder().Build(), NullLogger<SystemInstaller>.Instance, Path.Combine(_scratch, "Systems"));
        installer.Prepare();
        var admin = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, UserRoles.ServerAdmin) }, "test"));
        Services.AddSingleton(installer);
        Services.AddSingleton<AuthenticationStateProvider>(new Signed(admin));

        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
                     {
                         ("system.yaml", "id: street\nname: Street\nversion: 1.0.0\nentityTypes:\n  - { type: calling, label: Calling, folder: callings }\nfunctions:\n  twice: { script: rules.js#twice }\n"),
                         ("rules.js", "export function twice(x) { return 2 * x; }\n")
                     })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        var cut = Render<SystemInstallPanel>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes.ToArray(), "street.zip"));

        cut.WaitForAssertion(() => cut.Find(Id("install-plan")));
        Assert.Equal(Sentence, cut.Find(Id("install-scripts-off-text")).TextContent.Trim());
        Assert.Equal("/admin#settings-scripts", cut.Find(Id("install-scripts-off-turn-on")).GetAttribute("href"));
        Assert.Empty(cut.FindAll(Id("install-errors"))); // a warning, not an error: it installs
        Assert.Empty(cut.FindAll(Id("install-warnings"))); // and the English line from the check is not said again
        Assert.NotEmpty(cut.FindAll(Id("install-confirm")));
        await Task.CompletedTask;
    }
}
