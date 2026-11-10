using System.IO.Compression;
using System.Text;
using Codex.Persistence;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Overhaul step 10, in the browser: the server's admin installs a system from a zip on About, a campaign is made on it, a newer
/// version replaces it (the plan says one campaign uses it), the GM sees what changed until <i>Got it</i>, and removing it leaves
/// the campaign read-only. The app this class starts installs into its own data folder, so no other test sees the system.
/// </summary>
[Collection("RavenDb")]
public class SystemInstallE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public SystemInstallE2ETests(AppFixture app) => _app = app;

    private static FilePayload Archive(string id, string version)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry($"{id}/system.yaml").Open(), new UTF8Encoding(false));
            writer.Write($"id: {id}\nname: Rooftop Rules\nversion: {version}\nattributes:\n  - {{ key: BODY, label: Body, default: 5 }}\n");
        }

        return new FilePayload { Name = $"system-{id}-{version}.zip", MimeType = "application/zip", Buffer = buffer.ToArray() };
    }

    private static async Task UploadAsync(IPage page, FilePayload archive)
    {
        await E2EHelpers.GotoInteractiveAsync(page, "/about");
        await page.GetByTestId("install-system").Locator("input[type=file]").SetInputFilesAsync(archive);
        await page.GetByTestId("install-plan").WaitForAsync();
    }

    [Fact]
    public async Task InstallReplaceAndRemove_AsTheAdmin_Async()
    {
        const string password = "Correct-Horse-1";
        var adminId = "install-admin-" + Guid.NewGuid();
        var systemId = "rooftop-" + Guid.NewGuid().ToString("N")[..6];
        await _app.SeedUserAsync(adminId, "admin_" + adminId, password, UserRoles.ServerAdmin, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var page = await context.NewPageAsync();
        await E2EHelpers.LoginAsync(page, "admin_" + adminId, password);
        var listed = page.GetByTestId("about-system").Filter(new() { HasText = "Rooftop Rules" });

        // ---- 1. Install: the plan names the system, and installing puts it on About. ----
        await UploadAsync(page, Archive(systemId, "1.0.0"));
        await Assertions.Expect(page.GetByTestId("install-plan")).ToContainTextAsync(systemId);
        await Assertions.Expect(page.GetByTestId("install-replaces")).ToHaveCountAsync(0);
        await page.GetByTestId("install-confirm").ClickAsync();
        await Assertions.Expect(listed.GetByTestId("about-system-version")).ToContainTextAsync("1.0.0");

        var campaignId = await E2EHelpers.CreateCampaignAsync(page, "Rooftop Campaign", systemId);

        // ---- 2. Replace: the plan says what it replaces, what changed and how many campaigns use it. ----
        await UploadAsync(page, Archive(systemId, "1.1.0"));
        var replaces = page.GetByTestId("install-replaces");
        await Assertions.Expect(replaces).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("install-plan")).ToContainTextAsync("1.1.0");
        await page.GetByTestId("install-confirm").ClickAsync();
        await Assertions.Expect(listed.GetByTestId("about-system-version")).ToContainTextAsync("1.1.0");

        // ---- 3. The GM sees the change on the campaign until they acknowledge it. ----
        await E2EHelpers.GotoInteractiveAsync(page, $"/campaigns/{campaignId}");
        await Assertions.Expect(page.GetByTestId("campaign-system-changes")).ToContainTextAsync("1.1.0");
        await page.GetByTestId("campaign-system-accept").ClickAsync();
        await Assertions.Expect(page.GetByTestId("campaign-system-changed")).ToHaveCountAsync(0);
        await E2EHelpers.GotoInteractiveAsync(page, $"/campaigns/{campaignId}");
        await Assertions.Expect(page.GetByTestId("campaign-title")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("campaign-system-changed")).ToHaveCountAsync(0);

        // ---- 4. Remove: asked first; afterwards the campaign is read-only, saying its system is missing. ----
        await E2EHelpers.GotoInteractiveAsync(page, "/about");
        await listed.GetByTestId("remove-system").ClickAsync();
        await page.GetByTestId("confirm-accept").ClickAsync();
        await Assertions.Expect(listed).ToHaveCountAsync(0);
        await E2EHelpers.GotoInteractiveAsync(page, $"/campaigns/{campaignId}");
        await Assertions.Expect(page.GetByTestId("campaign-system-missing")).ToBeVisibleAsync();
    }
}
