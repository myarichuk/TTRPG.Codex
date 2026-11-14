using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using static Codex.Tests.Components.FitdContent;

namespace Codex.Tests.Components;

/// <summary>The roll-bonus tray on a creature's card at the table (docs/ROLL_BONUSES_DESIGN.md): drawn by <see cref="TableCard"/> for a fifth-edition
/// sheet with a feature that offers tick boxes, with the real system and packs. Only the card's own parameters and the content registry are needed.</summary>
public class TableCardTests : ComponentTestBase, IClassFixture<FitdContent>
{
    private readonly FitdContent content;

    public TableCardTests(FitdContent content)
    {
        this.content = content;
        Services.AddSingleton<IContentRegistry>(content.Registry);
    }

    private (SheetRuleEngine Engine, ActorDocument Actor, SheetEvaluation Evaluation) Wizard(string feature, params Dictionary<string, object>[] modifiers)
    {
        var entry = new ContentEntry { Type = "subclass", Id = "tester", Name = "Tester", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "wizard";
        entry.Properties["features"] = new List<object>
        {
            new Dictionary<string, object> { ["name"] = feature, ["level"] = 3L, ["description"] = feature + ".", ["modifiers"] = modifiers.Cast<object>().ToList() }
        };
        var engine = new SheetRuleEngine(content.SystemById("srd5e"), new WithHomebrew(content.Registry, entry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "wizard";
        sheet["subclass"] = "tester";
        sheet["species"] = "human";
        sheet["level"] = 5L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var attribute in new[] { "str", "dex", "con", "wis", "cha", "int" }) sheet[SheetRuleEngine.BasePrefix + attribute] = 10L;
        sheet["inventory"] = new List<object> { "longbow|equipped:1", "longsword|equipped:1" };
        return (engine, Actor("wiz", "Tess", sheet), engine.Recompute(sheet));
    }

    private static Dictionary<string, object> OptIn(string target, long amount, string label) => new()
    {
        ["target"] = target,
        ["amount"] = amount,
        ["optIn"] = new Dictionary<string, object> { ["label"] = label }
    };

    private IRenderedComponent<TableCard> Draw((SheetRuleEngine Engine, ActorDocument Actor, SheetEvaluation Evaluation) wizard, bool canAct = true)
        => Render<TableCard>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "camp-1", SystemId = "srd5e" })
            .Add(x => x.Actor, wizard.Actor)
            .Add(x => x.Definition, content.SystemById("srd5e"))
            .Add(x => x.Evaluation, wizard.Evaluation)
            .Add(x => x.Engine, wizard.Engine)
            .Add(x => x.Full, true)
            .Add(x => x.Expanded, true)
            .Add(x => x.CanAct, canAct)
            .Add(x => x.Send, _ => Task.CompletedTask));

    [Fact]
    public void TheCard_OffersOnlyTheBoxesItsAttackAndDamageRollsCanTake()
    {
        var cut = Draw(Wizard("Tricks", OptIn("check", 1, "Lucky"), OptIn("attack.weapon.ranged", 1, "Steady"), OptIn("damage", 1, "Brutal")));

        var tray = cut.Find(Id("card-roll-bonuses"));
        Assert.Equal("fieldset", tray.TagName.ToLowerInvariant());
        var labels = tray.QuerySelectorAll("label").Select(l => l.TextContent).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, l => l.Contains("Steady") && l.Contains("on ranged weapon attack rolls"));
        Assert.Contains(labels, l => l.Contains("Brutal"));
        Assert.DoesNotContain(labels, l => l.Contains("Lucky"));
        foreach (var box in tray.QuerySelectorAll("input[type=checkbox]")) Assert.NotNull(cut.Find($"label[for='{box.Id}']"));
    }

    [Fact]
    public void ATickBox_CanBeTicked_AndShowsChecked()
    {
        var cut = Draw(Wizard("Tricks", OptIn("damage", 1, "Brutal")));
        var box = cut.Find(Id("card-roll-bonuses")).QuerySelector("input[type=checkbox]")!;
        Assert.False(box.HasAttribute("checked"));

        box.Change(true);

        Assert.True(cut.Find(Id("card-roll-bonuses")).QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));
    }

    [Fact]
    public void ACardTheViewerCannotActOn_HasNoTray()
    {
        var cut = Draw(Wizard("Tricks", OptIn("damage", 1, "Brutal")), canAct: false);

        Assert.Empty(cut.FindAll(Id("card-roll-bonuses")));
    }

    [Fact]
    public void ACardWithOnlyBoxesNoCardRollTakes_HasNoTray()
    {
        var cut = Draw(Wizard("Tricks", OptIn("check", 1, "Lucky")));

        Assert.Empty(cut.FindAll(Id("card-roll-bonuses")));
    }

    [Fact]
    public void InRussian_ABoxThatIsAFeaturesName_IsShownTranslated()
    {
        Services.AddSingleton(Localizer.For("ru"));
        var cut = Draw(Wizard("Sacred Weapon", OptIn("damage", 1, "Sacred Weapon")));

        var label = cut.Find(Id("card-roll-bonuses")).QuerySelector("label")!;
        Assert.Contains("Священное оружие", label.TextContent);
        Assert.DoesNotContain("Sacred Weapon", label.TextContent);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
