using System.Reflection;
using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Codex.Core;

/// <summary>
/// Discovers game systems and content packs under one or more content roots and loads them once at
/// startup. A root may contain:
/// <list type="bullet">
/// <item>compiled plugins: <c>*.dll</c> directly in the root (code-level systems);</item>
/// <item>data systems: any <c>system.yaml</c> below the root (see <see cref="SystemDefinitionLoader"/>);</item>
/// <item>packs: any directory holding <c>manifest.yaml</c>/<c>manifest.json</c>, or a <c>*.zip</c>/<c>*.cdx</c> archive.</item>
/// </list>
/// Packs are loaded in dependency order and only for systems that actually loaded.
/// </summary>
public class PluginLoader(
    ILogger<PluginLoader> logger,
    ComponentRegistry registry,
    IContentPackLoader contentPackLoader) : ISystemCatalog
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    public bool IsLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public Exception? LoadException { get; private set; }
    public IReadOnlySet<string> LoadedSystemIds { get; private set; } = new HashSet<string>();

    /// <summary>Whether a system may name scripts (<c>Codex:Systems:Scripts</c>, overhaul section 4, rule 7). Off, a system
    /// that names one does not load, and says why: its rules would be missing a part, which is worse than not having it.</summary>
    public bool ScriptsAllowed { get; set; } = true;

    /// <summary>How many times the systems have been loaded: 1 after the start, one more for each <see cref="ReloadAsync"/>.</summary>
    public int Generation { get; private set; }

    /// <summary>The content roots the systems were loaded from, as given (missing ones included).</summary>
    public IReadOnlyList<string> ContentRoots => _configuredRoots;

    /// <summary>Problems found while loading that a GM should see (bad manifest, invalid system.yaml...), each with what
    /// it is about and the system it belongs to.</summary>
    public IReadOnlyList<LoadProblem> LoadProblems => _problems;

    /// <summary>The same problems as one line each.</summary>
    public IReadOnlyList<string> LoadWarnings => _problems.Select(p => p.Text).ToList();

    /// <summary>Systems that were found but did not load, with why.</summary>
    public IReadOnlyList<FailedSystem> FailedSystems => _failedSystems;

    /// <summary>The problems that belong to <paramref name="systemId"/>, and those that belong to no system.</summary>
    public IEnumerable<LoadProblem> ProblemsFor(string systemId) =>
        _problems.Where(p => p.SystemId == null || string.Equals(p.SystemId, systemId, StringComparison.OrdinalIgnoreCase));

    // What readers see is swapped whole at the end of a load (a reload builds the next set beside it): the lists are never
    // changed once published.
    private List<LoadProblem> _problems = new();
    private List<LoadProblem>? _building;
    private List<FailedSystem> _failedSystems = new();
    private List<string> _roots = new();
    private IReadOnlyList<string> _configuredRoots = Array.Empty<string>();
    private Dictionary<string, ICodexSystemPlugin> _pluginsBySystemId = new();
    private Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _buildingSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file a loaded system came from: its system.yaml, or a compiled plugin's DLL. Null when it is not loaded.</summary>
    public string? GetSource(string systemId) => _sources.GetValueOrDefault(systemId);

    /// <summary>Each plugin DLL loaded, by full path, with what it held, its hash then and the context it was loaded into. A reload
    /// that finds the file unchanged in the same context keeps its plugins; otherwise it loads them again.</summary>
    private Dictionary<string, (List<ICodexSystemPlugin> Plugins, string Hash, string Version, FolderLoadContext Context)> _loadedDlls = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (List<ICodexSystemPlugin> Plugins, string Hash, string Version, FolderLoadContext Context)>? _buildingDlls;

    public IEnumerable<SystemSummary> Systems => _pluginsBySystemId.Values
        .Select(p => new SystemSummary(p.SystemId, p.DisplayName, p.Definition?.Description, p.Definition?.Compatibility, p.Definition?.License, p is DataDrivenSystemPlugin,
            p is not DataDrivenSystemPlugin || (p.Definition != null && Rules.CompiledSuppliers.Names(p.Definition)), GetFingerprint(p.SystemId)?.Version))
        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string GetDisplayName(string systemId) =>
        _pluginsBySystemId.TryGetValue(systemId, out var plugin) ? plugin.DisplayName : systemId;

    public SystemDefinition? GetDefinition(string systemId) =>
        _pluginsBySystemId.TryGetValue(systemId, out var plugin) ? plugin.Definition : null;

    /// <summary>What the loaded system is (overhaul section 4, rule 8): a data system's version and file hashes, or a compiled
    /// plugin's assembly version and the hash of its DLL as it was loaded. Null for a system that is not loaded.</summary>
    public SystemFingerprint? GetFingerprint(string systemId)
    {
        if (!_pluginsBySystemId.TryGetValue(systemId, out var plugin)) return null;
        if (plugin is DataDrivenSystemPlugin) return plugin.Definition?.Fingerprint;
        foreach (var (path, loaded) in _loadedDlls)
        {
            if (loaded.Plugins.Contains(plugin))
            {
                return new SystemFingerprint(plugin.Definition?.Version ?? loaded.Version, new Dictionary<string, string> { [Path.GetFileName(path)] = loaded.Hash });
            }
        }

        return plugin.Definition?.Fingerprint;
    }

    public IEnumerable<UISchema> GetUISchemas(string systemId) =>
        _pluginsBySystemId.TryGetValue(systemId, out var plugin) ? plugin.GetUISchemas() : Enumerable.Empty<UISchema>();

    /// <summary>The loaded plugin ruling <paramref name="systemId"/>, or null if no such system is loaded.</summary>
    public ICodexSystemPlugin? GetPlugin(string systemId) => _pluginsBySystemId.GetValueOrDefault(systemId);

    public Task LoadAndInitializeAsync(string contentRoot) => LoadAndInitializeAsync(new[] { contentRoot });

    public async Task LoadAndInitializeAsync(IEnumerable<string> contentRoots)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (IsLoaded || IsLoading)
            {
                return;
            }

            IsLoading = true;
            _configuredRoots = contentRoots.ToList();
            try
            {
                await LoadCoreAsync(reload: false);
            }
            finally
            {
                IsLoaded = true;
                IsLoading = false;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Reads every system and content pack again from the same content roots (overhaul step 10), for a system installed,
    /// replaced or removed while the server runs. The new systems and packs are built beside the old ones and put in place in
    /// one step; until then, and if the reload fails, the app goes on with the old ones. A table already running keeps the
    /// definition it started with until it ends. A compiled plugin (<c>ICodexSystemPlugin</c>) whose DLL changed is loaded
    /// again, into a new load context (<see cref="FolderLoadContexts"/>); a table already running keeps the old plugin.
    /// </summary>
    public async Task<SystemsReload> ReloadAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!IsLoaded) throw new InvalidOperationException("The systems have not been loaded yet.");
            var before = _pluginsBySystemId.Keys.ToDictionary(id => id, GetFingerprint, StringComparer.Ordinal);
            IsLoading = true;
            try
            {
                if (await LoadCoreAsync(reload: true) is { } failed)
                {
                    return new SystemsReload(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), WithoutRoots(failed.Message));
                }
            }
            finally
            {
                IsLoading = false;
            }

            var after = _pluginsBySystemId.Keys.ToDictionary(id => id, GetFingerprint, StringComparer.Ordinal);
            return new SystemsReload(
                after.Keys.Where(id => !before.ContainsKey(id)).Order(StringComparer.Ordinal).ToList(),
                before.Keys.Where(id => !after.ContainsKey(id)).Order(StringComparer.Ordinal).ToList(),
                after.Where(a => before.TryGetValue(a.Key, out var was) && Changed(was, a.Value)).Select(a => a.Key).Order(StringComparer.Ordinal).ToList(),
                null);
        }
        finally
        {
            _semaphore.Release();
        }

        static bool Changed(SystemFingerprint? was, SystemFingerprint? now) =>
            was == null || now == null ? was != now : now.ChangesSince(was).Count > 0;
    }

    /// <summary>Loads everything into a new set and publishes it. On a reload that fails part-way nothing is published and the
    /// failure is returned; the first load publishes what it got, as it always has, with the failure as a problem.</summary>
    private async Task<Exception?> LoadCoreAsync(bool reload)
    {
        var problems = new List<LoadProblem>();
        var failed = new List<FailedSystem>();
        _building = problems;
        Exception? error = null;
        Dictionary<string, ICodexSystemPlugin>? plugins = null;
        var staging = contentPackLoader.Staging();
        _buildingSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _buildingDlls = new(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        try
        {
            roots = _configuredRoots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct().ToList();
            _roots = roots.OrderByDescending(r => r.Length).ToList();
            foreach (var missing in _configuredRoots.Where(r => !Directory.Exists(r)))
            {
                logger.LogWarning("Content root '{Root}' does not exist", missing);
            }

            try
            {
                var discovered = await Task.Run(() => DiscoverPlugins(roots, failed));

                // Core components every system relies on, registered once regardless of plugin.
                // Conditions (ActiveEffectsComponent) are stored too: their anchor is an actor id,
                // so a condition survives the table being ended and restarted.
                registry.Register<ResourcePoolComponent>();
                registry.Register<AttributesComponent>();
                registry.Register<StatModifierComponent>();
                registry.Register<TagsComponent>();
                registry.Register<ActiveEffectsComponent>();

                var found = discovered.ToDictionary(p => p.SystemId);
                plugins = found;
                await LoadContentPacksAsync(roots, found.Keys.ToHashSet(), staging, id => found.GetValueOrDefault(id)?.Definition);
            }
            catch (Exception ex)
            {
                error = ex;
                Add(new LoadProblem(LoadProblemSource.Loader, "loader", $"Loading systems and content packs stopped part-way: {ex.Message}"));
                logger.LogError(ex, "Plugin loading failed");
            }
        }
        finally
        {
            _building = null;
        }

        var dlls = _buildingDlls;
        _buildingDlls = null;
        if (reload && error != null) return error;

        // Registered once the set is certain: a replaced plugin's component types take their names over from the old ones, which
        // a reload that fails must not do. A table that is running keeps the instances it has.
        _building = problems;
        foreach (var plugin in plugins?.Values ?? Enumerable.Empty<ICodexSystemPlugin>())
        {
            try
            {
                logger.LogInformation("Initializing system: {SystemId} ({Name})", plugin.SystemId, plugin.DisplayName);
                plugin.RegisterComponents(registry);
            }
            catch (Exception ex)
            {
                Warn(new LoadProblem(LoadProblemSource.Plugin, plugin.SystemId, $"could not register its components: {ex.Message}", plugin.SystemId));
            }
        }

        _building = null;
        staging.Commit();
        _pluginsBySystemId = plugins ?? new Dictionary<string, ICodexSystemPlugin>();
        _sources = _buildingSources;
        _loadedDlls = dlls;
        // The load contexts of system folders and DLLs that are gone are unloaded once nothing they made is alive.
        FolderLoadContexts.RetireUnder(roots, roots.Concat(_sources.Values.Select(s => Path.GetDirectoryName(s)!)));
        LoadedSystemIds = _pluginsBySystemId.Keys.ToHashSet();
        _problems = problems;
        _failedSystems = failed;
        LoadException = error;
        Generation++;
        return null;
    }

    private List<ICodexSystemPlugin> DiscoverPlugins(IReadOnlyList<string> roots, List<FailedSystem> failedSystems)
    {
        var plugins = new List<ICodexSystemPlugin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var plugin in LoadPlugins(root))
            {
                if (!seen.Add(plugin.SystemId))
                {
                    Warn(new LoadProblem(LoadProblemSource.Plugin, plugin.SystemId, "is provided twice; the second copy was ignored.", plugin.SystemId, root));
                    continue;
                }

                plugins.Add(plugin);
                if ((_buildingDlls ?? _loadedDlls).FirstOrDefault(d => d.Value.Plugins.Contains(plugin)).Key is { } dll) _buildingSources[plugin.SystemId] = dll;
            }
        }

        foreach (var root in roots)
        {
            foreach (var systemFile in EnumerateFiles(root, SystemDefinitionLoader.FileName))
            {
                try
                {
                    var unknownKeys = new List<string>();
                    var definition = SystemDefinitionLoader.Load(systemFile, unknownKeys);
                    if (!seen.Add(definition.Id))
                    {
                        Warn(new LoadProblem(LoadProblemSource.System, definition.Id, "duplicates an already loaded system and was ignored.", definition.Id, systemFile));
                        continue;
                    }

                    if (!ScriptsAllowed && Rules.SystemScripts.FilesNamed(definition).ToList() is { Count: > 0 } scripts)
                    {
                        var stated = $"names scripts ({string.Join(", ", scripts)}), and this server has scripts turned off (Codex:Systems:Scripts).";
                        failedSystems.Add(new FailedSystem(definition.Id, definition.Name, new[] { stated }, systemFile));
                        Warn(new LoadProblem(LoadProblemSource.System, definition.Name, $"did not load: it {stated}", definition.Id, systemFile));
                        continue;
                    }

                    if (unknownKeys.Count > 0)
                    {
                        Warn(new LoadProblem(LoadProblemSource.System, definition.Id, UnknownKeysMessage(unknownKeys), definition.Id, systemFile));
                    }

                    foreach (var problem in ProblemsOf(definition))
                    {
                        Warn(new LoadProblem(LoadProblemSource.System, definition.Id, problem, definition.Id, systemFile));
                    }

                    logger.LogInformation("Loaded data-driven system {SystemId} from {File}", definition.Id, systemFile);
                    plugins.Add(new DataDrivenSystemPlugin(definition));
                    _buildingSources[definition.Id] = Path.GetFullPath(systemFile);
                }
                catch (Exception ex)
                {
                    var id = DeclaredId(systemFile);
                    var name = id ?? Path.GetFileName(Path.GetDirectoryName(systemFile)) ?? SystemDefinitionLoader.FileName;
                    var errors = SystemDefinitionLoader.ErrorsOf(ex);
                    failedSystems.Add(new FailedSystem(id, name, errors.Select(WithoutRoots).ToList(), systemFile));
                    Warn(new LoadProblem(LoadProblemSource.System, name, $"did not load: {string.Join(" ", errors)}", id, systemFile));
                    logger.LogError(ex, "Failed to load system definition {File}", systemFile);
                }
            }
        }

        return plugins;
    }

    internal Task LoadContentPacksAsync(string contentRoot, HashSet<string> activeSystemIds) =>
        LoadContentPacksAsync(new[] { contentRoot }, activeSystemIds);

    // Internal (rather than private) so tests can exercise dependency-ordered pack loading
    // directly, without needing a real plugin assembly on disk just to populate activeSystemIds.
    internal Task LoadContentPacksAsync(IEnumerable<string> contentRoots, IReadOnlySet<string> activeSystemIds) =>
        LoadContentPacksAsync(contentRoots, activeSystemIds, contentPackLoader, GetDefinition);

    private async Task LoadContentPacksAsync(IEnumerable<string> contentRoots, IReadOnlySet<string> activeSystemIds, IContentPackLoader packLoader, Func<string, SystemDefinition?> definitionOf)
    {
        var packPaths = new List<string>();
        foreach (var root in contentRoots.Where(Directory.Exists))
        {
            packPaths.AddRange(EnumerateFiles(root, YamlContentPackLoader.ManifestYaml).Select(Path.GetDirectoryName)!);
            packPaths.AddRange(EnumerateFiles(root, YamlContentPackLoader.ManifestJson).Select(Path.GetDirectoryName)!);
            packPaths.AddRange(EnumerateFiles(root, "*.zip"));
            packPaths.AddRange(EnumerateFiles(root, "*.cdx"));
        }

        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

        // Read every manifest up front so packs can be loaded in dependency order rather than
        // whatever order the filesystem happens to enumerate them in - a pack's base has to be
        // registered before anything that inherits from it, regardless of load order.
        var manifestsByPath = new Dictionary<string, PackManifest>();
        foreach (var packPath in packPaths.Where(p => p != null).Distinct())
        {
            try
            {
                manifestsByPath[packPath!] = await packLoader.ReadManifestAsync(packPath!);
            }
            catch (Exception ex)
            {
                Warn(new LoadProblem(LoadProblemSource.Pack, Path.GetFileName(packPath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    $"its manifest could not be read: {ex.Message}", Path: packPath));
                logger.LogError(ex, "Failed to read manifest for content pack at {Path}", packPath);
            }
        }

        foreach (var packPath in TopologicalOrder(manifestsByPath))
        {
            var manifest = manifestsByPath[packPath];

            if (!manifest.IsAppVersionCompatible(appVersion))
            {
                Warn(new LoadProblem(LoadProblemSource.Pack, manifest.Id, $"needs app version {manifest.MinAppVersion} (this is {appVersion}) and was skipped.", manifest.SystemId, packPath));
                continue;
            }

            if (!activeSystemIds.Contains(manifest.SystemId))
            {
                Warn(new LoadProblem(LoadProblemSource.Pack, manifest.Id, $"is for system '{manifest.SystemId}', which is not loaded, and was skipped.", manifest.SystemId, packPath));
                continue;
            }

            try
            {
                logger.LogInformation("Loading content pack: {PackName} ({PackId}) for system {SystemId}",
                    manifest.Name, manifest.Id, manifest.SystemId);
                await packLoader.LoadPackAsync(packPath, definitionOf(manifest.SystemId));
            }
            catch (Exception ex)
            {
                Warn(new LoadProblem(LoadProblemSource.Pack, manifest.Id, $"failed to load: {ex.Message}", manifest.SystemId, packPath));
                logger.LogError(ex, "Failed to load content pack from {Path}", packPath);
            }

            // Entries the pack loader skipped or loaded without what they inherit: already logged there.
            foreach (var problem in packLoader.TakeProblems())
            {
                Add(new LoadProblem(LoadProblemSource.Pack, manifest.Id, problem, manifest.SystemId, packPath));
            }
        }
    }

    /// <summary>
    /// Orders packs so every pack's <see cref="PackManifest.Dependencies"/> load before it does.
    /// A dependency that names a pack id not present among the discovered manifests is a load
    /// error naming the pack; that pack still loads, but its base content may be missing.
    /// </summary>
    private IEnumerable<string> TopologicalOrder(Dictionary<string, PackManifest> manifestsByPath)
    {
        var pathById = new Dictionary<string, string>();
        foreach (var (path, manifest) in manifestsByPath)
        {
            if (!pathById.TryAdd(manifest.Id, path))
            {
                Warn(new LoadProblem(LoadProblemSource.Pack, manifest.Id, "appears twice; both copies load and, entry by entry, the higher priority wins.", manifest.SystemId, path));
            }
        }

        var visited = new HashSet<string>();
        var result = new List<string>();

        foreach (var path in manifestsByPath.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            Visit(path, new HashSet<string>());
        }

        return result;

        void Visit(string path, HashSet<string> inProgress)
        {
            if (visited.Contains(path) || !inProgress.Add(path))
            {
                return;
            }

            var manifest = manifestsByPath[path];
            foreach (var dependencyId in manifest.Dependencies ?? Array.Empty<string>())
            {
                if (!pathById.TryGetValue(dependencyId, out var dependencyPath))
                {
                    Warn(new LoadProblem(LoadProblemSource.Pack, manifest.Id, $"depends on '{dependencyId}', which was not found. It still loads, but inherited content may be missing.", manifest.SystemId, path));
                    continue;
                }

                Visit(dependencyPath, inProgress);
            }

            visited.Add(path);
            result.Add(path);
        }
    }

    public List<ICodexSystemPlugin> LoadPlugins(string pluginsDirectory)
    {
        var plugins = new List<ICodexSystemPlugin>();

        if (!Directory.Exists(pluginsDirectory))
        {
            logger.LogWarning("Plugins directory '{PluginsDirectory}' does not exist", pluginsDirectory);
            return plugins;
        }

        // A folder with a system.yaml is a data system's: its DLLs are the system's compiled suppliers, which the system's own
        // load context loads (Rules.CompiledSuppliers), not plugins of the host's.
        if (File.Exists(Path.Combine(pluginsDirectory, SystemDefinitionLoader.FileName))) return plugins;

        var dlls = Directory.GetFiles(pluginsDirectory, "*.dll");
        if (dlls.Length == 0) return plugins;
        FolderLoadContext context;
        IDisposable hold;
        try
        {
            // The root's context for the DLLs it holds now (FolderLoadContexts), read from their bytes: a DLL replaced on disk is
            // loaded again by the next reload, into a new context, while a table that started before keeps the old plugin.
            hold = FolderLoadContexts.Hold(pluginsDirectory, out context);
        }
        catch (IOException ex)
        {
            Warn(new LoadProblem(LoadProblemSource.Plugin, Path.GetFileName(pluginsDirectory), $"plugins could not be read: {ex.Message}", Path: pluginsDirectory));
            return plugins;
        }

        using var held = hold;
        var loaded = _buildingDlls ?? _loadedDlls;
        foreach (var file in dlls)
        {
            var full = Path.GetFullPath(file);
            string hash;
            try
            {
                hash = SystemFingerprint.Hash(full);
            }
            catch (IOException ex)
            {
                Warn(new LoadProblem(LoadProblemSource.Plugin, Path.GetFileName(file), $"could not be read: {ex.Message}", Path: file));
                continue;
            }

            // The same file in the same context: the plugins it made before, so an unchanged plugin is the same object.
            if (_loadedDlls.TryGetValue(full, out var known) && known.Hash == hash && known.Context == context)
            {
                loaded[full] = known;
                plugins.AddRange(known.Plugins);
                continue;
            }

            try
            {
                var assembly = context.Main(Path.GetFileNameWithoutExtension(file), file);
                if (assembly == null) continue; // the host's own assembly, copied into a root: not a plugin
                var found = new List<ICodexSystemPlugin>();
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(ICodexSystemPlugin).IsAssignableFrom(t) &&
                                t is { IsInterface: false, IsAbstract: false } &&
                                t.GetConstructor(Type.EmptyTypes) != null);

                foreach (var type in pluginTypes)
                {
                    if (context.Create(type) is ICodexSystemPlugin plugin)
                    {
                        logger.LogInformation("Loaded plugin: {SystemId}", plugin.SystemId);
                        found.Add(plugin);
                    }
                }

                loaded[full] = (found, hash, assembly.GetName().Version?.ToString() ?? "1.0.0", context);
                plugins.AddRange(found);
            }
            catch (Exception ex)
            {
                // A compiled plugin that fails used to be only in the server log: the GM saw its system simply missing.
                Warn(new LoadProblem(LoadProblemSource.Plugin, Path.GetFileName(file), $"did not load: {ex.InnerException?.Message ?? ex.Message}", Path: file));
                logger.LogError(ex, "Failed to load plugin from {File}", file);
            }
        }

        return plugins;
    }

    public void InitializePlugins(IEnumerable<ICodexSystemPlugin> plugins, CodexWorld world)
    {
        foreach (var plugin in plugins)
        {
            logger.LogInformation("Initializing plugin: {SystemId}", plugin.SystemId);
            plugin.RegisterComponents(registry);
            plugin.RegisterSystems(world);
        }
    }

    /// <summary>What is wrong with a loaded definition that still lets it load: rolls, events, layout, campaign objects, scripts
    /// and compiled code that do not fit. The install check (<see cref="SystemArchive"/>) reports the same.</summary>
    public static IEnumerable<string> ProblemsOf(SystemDefinition definition) =>
        Rules.SheetRolls.Problems(definition).Concat(Rules.SheetEvents.Problems(definition)).Concat(Rules.SheetLayout.Problems(definition))
            .Concat(Rules.CampaignObjects.DefinitionProblems(definition)).Concat(Rules.SystemScripts.Problems(definition)).Concat(Rules.CompiledSuppliers.Problems(definition));

    /// <summary>How many unknown keys a load problem names before it only counts the rest.</summary>
    public const int UnknownKeysListed = 12;

    /// <summary>One sentence for all of a system's unknown keys: a misspelt key and a section a later version reads look the same
    /// from here, and either way the system loads without it.</summary>
    public static string UnknownKeysMessage(IReadOnlyList<string> keys)
    {
        var listed = string.Join(", ", keys.Take(UnknownKeysListed));
        var more = keys.Count > UnknownKeysListed ? $" and {keys.Count - UnknownKeysListed} more" : "";
        return $"has keys this version does not read, which are ignored (a misspelling, or a section a newer version understands): {listed}{more}.";
    }

    private void Warn(LoadProblem problem)
    {
        Add(problem);
        logger.LogWarning("{Problem} ({Path})", problem.Text, problem.Path);
    }

    /// <summary>Every problem goes through here: exception messages (.NET's, YamlDotNet's) carry full paths, and the message
    /// is shown to people who don't run the server, so the content roots are cut out and paths are left relative to them.</summary>
    private void Add(LoadProblem problem) => (_building ?? _problems).Add(problem with { Message = WithoutRoots(problem.Message) });

    private string WithoutRoots(string message)
    {
        foreach (var root in _roots)
        {
            message = message.Replace(root + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal)
                .Replace(root + Path.AltDirectorySeparatorChar, string.Empty, StringComparison.Ordinal)
                .Replace(root, ".", StringComparison.Ordinal);
        }

        return message;
    }

    /// <summary>The <c>id:</c> a system file states, read line by line so it works even when the YAML is broken.</summary>
    public static string? DeclaredId(string systemFile)
    {
        try
        {
            foreach (var line in File.ReadLines(systemFile))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^id:\s*[""']?([a-z0-9][a-z0-9_-]*)[""']?\s*(#.*)?$");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    /// <summary>Recursive file search that skips build output and hidden folders.</summary>
    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules")
                {
                    continue;
                }

                pending.Push(sub);
            }
        }
    }
}

/// <summary>What a <see cref="PluginLoader.ReloadAsync"/> did: the systems that appeared, went and changed (by fingerprint), or
/// why it did nothing.</summary>
public sealed record SystemsReload(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed, string? Error);
