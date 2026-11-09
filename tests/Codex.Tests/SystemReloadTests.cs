using Codex.Core;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Reload and fingerprints (overhaul step 10): a reload reads the content roots again and swaps systems and packs in one step;
/// what a table took before it keeps; a fingerprint says what a system's rules came from and what changed.
/// </summary>
public sealed class SystemReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codex-reload-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ContentRegistry _content = new();
    private readonly PluginLoader _loader;

    public SystemReloadTests()
    {
        Directory.CreateDirectory(_root);
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_content));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void System(string id, string version = "1.0.0", string extra = "") =>
        Write($"{id}/system.yaml", $"id: {id}\nname: {id} rules\nversion: {version}\nentityTypes:\n  - {{ type: calling, label: Calling, folder: callings }}\n{extra}");

    private void Pack(string system, string pack, params string[] entries)
    {
        Write($"{system}/packs/{pack}/manifest.yaml", $"id: {pack}\nname: {pack}\nversion: 1.0.0\nsystemId: {system}\n");
        Write($"{system}/packs/{pack}/callings/callings.yaml", string.Concat(entries.Select(e => $"- id: {e}\n  name: {e}\n")));
    }

    [Fact]
    public async Task Reload_FindsSystemsAddedRemovedAndChanged_AndSwapsTheirPacks()
    {
        System("alpha");
        Pack("alpha", "alpha-core", "brawler", "tinker");
        System("beta");
        await _loader.LoadAndInitializeAsync(_root);
        Assert.Equal(1, _loader.Generation);
        Assert.Equal(new[] { "brawler", "tinker" }, _content.GetEntries("alpha", "calling").Select(e => e.Id).Order());

        Directory.Delete(Path.Combine(_root, "beta"), recursive: true);
        System("gamma");
        System("alpha", version: "1.1.0");
        Pack("alpha", "alpha-core", "drifter");

        var reload = await _loader.ReloadAsync();

        Assert.Null(reload.Error);
        Assert.Equal(new[] { "gamma" }, reload.Added);
        Assert.Equal(new[] { "beta" }, reload.Removed);
        Assert.Equal(new[] { "alpha" }, reload.Changed);
        Assert.Equal(2, _loader.Generation);
        Assert.Equal(new[] { "alpha", "gamma" }, _loader.LoadedSystemIds.Order());
        Assert.Equal("1.1.0", _loader.Systems.Single(s => s.Id == "alpha").Version);
        Assert.Equal(new[] { "drifter" }, _content.GetEntries("alpha", "calling").Select(e => e.Id));
    }

    [Fact]
    public async Task Reload_WithNothingChanged_ReportsNothing_AndAProblemFixedOnDiskIsGone()
    {
        System("alpha", extra: "colour: blue\n");
        await _loader.LoadAndInitializeAsync(_root);
        Assert.Contains(_loader.LoadProblems, p => p.SystemId == "alpha" && p.Message.Contains("colour"));

        var same = await _loader.ReloadAsync();
        Assert.Empty(same.Added.Concat(same.Removed).Concat(same.Changed));

        System("alpha");
        var fixedOne = await _loader.ReloadAsync();
        Assert.Equal(new[] { "alpha" }, fixedOne.Changed);
        Assert.DoesNotContain(_loader.LoadProblems, p => p.SystemId == "alpha");
    }

    [Fact]
    public async Task ADefinitionTakenBeforeAReload_IsTheOneItWas()
    {
        // A live table holds the definition it started with (CampaignRuntime): a reload must not change it under the table.
        System("alpha", version: "1.0.0");
        await _loader.LoadAndInitializeAsync(_root);
        var running = _loader.GetDefinition("alpha")!;

        System("alpha", version: "2.0.0");
        await _loader.ReloadAsync();

        Assert.Equal("1.0.0", running.Version);
        Assert.Equal("1.0.0", running.Fingerprint!.Version);
        Assert.Equal("2.0.0", _loader.GetDefinition("alpha")!.Version);
        Assert.NotSame(running, _loader.GetDefinition("alpha"));
    }

    [Fact]
    public async Task ScriptsTurnedOff_ASystemNamingOneDoesNotLoad_AndSaysWhy()
    {
        System("scripted", extra: "functions:\n  twice: { script: rules.js#twice }\n");
        Write("scripted/rules.js", "export function twice(x) { return x * 2; }\n");
        System("plain");
        _loader.ScriptsAllowed = false;

        await _loader.LoadAndInitializeAsync(_root);

        Assert.Equal(new[] { "plain" }, _loader.LoadedSystemIds);
        var failed = Assert.Single(_loader.FailedSystems);
        Assert.Equal("scripted", failed.Id);
        Assert.Contains("rules.js", Assert.Single(failed.Errors));
        Assert.Contains("scripts turned off", failed.Errors[0]);

        _loader.ScriptsAllowed = true;
        var reload = await _loader.ReloadAsync();
        Assert.Equal(new[] { "scripted" }, reload.Added);
        Assert.Empty(_loader.FailedSystems);
    }

    [Fact]
    public void Fingerprint_HashesSystemYamlTranslationsScriptsAndDlls_IgnoringLineEndings()
    {
        System("alpha", extra: "functions:\n  twice: { script: rules.js#twice }\n");
        Write("alpha/rules.js", "export function twice(x) {\r\n  return x * 2;\r\n}\r\n");
        Write("alpha/system.ru.yaml", "name: альфа\n");
        File.WriteAllBytes(Path.Combine(_root, "alpha", "Helper.dll"), new byte[] { 1, 2, 3 });

        var fingerprint = SystemDefinitionLoader.Load(Path.Combine(_root, "alpha", "system.yaml")).Fingerprint!;

        Assert.Equal("1.0.0", fingerprint.Version);
        Assert.Equal(new[] { "Helper.dll", "rules.js", "system.ru.yaml", "system.yaml" }, fingerprint.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(SystemFingerprint.HashText("export function twice(x) {\n  return x * 2;\n}\n"), fingerprint.Files["rules.js"]);

        // The same text with Linux line endings is the same system.
        var yaml = File.ReadAllText(Path.Combine(_root, "alpha", "system.yaml"));
        File.WriteAllText(Path.Combine(_root, "alpha", "system.yaml"), yaml.Replace("\n", "\r\n"));
        Assert.Equal(fingerprint.Files["system.yaml"], SystemFingerprint.Hash(Path.Combine(_root, "alpha", "system.yaml")));
    }

    [Fact]
    public void ChangesSince_NamesTheVersionAndEachFileAddedRemovedOrChanged()
    {
        var before = new SystemFingerprint("1.0.0", new Dictionary<string, string> { ["system.yaml"] = "a", ["rules.js"] = "b", ["old.js"] = "c" });
        var after = new SystemFingerprint("1.1.0", new Dictionary<string, string> { ["system.yaml"] = "a", ["rules.js"] = "B", ["Street.dll"] = "d" });

        var changes = after.ChangesSince(before);

        Assert.Equal(new[]
        {
            new SystemChange(SystemChangeKind.Version, null, "1.0.0", "1.1.0"),
            new SystemChange(SystemChangeKind.Added, "Street.dll", null, "d"),
            new SystemChange(SystemChangeKind.Changed, "rules.js", "b", "B"),
            new SystemChange(SystemChangeKind.Removed, "old.js", "c", null),
        }, changes);
        Assert.Empty(after.ChangesSince(after));
    }
}
