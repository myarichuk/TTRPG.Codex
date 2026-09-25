using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Boots the real <c>Codex.Web</c> app as a child process, pointed at a throwaway RavenDB data
/// directory, and a headless Chromium instance to drive it (Phase 3 exit criterion: "an automated
/// Playwright end-to-end test"). The app owns its own embedded RavenDB server; this fixture
/// deliberately does NOT also open that data directory itself (two processes opening the same
/// embedded RavenDB directory would fight over its file lock) - instead it seeds through the
/// app's own opt-in <c>/test/seed/*</c> endpoints (see Program.cs), so seeding goes through the
/// same repositories, validation, and RavenDB session the app under test already uses.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private Process? _process;
    private readonly StringBuilder _output = new();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "E2E_RavenData_" + Guid.NewGuid());
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = string.Empty;
    public IBrowser Browser { get; private set; } = null!;
    public HttpClient Http { get; } = new();

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        Http.BaseAddress = new Uri(BaseUrl);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(Path.Combine(repoRoot, "src", "Codex.Web", "Codex.Web.csproj"));
        psi.ArgumentList.Add("--no-launch-profile");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        psi.Environment["Codex__DataDirectory"] = _dataDir;
        // The suite's own set of goblins/hidden-NPC/PCs is seeded through these endpoints (see
        // TestSeeding.cs) rather than a second process writing straight to RavenDB - see the type
        // doc comment above for why.
        psi.Environment["Codex__EnableTestSeedEndpoint"] = "true";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_output) _output.AppendLine(e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilReadyAsync();

        _playwright = await Playwright.CreateAsync();
        var options = new BrowserTypeLaunchOptions { Headless = true };

        // This sandbox has Chromium pre-cached outside Playwright's normal revision-managed
        // browsers folder; point at it directly rather than triggering (or requiring) a
        // `playwright install` download. CI installs browsers the normal way (see ci.yml), so
        // there ExecutablePath is left unset and the default resolution applies.
        const string sandboxChromium = "/opt/pw-browsers/chromium";
        if (File.Exists(sandboxChromium))
        {
            options.ExecutablePath = sandboxChromium;
        }

        Browser = await _playwright.Chromium.LaunchAsync(options);
    }

    /// <summary>Seeds a user directly through the app's own repository - same password-hashing
    /// path <c>Register.razor</c> uses, so the real <c>/login</c> form works against it.</summary>
    public Task SeedUserAsync(string id, string username, string password, params string[] roles) =>
        Http.PostAsJsonAsync("/test/seed/users", new[]
        {
            new Codex.Web.TestSeedUser { Id = id, Username = username, Password = password, Roles = roles.ToList() }
        });

    public Task SeedActorsAsync(IEnumerable<Codex.Web.TestSeedActor> actors) =>
        Http.PostAsJsonAsync("/test/seed/actors", actors.ToList());

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            if (_process!.HasExited)
            {
                throw new InvalidOperationException($"Codex.Web exited early (code {_process.ExitCode}) before becoming ready:\n{_output}");
            }

            try
            {
                using var response = await Http.GetAsync("/login");
                return; // Any HTTP response at all means Kestrel is up and routing works.
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                await Task.Delay(500);
            }
        }

        throw new TimeoutException($"Codex.Web never became ready at {BaseUrl}.\nLast error: {lastError}\nProcess output:\n{_output}");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Walks up from the test assembly's output directory to find the checkout root
    /// (marked by the .sln) - robust to Debug/Release and whatever TFM segment names, unlike
    /// hardcoding a fixed number of "..".</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !dir.GetFiles("TTRPG.Codex.sln").Any())
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (TTRPG.Codex.sln) above " + AppContext.BaseDirectory);
    }

    public async Task DisposeAsync()
    {
        if (Browser != null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
        Http.Dispose();

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Best-effort - the process is going away with the test host regardless.
            }
        }

        _process?.Dispose();

        try
        {
            await Task.Delay(500); // let RavenDB's embedded server release its file handles
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of a throwaway temp directory.
        }
    }
}
