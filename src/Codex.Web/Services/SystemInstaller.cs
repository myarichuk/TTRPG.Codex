using System.Collections.Concurrent;
using System.Security.Claims;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>A checked upload waiting for the person who sent it to install it: what it is, what it replaces, who uses that.</summary>
/// <param name="Token">What <see cref="SystemInstaller.InstallAsync"/> takes to install it.</param>
/// <param name="Replaces">The installed version it would replace, or null for a system not installed yet.</param>
/// <param name="Changes">What differs from the installed one (version, files).</param>
/// <param name="CampaignsUsing">How many campaigns, anyone's, play under the system it replaces.</param>
/// <param name="Conflict">Why it cannot replace what is installed (a system shipped with the app or copied onto the server by
/// hand), or null.</param>
public sealed record SystemInstallPlan(
    string Token,
    SystemArchiveCheck Check,
    string? Replaces,
    IReadOnlyList<SystemChange> Changes,
    int CampaignsUsing,
    string? Conflict)
{
    public bool CanInstall => Check.CanInstall && Conflict == null;
}

/// <summary>What an install, a removal or a reload did, for the page to say.</summary>
public sealed record SystemInstallResult(bool Succeeded, string? Error, SystemsReload? Reload = null)
{
    public static SystemInstallResult Fail(string error) => new(false, error);
}

/// <summary>
/// Installs, replaces and removes systems from the app (overhaul step 10, section 4 rule 7), and reloads them.
/// <list type="bullet">
/// <item><b>Where.</b> Systems the app installs live in their own folder, <see cref="Root"/> (<c>Codex:Systems:InstallDirectory</c>,
/// by default <c>Systems</c> beside the database's folder), which is one of the content roots. Only systems found there can be
/// replaced or removed from the app; one shipped with the app, or copied into another content folder by hand, is the server's
/// to change.</item>
/// <item><b>Who.</b> The server's admin. Any signed-in user too when <c>Codex:Systems:GmInstall</c> is on, except while the
/// server is open to the internet (<see cref="ExposurePolicy"/>). Reloading the server's systems is the admin's alone.</item>
/// <item><b>What.</b> A zip of one system, checked by <see cref="SystemArchive"/>: never compiled code, and no scripts when they are
/// turned off.</item>
/// <item><b>How.</b> The upload is unpacked into a hidden staging folder under <see cref="Root"/> and checked there; installing
/// moves it into place and reloads (<see cref="PluginLoader.ReloadAsync"/>). A replaced folder is kept until the reload has
/// loaded the new one, and put back if it does not. A table that is running keeps the system it started with until it ends; a
/// campaign whose system is removed is read-only until it is installed again.</item>
/// </list>
/// </summary>
public sealed class SystemInstaller
{
    public const string GmInstallKey = "Codex:Systems:GmInstall";
    public const string InstallDirectoryKey = "Codex:Systems:InstallDirectory";
    private const string StagingFolder = ".staging";
    private const string TrashFolder = ".trash";
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(30);

    private readonly PluginLoader _loader;
    private readonly ICampaignRepository _campaigns;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemInstaller> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (SystemInstallPlan Plan, string UserId, DateTime Expires)> _plans = new(StringComparer.Ordinal);

    public SystemInstaller(PluginLoader loader, ICampaignRepository campaigns, IConfiguration configuration, ILogger<SystemInstaller> logger, string root)
    {
        _loader = loader;
        _campaigns = campaigns;
        _configuration = configuration;
        _logger = logger;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>The folder installed systems live in, one folder each.</summary>
    public string Root { get; }

    /// <summary>The install folder <c>Codex:Systems:InstallDirectory</c> names (relative to the content root), or <c>Systems</c>
    /// beside the database's folder.</summary>
    public static string ResolveRoot(IConfiguration configuration, DataFolders folders, string contentRoot) =>
        configuration[InstallDirectoryKey] is { Length: > 0 } configured
            ? Path.GetFullPath(configured, contentRoot)
            : Path.Combine(Path.GetDirectoryName(folders.Data) ?? folders.Data, "Systems");

    /// <summary>Makes <see cref="Root"/> and clears what an earlier run left in its staging and trash folders.</summary>
    public void Prepare()
    {
        Directory.CreateDirectory(Root);
        foreach (var leftover in new[] { StagingFolder, TrashFolder }.Select(f => Path.Combine(Root, f)).Where(Directory.Exists))
        {
            TryDelete(leftover);
        }
    }

    public bool MayInstall(ClaimsPrincipal user) =>
        user.IsInRole(ExposurePolicy.ServerAdminRole)
        || (user.Identity?.IsAuthenticated == true && _configuration.GetValue<bool>(GmInstallKey) && !ExposurePolicy.IsExposed(_configuration));

    public bool MayReload(ClaimsPrincipal user) => user.IsInRole(ExposurePolicy.ServerAdminRole);

    /// <summary>Whether the loaded system came from <see cref="Root"/>, so the app may replace or remove it.</summary>
    public bool IsInstalled(string systemId) => _loader.GetSource(systemId) is { } source && IsUnderRoot(source);

    /// <summary>Unpacks and checks an upload, for the page to show before anything is installed.</summary>
    public async Task<SystemInstallPlan?> CheckAsync(Stream zip, ClaimsPrincipal user)
    {
        if (!MayInstall(user) || UserId(user) is not { } userId) return null;
        Expire();
        var token = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(Root, StagingFolder, token);
        var check = await SystemArchive.CheckAsync(zip, staging, _loader.ScriptsAllowed);

        string? replaces = null;
        string? conflict = null;
        IReadOnlyList<SystemChange> changes = Array.Empty<SystemChange>();
        var using_ = 0;
        if (check.Id is { } id && _loader.LoadedSystemIds.Contains(id))
        {
            if (!IsInstalled(id))
            {
                conflict = $"'{id}' is a system this server has outside the app's systems folder (shipped with the app, or copied there by hand), so it can only be changed on the server.";
            }

            replaces = _loader.GetFingerprint(id)?.Version;
            if (_loader.GetFingerprint(id) is { } installed && check.Fingerprint != null) changes = check.Fingerprint.ChangesSince(installed);
            using_ = await CampaignsUsingAsync(id);
        }

        var plan = new SystemInstallPlan(token, check, replaces, changes, using_, conflict);
        _plans[token] = (plan, userId, DateTime.UtcNow + PlanLifetime);
        return plan;
    }

    /// <summary>Installs a checked upload that the same person sent, replacing what is installed under its id.</summary>
    public async Task<SystemInstallResult> InstallAsync(string token, ClaimsPrincipal user)
    {
        if (!MayInstall(user)) return SystemInstallResult.Fail("You may not install systems on this server.");
        if (!_plans.TryRemove(token, out var held) || held.UserId != UserId(user) || held.Expires < DateTime.UtcNow)
        {
            return SystemInstallResult.Fail("That upload has expired. Choose the file again.");
        }

        var plan = held.Plan;
        if (!plan.CanInstall) return SystemInstallResult.Fail(plan.Conflict ?? string.Join(" ", plan.Check.Errors));
        var id = plan.Check.Id!;

        await _gate.WaitAsync();
        try
        {
            // Checked again under the gate: another install or a reload may have changed what is loaded since the check.
            if (_loader.LoadedSystemIds.Contains(id) && !IsInstalled(id))
            {
                return SystemInstallResult.Fail($"'{id}' is a system this server has outside the app's systems folder, so it can only be changed on the server.");
            }

            var target = TargetFolder(id);
            var trash = Directory.Exists(target) ? Path.Combine(Root, TrashFolder, $"{Path.GetFileName(target)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}") : null;
            if (trash != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
                Directory.Move(target, trash);
            }

            Directory.Move(plan.Check.Folder, target);
            var reload = await _loader.ReloadAsync();
            if (reload.Error != null || !_loader.LoadedSystemIds.Contains(id))
            {
                // Put back what was there: the server stays as it was.
                TryDelete(target);
                if (trash != null) Directory.Move(trash, target);
                await _loader.ReloadAsync();
                var why = reload.Error ?? string.Join(" ", _loader.FailedSystems.Where(f => f.Id == id).SelectMany(f => f.Errors).DefaultIfEmpty("it did not load."));
                return SystemInstallResult.Fail($"The system was not installed: {why}");
            }

            if (trash != null) TryDelete(trash);
            _logger.LogWarning("System {SystemId} {Version} installed into {Folder} by {User}", id, plan.Check.Version, target, user.Identity?.Name);
            return new SystemInstallResult(true, null, reload);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops a checked upload that will not be installed.</summary>
    public void Discard(string token)
    {
        if (_plans.TryRemove(token, out var held)) TryDelete(held.Plan.Check.Folder);
    }

    /// <summary>How many campaigns, anyone's, play under <paramref name="systemId"/>: what removing it would make read-only.</summary>
    public async Task<int> CampaignsUsingAsync(string systemId) =>
        (await _campaigns.GetAllAsync()).Count(c => string.Equals(c.SystemId, systemId, StringComparison.Ordinal));

    /// <summary>Removes an installed system. Its campaigns become read-only until it is installed again; a running table keeps it
    /// until it ends.</summary>
    public async Task<SystemInstallResult> RemoveAsync(string systemId, ClaimsPrincipal user)
    {
        if (!MayInstall(user)) return SystemInstallResult.Fail("You may not remove systems on this server.");
        await _gate.WaitAsync();
        try
        {
            if (!IsInstalled(systemId)) return SystemInstallResult.Fail($"'{systemId}' was not installed from the app, so it can only be removed on the server.");
            var folder = Path.GetDirectoryName(_loader.GetSource(systemId)!)!;
            var trash = Path.Combine(Root, TrashFolder, $"{Path.GetFileName(folder)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            Directory.Move(folder, trash);
            var reload = await _loader.ReloadAsync();
            if (reload.Error != null)
            {
                Directory.Move(trash, folder);
                await _loader.ReloadAsync();
                return SystemInstallResult.Fail($"The system was not removed: {reload.Error}");
            }

            TryDelete(trash);
            _logger.LogWarning("System {SystemId} removed from {Folder} by {User}", systemId, folder, user.Identity?.Name);
            return new SystemInstallResult(true, null, reload);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads every content root again: for a system the admin changed on the server's disk (a DLL replaced by hand).</summary>
    public async Task<SystemInstallResult> ReloadAsync(ClaimsPrincipal user)
    {
        if (!MayReload(user)) return SystemInstallResult.Fail("Only the server's admin reloads its systems.");
        await _gate.WaitAsync();
        try
        {
            var reload = await _loader.ReloadAsync();
            return reload.Error == null ? new SystemInstallResult(true, null, reload) : SystemInstallResult.Fail(reload.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Where a system goes: the folder it is installed in, or a new one named after its id (with a number after it when
    /// that name holds something else, such as a system that failed to load under another id).</summary>
    private string TargetFolder(string id)
    {
        if (_loader.GetSource(id) is { } source && IsUnderRoot(source)) return Path.GetDirectoryName(source)!;
        var folder = Path.Combine(Root, id);
        for (var n = 2; Directory.Exists(folder) && !Holds(folder, id); n++) folder = Path.Combine(Root, $"{id}-{n}");
        return folder;

        static bool Holds(string folder, string id) =>
            PluginLoader.DeclaredId(Path.Combine(folder, SystemDefinitionLoader.FileName)) == id;
    }

    private bool IsUnderRoot(string path) =>
        Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? UserId(ClaimsPrincipal user) => user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private void Expire()
    {
        foreach (var (token, held) in _plans)
        {
            if (held.Expires < DateTime.UtcNow) Discard(token);
        }
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Folder}; it is cleared at the next start", folder);
        }
    }
}
