using Codex.Core;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Loads the real <c>content/</c> folder and checks the shipped ru/he translation packs.</summary>
public class ShippedTranslationPackTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
    private readonly PluginLoader _loader;

    public ShippedTranslationPackTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("ru", "Варвар")]
    [InlineData("he", "ברברי")]
    public void ClassNames_AreTranslated_WithoutChangingTheEntry(string locale, string barbarian)
    {
        var original = _registry.GetEntry("srd5e", "class", "barbarian")!;
        var shown = _registry.Localize(original, locale);

        Assert.Equal("Barbarian", original.Name);
        Assert.Equal(barbarian, shown.Name);
        Assert.Equal(original.Description, shown.Description);                 // rules text stays English until the SRD is translated
        Assert.Equal(original.Properties["hitDie"], shown.Properties["hitDie"]); // rules data untouched
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void TranslationPacks_NameOnlyEntriesThatExist_AndLoadCleanly(string locale)
    {
        var pack = _registry.GetPacks("srd5e").Single(p => p.Locale == locale);
        Assert.True(pack.IsTranslation);
        Assert.Equal("codex-flavor", pack.TranslationOf);

        var folder = Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "packs", pack.Id);
        var ids = Directory.GetFiles(folder, "*.yaml", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "manifest.yaml")
            .SelectMany(f => File.ReadLines(f).Where(l => l.StartsWith("- id: ")).Select(l => (Type: Path.GetFileName(Path.GetDirectoryName(f))!, Id: l[6..].Trim())))
            .ToList();

        Assert.Equal(69, ids.Count);
        Assert.All(ids, x => Assert.NotNull(_registry.GetEntry("srd5e", x.Type, x.Id)));
        Assert.Empty(_loader.LoadWarnings);
    }

    [Fact]
    public void SearchingInRussian_FindsTheClass_ByEitherName()
    {
        var byRussian = _registry.Search("srd5e", "варвар", type: "class", culture: "ru").Select(e => e.Id);
        var byEnglish = _registry.Search("srd5e", "barbarian", type: "class", culture: "ru").Select(e => e.Id);

        Assert.Contains("barbarian", byRussian);
        Assert.Contains("barbarian", byEnglish);
        Assert.Empty(_registry.Search("srd5e", "варвар", type: "class"));
    }
}
