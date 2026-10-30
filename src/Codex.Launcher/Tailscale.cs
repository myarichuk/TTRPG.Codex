using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codex.Launcher;

/// <summary>What <c>tailscale status --json</c> says, reduced to what the launcher needs. The field names come
/// from Tailscale's own client rather than its documentation, so every one is optional: a missing field
/// means "unknown", never a failure.</summary>
internal sealed record TailscaleStatus(string? BackendState, string? DnsName, bool? HttpsEnabled)
{
    public bool SignedIn => string.Equals(BackendState, "Running", StringComparison.Ordinal);
}

internal enum FunnelLineKind { Other, Public, Approval, NeedsPermission }

/// <summary>One line of <c>tailscale funnel</c> output, understood.</summary>
internal sealed record FunnelLine(FunnelLineKind Kind, string? Url = null);

internal static partial class TailscaleParser
{
    [GeneratedRegex(@"https://[^\s""'<>()]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>Reads the status JSON; <c>null</c> when the text is not JSON at all.</summary>
    public static TailscaleStatus? ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var backend = Text(root, "BackendState");
            string? dns = null;
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object) dns = Text(self, "DNSName");
            dns = dns?.TrimEnd('.');

            bool? https = null;
            if (root.TryGetProperty("CertDomains", out var certs))
            {
                // null or an empty list: the tailnet has not turned HTTPS certificates on.
                https = certs.ValueKind == JsonValueKind.Array && certs.GetArrayLength() > 0;
            }

            return new TailscaleStatus(backend, string.IsNullOrEmpty(dns) ? null : dns, https);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Picks the public address and the approval page out of what the CLI prints, wherever on the line they sit.</summary>
    public static FunnelLine ParseFunnelLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new(FunnelLineKind.Other);

        // An error that happens to name the address is not the address being served.
        var failed = line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase);

        foreach (Match match in UrlPattern().Matches(line))
        {
            var text = match.Value.TrimEnd('.', ',', ';', ':');
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
            if (uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
            {
                if (failed) continue;
                return new(FunnelLineKind.Public, uri.GetLeftPart(UriPartial.Authority));
            }

            if (uri.Host.Equals("tailscale.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".tailscale.com", StringComparison.OrdinalIgnoreCase))
            {
                return new(FunnelLineKind.Approval, text);
            }
        }

        if (line.Contains("access denied", StringComparison.OrdinalIgnoreCase) || line.Contains("--operator", StringComparison.OrdinalIgnoreCase))
        {
            return new(FunnelLineKind.NeedsPermission);
        }

        return new(FunnelLineKind.Other);
    }
}

internal static class TailscaleCli
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The <c>tailscale</c> command line, or <c>null</c> when it is not installed. A tray app started from
    /// the desktop has a short PATH, so the usual install folders are tried as well.</summary>
    public static string? Find() =>
        Candidates(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).FirstOrDefault(File.Exists);

    internal static IEnumerable<string> Candidates(bool windows, bool mac, string? path, string programFiles)
    {
        var name = windows ? "tailscale.exe" : "tailscale";
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) yield return Path.Combine(dir, name);
        if (windows)
        {
            yield return Path.Combine(programFiles, "Tailscale", name);
        }
        else
        {
            if (mac) yield return "/Applications/Tailscale.app/Contents/MacOS/Tailscale";
            yield return "/usr/bin/tailscale";
            yield return "/usr/local/bin/tailscale";
        }
    }

    /// <summary>Whether the text of <c>tailscale funnel status</c> shows a public address forwarding to this port, as a
    /// run of the launcher that was killed would leave behind. Judged from the text alone, since the format is not documented.</summary>
    internal static bool FunnelPointsAtPort(string? statusText, int port) =>
        statusText != null
        && statusText.Contains(".ts.net", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(statusText, @":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"(?!\d)");

    /// <summary>The output of <c>tailscale funnel status</c>, or <c>null</c> when it cannot be had.</summary>
    public static Task<string?> FunnelStatusAsync(string exe) => RunAsync(exe, "funnel", "status");

    /// <summary>Asks Tailscale for its status; <c>null</c> when it cannot be reached or answers with something else.</summary>
    public static async Task<TailscaleStatus?> StatusAsync(string exe) => TailscaleParser.ParseStatus(await RunAsync(exe, "status", "--json"));

    /// <summary>Runs one short command and returns what it printed. A command that hangs (a client whose daemon is down) is
    /// killed at the timeout rather than left behind.</summary>
    private static async Task<string?> RunAsync(string exe, params string[] args)
    {
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            process = Process.Start(info);
            if (process == null) return null;

            using var timeout = new CancellationTokenSource(StatusTimeout);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
        }
    }
}

/// <summary>One run of <c>tailscale funnel &lt;port&gt;</c>, kept in the foreground on purpose: Tailscale takes the
/// public address down the moment this process ends, so the server can never stay on the internet after the
/// launcher (or this session) is gone. It never uses <c>--bg</c> and never runs <c>funnel reset</c>, which would
/// also wipe any Serve or Funnel setup the person has made for other things.</summary>
internal sealed class FunnelSession : IDisposable
{
    private readonly string exe;
    private readonly int port;
    private readonly Action<FunnelLine> onLine;
    private readonly Action<string> log;
    private readonly Action<int?> onExit;
    private volatile Process? process;
    private volatile bool stopping;

    public FunnelSession(string exe, int port, Action<FunnelLine> onLine, Action<string> log, Action<int?> onExit)
    {
        this.exe = exe;
        this.port = port;
        this.onLine = onLine;
        this.log = log;
        this.onExit = onExit;
    }

    public int? ProcessId { get; private set; }

    public bool IsRunning => process is { } p && !HasExited(p);

    public void Start()
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("funnel");
        info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var child = new Process { StartInfo = info, EnableRaisingEvents = true };
        child.OutputDataReceived += (_, e) => Handle(e.Data);
        child.ErrorDataReceived += (_, e) => Handle(e.Data);
        child.Exited += (_, _) =>
        {
            int? code = null;
            try { child.WaitForExit(); code = child.ExitCode; }
            catch (InvalidOperationException) { }
            if (!stopping) onExit(code);
        };
        log($"--- tailscale funnel {port} ---");
        process = child; // before it runs, so a Stop from another thread always finds it
        child.Start();
        ProcessId = child.Id;
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
    }

    private void Handle(string? line)
    {
        if (line == null) return;
        log("[funnel] " + line);
        onLine(TailscaleParser.ParseFunnelLine(line));
    }

    public void Stop()
    {
        stopping = true;
        var child = process;
        process = null;
        if (child == null) return;
        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            child.Dispose();
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    public void Dispose() => Stop();
}
