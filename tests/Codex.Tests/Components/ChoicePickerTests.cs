using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Localization;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Components;

/// <summary>The choices a feature leaves to the player, drawn by <see cref="ChoicePicker"/> and <see cref="ChoicesBlock"/>: the feature and class names and the
/// options' names are in the reader's language (the packs' and the system's translations), English is unchanged, and the picks raised are still the keys.</summary>
public class ChoicePickerTests : ComponentTestBase, IAsyncLifetime
{
    private readonly ContentRegistry registry = new();
    private SystemDefinition system = null!;
    private ContentEntry rogue = null!;

    public async Task InitializeAsync()
    {
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        system = loader.GetDefinition("srd5e")!;
        rogue = registry.GetEntries("srd5e", "class").First(e => e.Id == "rogue");
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    private static readonly ChoiceOption[] Options = { new("fighter", "Fighter"), new("wizard", "Wizard") };

    /// <summary>The Rogue's Expertise, picking among two options named after classes (the plain checkbox branch).</summary>
    private PendingChoice Expertise(params string[] made) => new("choice-key", "Expertise", "Rogue", 1, made, Options, null, "skills", false, false,
        new ActiveFeature("Expertise", null, rogue, 1, new Dictionary<string, object>(), null, new List<FeatureModifier>(), new List<FeatureAdd>(), null));

    private IRenderedComponent<ChoicePicker> Draw(string culture, PendingChoice choice, Action<IReadOnlyList<string>>? picked = null)
    {
        Services.AddSingleton(Localizer.For(culture));
        var names = RollBonusNames.For(registry, system, culture);
        return Render<ChoicePicker>(p => p.Add(x => x.Choice, choice).Add(x => x.Names, names)
            .Add(x => x.OnPicks, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<IReadOnlyList<string>>(this, picks => picked?.Invoke(picks))));
    }

    [Fact]
    public void InEnglish_TheLegendAndTheOptionsAreAsWritten()
    {
        var cut = Draw("en", Expertise());
        Assert.Contains("Expertise (Rogue, level 1)", cut.Find("legend").TextContent);
        Assert.Equal(new[] { "Fighter", "Wizard" }, cut.FindAll("label").Select(l => l.TextContent));
    }

    [Fact]
    public void InRussian_TheFeatureTheClassAndTheOptionsAreTranslated_AndThePicksAreStillKeys()
    {
        IReadOnlyList<string>? picks = null;
        var cut = Draw("ru", Expertise(), p => picks = p);
        var legend = cut.Find("legend").TextContent;
        Assert.Contains("Компетентность", legend);
        Assert.Contains("Плут", legend);
        Assert.DoesNotContain("Expertise", legend);
        Assert.DoesNotContain("Rogue", legend);
        Assert.Equal(new[] { "Воин", "Волшебник" }, cut.FindAll("label").Select(l => l.TextContent));

        cut.Find(Id("choice-pick-wizard")).Change(true);
        Assert.Equal(new[] { "wizard" }, picks);
    }

    [Fact]
    public void InHebrew_TheLegendAndTheOptionsAreTranslated()
    {
        var cut = Draw("he", Expertise());
        Assert.Matches("[֐-׿]", cut.Find("legend").TextContent);
        Assert.DoesNotContain("Rogue", cut.Find("legend").TextContent);
        Assert.All(cut.FindAll("label"), l => Assert.Matches("[֐-׿]", l.TextContent));
    }

    [Fact]
    public void TheMadeSummaryOfAChoicesBlock_IsInTheReadersLanguage()
    {
        Services.AddSingleton(Localizer.For("ru"));
        var choice = Expertise("wizard");
        var evaluation = new SheetEvaluation { MadeChoices = { choice } };
        var cut = Render<ChoicesBlock>(p => p.Add(x => x.Evaluation, evaluation).Add(x => x.Describe, v => v.Key)
            .Add(x => x.Names, RollBonusNames.For(registry, system, "ru")));
        var text = cut.Find(Id("choices-made")).TextContent;
        Assert.Contains("Компетентность (Плут)", text);
        Assert.Contains("Волшебник", text);
        Assert.DoesNotContain("Rogue", text);
    }
}
