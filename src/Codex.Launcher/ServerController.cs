using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Codex.Launcher;

internal enum ServerState { Stopped, Starting, Running, Stopping, Failed }

/// <summary>Starts and stops the TTRPG.Codex web server as a child process on the bundled .NET runtime.
/// The server's stdin stays open: closing it is the stop request (see <c>Codex:StopOnStdinClose</c>), which
/// also stops the server if the launcher is killed.</summary>
internal sealed class ServerController : IDisposable
{
    private const int LogLinesKept = 300;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    private readonly string serverDir;
    private readonly string dataHome;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentQueue<string> recentLines = new();
    private readonly object gate = new();
    private Process? process;
    private StreamWriter? logWriter;
    private bool stopRequested;

    public ServerController(string serverDir, string dataHome)
    {
        this.serverDir = serverDir;
        this.dataHome = dataHome;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? Problem { get; private set; }
    public string LogPath => LauncherPaths.LogFile(dataHome);

    /// <summary>Raised on a thread-pool thread whenever <see cref="State"/> changes.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<string> RecentLines => recentLines.ToArray();

    public bool IsBusy => State is ServerState.Starting or ServerState.Stopping;

    /// <summary>Why the server cannot be started here, or <c>null</c> when it can.</summary>
    public string? CheckInstall()
    {
        if (!File.Exists(Path.Combine(serverDir, "Codex.Web.dll")))
        {
            return $"Codex.Web.dll was not found in {serverDir}.";
        }

        if (LauncherPaths.DotnetHost(serverDir) == "dotnet" && !File.Exists(Path.Combine(serverDir, "runtime", "dotnet")) && !File.Exists(Path.Combine(serverDir, "runtime", "dotnet.exe")))
        {
            // Not fatal in a build tree (the PATH has dotnet), but a release always has its own runtime.
            return Directory.Exists(Path.Combine(serverDir, "runtime")) ? "The bundled .NET runtime is incomplete." : null;
        }

        return null;
    }

    public void Start(LauncherSettings settings)
    {
        lock (gate)
        {
            if (State is ServerState.Starting or ServerState.Running or ServerState.Stopping) return;
            Problem = null;
            stopRequested = false;
        }

        if (CheckInstall() is { } missing)
        {
            Fail(missing);
            return;
        }

        try
        {
            OpenLog();
            var host = LauncherPaths.DotnetHost(serverDir);
            var info = new ProcessStartInfo(host)
            {
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.ArgumentList.Add(Path.Combine(serverDir, "Codex.Web.dll"));
            if (host != "dotnet") info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(host)!;
            info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            info.Environment["ASPNETCORE_URLS"] = settings.BindUrl;
            info.Environment["Codex__DataDirectory"] = LauncherPaths.RavenData(dataHome);
            info.Environment["Codex__StopOnStdinClose"] = "true";
            // Invite links must carry an address players can reach, not the DM's own "localhost".
            if (settings.PlayerUrl(LanAddress.Best(), LanAddress.Tailnet()) is { } playerUrl) info.Environment["Codex__PublicUrl"] = playerUrl;

            Directory.CreateDirectory(LauncherPaths.RavenData(dataHome));
            var child = new Process { StartInfo = info, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, e) => Log(e.Data);
            child.ErrorDataReceived += (_, e) => Log(e.Data);
            child.Exited += (_, _) => OnExited(child);
            Log($"--- starting {host} Codex.Web.dll on {settings.BindUrl} ---");
            child.Start();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            process = child;
            SetState(ServerState.Starting);
            _ = Task.Run(() => WaitUntilReadyAsync(child, settings.LocalUrl));
        }
        catch (Exception ex)
        {
            Fail("The server could not be started: " + ex.Message);
        }
    }

    /// <summary>Asks the server to stop by closing its stdin, then ends the whole process tree (the
    /// embedded database is a child of the server) if it has not gone within the timeout.</summary>
    public Task StopAsync()
    {
        Process? child;
        lock (gate)
        {
            child = process;
            if (child == null || State is ServerState.Stopped or ServerState.Failed or ServerState.Stopping) return Task.CompletedTask;
            stopRequested = true;
        }

        SetState(ServerState.Stopping);
        return Task.Run(() =>
        {
            try
            {
                child.StandardInput.Close();
                if (!child.WaitForExit(StopTimeout))
                {
                    Log("--- did not stop in time, ending the process tree ---");
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // Already gone.
            }
        });
    }

    private async Task WaitUntilReadyAsync(Process child, string localUrl)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!ReferenceEquals(process, child) || HasExited(child)) return;
            try
            {
                using var response = await http.GetAsync(localUrl + "/login");
                if (response.IsSuccessStatusCode)
                {
                    lock (gate)
                    {
                        if (!ReferenceEquals(process, child) || State != ServerState.Starting) return;
                    }

                    SetState(ServerState.Running);
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        if (!HasExited(child))
        {
            Fail("The server did not answer in time. See the log for details.");
            await StopAsync();
        }
    }

    private void OnExited(Process child)
    {
        bool unexpected;
        lock (gate)
        {
            if (!ReferenceEquals(process, child)) return;
            unexpected = !stopRequested;
            process = null;
        }

        Log($"--- server exited ({SafeExitCode(child)}) ---");
        logWriter?.Flush();
        if (unexpected && State != ServerState.Failed)
        {
            Fail("The server stopped unexpectedly. Another program may be using the port. See the log for details.");
        }
        else if (State != ServerState.Failed)
        {
            SetState(ServerState.Stopped);
        }
    }

    private void Fail(string problem)
    {
        Problem = problem;
        Log(problem);
        SetState(ServerState.Failed);
    }

    private void SetState(ServerState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static string SafeExitCode(Process p)
    {
        try { return "code " + p.ExitCode; }
        catch (InvalidOperationException) { return "code unknown"; }
    }

    private void OpenLog()
    {
        lock (gate)
        {
            logWriter?.Dispose();
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Move(path, path + ".1", overwrite: true);
            logWriter = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            while (recentLines.TryDequeue(out _)) { }
        }
    }

    /// <summary>Adds a launcher line to the log beside the server's own output.</summary>
    public void Note(string line) => Log("[launcher] " + line);

    private void Log(string? line)
    {
        if (line == null) return;
        recentLines.Enqueue(line);
        while (recentLines.Count > LogLinesKept && recentLines.TryDequeue(out _)) { }
        lock (gate)
        {
            try { logWriter?.WriteLine(line); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        try { StopAsync().Wait(StopTimeout + TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { }
        http.Dispose();
        lock (gate) logWriter?.Dispose();
    }
}
