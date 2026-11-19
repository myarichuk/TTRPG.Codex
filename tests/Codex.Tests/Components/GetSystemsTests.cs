using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Bunit;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Pages;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>"Get more game systems": what the page lists and says for each state, what it says when the list cannot be had (with the
/// upload still there), who may open it, and that a download ends in the same plan an upload does. A fake server stands in for the
/// release; nothing reaches the internet.</summary>
public sealed class GetSystemsTests : ComponentTestBase, IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "codex-get-systems-" + Guid.NewGuid().ToString("N")[..8]);
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

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

    private sealed class Server(GetSystemsTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(owner._respond(request));
    }

    private sealed class Signed(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private static string Yaml(string id, string version) =>
        $"id: {id}\nname: {id} rules\nversion: {version}\nentityTypes:\n  - {{ type: calling, label: Calling, folder: callings }}\n";

    private static byte[] Zip(string id, string version)
    {
        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("system.yaml").Open());
            writer.Write(Yaml(id, version));
        }

        return bytes.ToArray();
    }

    private static SystemCatalogEntry Entry(string id, string version, byte[]? archive = null, bool scripts = false)
    {
        archive ??= Zip(id, version);
        return new SystemCatalogEntry(id, $"{id} rules", version, $"About {id}.", "CC-BY-4.0", null, scripts, $"system-{id}-{version}.zip", archive.Length,
            Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant());
    }

    private static ClaimsPrincipal Admin() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, UserRoles.ServerAdmin) }, "test"));

    private static ClaimsPrincipal Player() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "player") }, "test"));

    /// <summary>Loads "street" and "newer" (1.0.0) from the install folder and "rock" from outside it, then renders the page.</summary>
    private async Task<IRenderedComponent<GetSystems>> OpenAsync(ClaimsPrincipal user, string? catalogUrl = null, IConfiguration? extra = null, bool release = true)
    {
        var root = Path.Combine(_scratch, "Systems");
        foreach (var (folder, id) in new[] { (Path.Combine(root, "street"), "street"), (Path.Combine(root, "newer"), "newer"), (Path.Combine(_scratch, "content", "rock"), "rock") })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "system.yaml"), Yaml(id, "1.0.0"));
        }

        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
        await loader.LoadAndInitializeAsync(_scratch);
        var repository = Substitute.For<ICampaignRepository>();
        repository.GetAllAsync().Returns(Array.Empty<CampaignDocument>());
        var configuration = extra ?? new ConfigurationBuilder().Build();
        var installer = new SystemInstaller(loader, repository, configuration, NullLogger<SystemInstaller>.Instance, root);
        installer.Prepare();

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Server(this), disposeHandler: false));
        var settings = new ConfigurationBuilder().AddInMemoryCollection(catalogUrl == null ? new Dictionary<string, string?>() : new() { [SystemCatalog.CatalogUrlKey] = catalogUrl }).Build();

        Services.AddSingleton(loader);
        Services.AddSingleton(installer);
        Services.AddSingleton(new SystemCatalog(factory, settings, NullLogger<SystemCatalog>.Instance, "1.4.0", release));
        Services.AddSingleton<AuthenticationStateProvider>(new Signed(user));
        return Render<GetSystems>();
    }

    private HttpResponseMessage Listing(params SystemCatalogEntry[] entries) =>
        new(HttpStatusCode.OK) { Content = new StringContent(SystemCatalogFile.Write(entries), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task EachSystem_ShowsItsDetails_AndItsState()
    {
        _respond = _ => Listing(Entry("fresh", "1.0.0", scripts: true), Entry("street", "1.0.0"), Entry("newer", "1.1.0"), Entry("rock", "1.0.0"));

        var cut = await OpenAsync(Admin());

        cut.WaitForAssertion(() => cut.Find(Id("catalog-fresh")));
        Assert.Contains("fresh rules", cut.Find(Id("catalog-fresh")).TextContent);
        Assert.Contains("v1.0.0", cut.Find(Id("catalog-fresh")).TextContent);
        Assert.Contains("About fresh.", cut.Find(Id("catalog-fresh")).TextContent);
        Assert.Contains("Licence: CC-BY-4.0", cut.Find(Id("catalog-fresh")).TextContent);
        Assert.Contains("Has scripts", cut.Find(Id("catalog-scripts-fresh")).TextContent);
        Assert.Empty(cut.FindAll(Id("catalog-scripts-street")));
        Assert.Equal("Install", cut.Find(Id("catalog-install-fresh")).TextContent.Trim());
        Assert.Contains("Installed", cut.Find(Id("catalog-state-street")).TextContent);
        Assert.Empty(cut.FindAll(Id("catalog-install-street")));
        Assert.Equal("Update to v1.1.0", cut.Find(Id("catalog-update-newer")).TextContent.Trim());
        Assert.Contains("Installed: v1.0.0", cut.Find(Id("catalog-state-newer")).TextContent);
        Assert.Contains("Bundled with the app", cut.Find(Id("catalog-state-rock")).TextContent);
        Assert.Empty(cut.FindAll(Id("catalog-install-rock")));
        Assert.NotEmpty(cut.FindAll(Id("install-system"))); // the upload is there too
    }

    [Fact]
    public async Task WhenTheListCannotBeHad_ThePageSaysSoPlainly_AndTheUploadStays()
    {
        _respond = _ => throw new HttpRequestException("offline");

        var cut = await OpenAsync(Admin());

        cut.WaitForAssertion(() => cut.Find(Id("systems-failed")));
        Assert.Equal("Couldn't reach the list of systems. You can still install a downloaded file.", cut.Find(Id("systems-failed")).QuerySelector("p")!.TextContent.Trim());
        Assert.Equal("alert", cut.Find(Id("systems-failed")).GetAttribute("role"));
        Assert.NotEmpty(cut.FindAll(Id("install-system")));
        Assert.Empty(cut.FindAll(Id("catalog-fresh")));
    }

    [Fact]
    public async Task WithTheListSwitchedOff_OnlyTheUploadIsOffered()
    {
        var cut = await OpenAsync(Admin(), catalogUrl: "");

        Assert.NotEmpty(cut.FindAll(Id("systems-off")));
        Assert.NotEmpty(cut.FindAll(Id("install-system")));
    }

    [Fact]
    public async Task ABuildWithNoReleaseVersion_SaysTheListIsTheLatestReleases()
    {
        _respond = _ => Listing();
        var cut = await OpenAsync(Admin(), release: false);

        cut.WaitForAssertion(() => cut.Find(Id("systems-empty")));
        Assert.Contains("latest release", cut.Find(Id("systems-from-latest")).TextContent);
    }

    [Fact]
    public async Task AReleasedBuild_SaysNothingAboutTheLatestRelease()
    {
        _respond = _ => Listing();
        var cut = await OpenAsync(Admin());

        cut.WaitForAssertion(() => cut.Find(Id("systems-empty")));
        Assert.Empty(cut.FindAll(Id("systems-from-latest")));
    }

    [Fact]
    public async Task SomeoneWhoMayNotInstall_IsSentHome_AndSeesNothing()
    {
        var cut = await OpenAsync(Player());

        Assert.Empty(cut.FindAll("h1"));
        Assert.Empty(cut.FindAll(Id("systems-list")));
        Assert.EndsWith("/", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task APlayerWhenGmInstallIsOn_MayOpenIt()
    {
        _respond = _ => Listing();
        var on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SystemInstaller.GmInstallKey] = "true" }).Build();

        var cut = await OpenAsync(Player(), extra: on);

        cut.WaitForAssertion(() => cut.Find("h1"));
        Assert.Equal("Get more game systems", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public async Task Installing_DownloadsTheArchive_AndShowsTheSamePlanAnUploadShows()
    {
        var archive = Zip("fresh", "1.0.0");
        _respond = r => r.RequestUri!.AbsolutePath.EndsWith("systems.json", StringComparison.Ordinal)
            ? Listing(Entry("fresh", "1.0.0", archive))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        var cut = await OpenAsync(Admin());
        cut.WaitForAssertion(() => cut.Find(Id("catalog-install-fresh")));

        cut.Find(Id("catalog-install-fresh")).Click();

        cut.WaitForAssertion(() => cut.Find(Id("install-plan")));
        Assert.Contains("fresh rules", cut.Find(Id("install-plan")).TextContent);
        Assert.NotEmpty(cut.FindAll(Id("install-confirm")));
        Assert.NotEmpty(cut.FindAll(Id("install-cancel")));
    }

    [Fact]
    public async Task WhileAPlanIsOpen_TheListButtonsAreOff_SoOnePlanIsNeverReplacedUnseen()
    {
        _respond = r => r.RequestUri!.AbsolutePath.EndsWith("systems.json", StringComparison.Ordinal)
            ? Listing(Entry("fresh", "1.0.0"), Entry("other", "1.0.0"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(r.RequestUri.AbsolutePath.Contains("other") ? "other" : "fresh", "1.0.0")) };
        var cut = await OpenAsync(Admin());
        cut.WaitForAssertion(() => cut.Find(Id("catalog-install-fresh")));

        cut.Find(Id("catalog-install-fresh")).Click();

        cut.WaitForAssertion(() => cut.Find(Id("install-plan")));
        Assert.True(cut.Find(Id("catalog-install-other")).HasAttribute("disabled"));
        Assert.True(cut.Find(Id("catalog-install-fresh")).HasAttribute("disabled"));
        cut.Find(Id("install-cancel")).Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find(Id("catalog-install-other")).HasAttribute("disabled")));
    }

    [Fact]
    public async Task APreReleaseIsNeverOfferedAsAnUpdateOverItsFinalRelease_ButTheFinalIsOverThePreRelease()
    {
        _respond = _ => Listing(Entry("street", "1.0.0-beta.1"), Entry("newer", "1.0.1"), Entry("rock", "1.0.0-rc.1"));
        var cut = await OpenAsync(Admin());

        cut.WaitForAssertion(() => cut.Find(Id("catalog-street")));
        Assert.Empty(cut.FindAll(Id("catalog-update-street")));
        Assert.Contains("Installed", cut.Find(Id("catalog-state-street")).TextContent);
        Assert.NotEmpty(cut.FindAll(Id("catalog-update-newer")));
    }

    [Fact]
    public async Task AnArchiveForAnotherSystemThanTheListNamed_IsDiscarded_AndNothingIsOffered()
    {
        // The list says "fresh", the bytes (and their checksum) are of "swapped".
        var swapped = Zip("swapped", "1.0.0");
        _respond = r => r.RequestUri!.AbsolutePath.EndsWith("systems.json", StringComparison.Ordinal)
            ? Listing(Entry("fresh", "1.0.0", swapped))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(swapped) };
        var cut = await OpenAsync(Admin());
        cut.WaitForAssertion(() => cut.Find(Id("catalog-install-fresh")));

        cut.Find(Id("catalog-install-fresh")).Click();

        cut.WaitForAssertion(() => Assert.Contains("was not the one the list described", cut.Markup));
        Assert.Empty(cut.FindAll(Id("install-plan")));
    }

    [Fact]
    public async Task ADownloadThatIsNotWhatTheListDescribed_InstallsNothing_AndSaysSo()
    {
        var archive = Zip("fresh", "1.0.0");
        _respond = r => r.RequestUri!.AbsolutePath.EndsWith("systems.json", StringComparison.Ordinal)
            ? Listing(Entry("fresh", "1.0.0", archive))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("fresh", "9.9.9")) };
        var cut = await OpenAsync(Admin());
        cut.WaitForAssertion(() => cut.Find(Id("catalog-install-fresh")));

        cut.Find(Id("catalog-install-fresh")).Click();

        cut.WaitForAssertion(() => Assert.Contains("was not the one the list described", cut.Markup));
        Assert.Empty(cut.FindAll(Id("install-plan")));
    }
}
