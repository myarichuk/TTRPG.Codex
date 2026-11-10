using Codex.Core;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The content-root watcher (overhaul step 10, <c>Codex:Systems:Watch</c>): a change under a root reloads the systems once the
/// roots have been quiet, a burst of changes is one reload, and what the loader ignores (hidden folders, build output) is ignored.
/// </summary>
public sealed class SystemsWatcherTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codex-watch-" + Guid.NewGuid().ToString("N")[..8]);

    public SystemsWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var waited = 0; !condition(); waited += 50)
        {
            Assert.True(waited < 15000, $"timed out waiting until {what}");
            await Task.Delay(50);
        }
    }

    [Theory]
    [InlineData("street/system.yaml", true)]
    [InlineData("street/packs/core/callings/a.yaml", true)]
    [InlineData("Plugin.dll", true)]
    [InlineData(".staging/x/system.yaml", false)]
    [InlineData("street/.git/index", false)]
    [InlineData("tools/bin/Debug/system.yaml", false)]
    [InlineData("tools/obj/x.dll", false)]
    public void WhatMatters_IsWhatTheLoaderReads(string relative, bool matters) =>
        Assert.Equal(matters, SystemsWatcher.Matters(_root, Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public async Task ABurstOfChanges_IsOneReload_AfterTheRootsAreQuiet_AndHiddenFoldersAreIgnored()
    {
        var reloads = 0;
        using var watcher = new SystemsWatcher(new[] { _root }, () =>
        {
            Interlocked.Increment(ref reloads);
            return Task.CompletedTask;
        }, Quiet);

        Directory.CreateDirectory(Path.Combine(_root, ".staging"));
        File.WriteAllText(Path.Combine(_root, ".staging", "system.yaml"), "id: hidden\nname: Hidden\n");
        await Task.Delay(Quiet * 4);
        Assert.Equal(0, reloads);

        Directory.CreateDirectory(Path.Combine(_root, "street"));
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(_root, "street", $"file{i}.yaml"), "x: 1\n");
        await Until(() => Volatile.Read(ref reloads) == 1, "one reload ran");
        await Task.Delay(Quiet * 4);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task ASystemEditedOnDisk_IsReloaded_WithoutAnyonePressingReload()
    {
        var yaml = Path.Combine(_root, "street", "system.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(yaml)!);
        File.WriteAllText(yaml, "id: street\nname: Street\nversion: 1.0.0\n");
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
        await loader.LoadAndInitializeAsync(_root);
        using var watcher = new SystemsWatcher(loader.ContentRoots, async () => await loader.ReloadAsync(), Quiet);

        File.WriteAllText(yaml, "id: street\nname: Street\nversion: 1.1.0\n");

        await Until(() => loader.GetDefinition("street")?.Version == "1.1.0", "the edited system was reloaded");
        Assert.Equal(2, loader.Generation);
    }
}
