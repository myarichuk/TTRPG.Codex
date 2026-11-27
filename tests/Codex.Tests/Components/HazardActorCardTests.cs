using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>A complex hazard is an actor at the table (Hidden, linked to its placement), and its card is the GM's only. It has no creature numbers: a creature's sheet rules
/// would print an "Initiative +0" and an armor class from an empty sheet next to the hazard's real roll, so the card shows what is true of a hazard (its name and its hit points).</summary>
public class HazardActorCardTests : ComponentTestBase
{
    public HazardActorCardTests()
    {
        var registry = new ContentRegistry();
        Services.AddSingleton<IContentRegistry>(registry);
        Services.AddSingleton(new CampaignLibrary(registry, null!, null!));
    }

    private static SystemDefinition Srd() => SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    private static Dictionary<string, object> Sheet() => new() { ["initiative"] = 8L, ["armorClass"] = 15L };

    private IRenderedComponent<TableCard> Card(ActorDocument actor)
    {
        var system = Srd();
        var engine = new SheetRuleEngine(system, NullEntryResolver.Instance);
        return Render<TableCard>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "camp-1", SystemId = "srd5e" })
            .Add(x => x.Actor, actor)
            .Add(x => x.State, new ActorStateSnapshot(new Dictionary<string, int>(), Array.Empty<EffectSnapshot>()))
            .Add(x => x.Definition, system)
            .Add(x => x.Evaluation, engine.Recompute(actor.Sheet))
            .Add(x => x.Engine, engine)
            .Add(x => x.IsDm, true)
            .Add(x => x.Full, true));
    }

    [Fact]
    public void ACreaturesCard_StillPrintsItsNumbers()
    {
        var cut = Card(new ActorDocument { Id = "orc", Name = "Orc", Kind = ActorKind.Monster, Sheet = Sheet() });

        Assert.Contains("Initiative", cut.Find(Id("table-card-stats")).TextContent);
        Assert.Contains("+0", cut.Find(Id("table-card-stats")).TextContent);
        Assert.Contains("AC", cut.Find(Id("table-card-stats")).TextContent);
    }

    [Fact]
    public void AHazardsCard_KeepsItsName_AndPrintsItsOwnNumbers_NotACreaturesInitiative()
    {
        var link = new HazardActorLink { RegionId = "r", LocationId = "p", Entry = "hazard/rolling-stone" };
        var sheet = new Dictionary<string, object> { ["initiative"] = 8L, ["armorClass"] = 17L, ["hitPoints"] = 100L, ["damageThreshold"] = 10L, ["immunities"] = new List<object> { "poison" } };
        var cut = Card(new ActorDocument { Id = "stone", Name = "Rolling Stone", Kind = ActorKind.Monster, Visibility = ActorVisibility.Hidden, Hazard = link, Sheet = sheet });

        Assert.Equal("Rolling Stone", cut.Find(".table-card-name").TextContent.Trim());
        var stats = cut.Find(Id("table-card-stats")).TextContent;
        // The labels are the hazard type's own field labels ("Armor Class", "Damage Threshold").
        Assert.Contains("ArmorClass17", stats.Replace(" ", string.Empty));
        Assert.Contains("DamageThreshold10", stats.Replace(" ", string.Empty));
        Assert.Contains("poison", stats);
        Assert.Contains("+8", stats);
        Assert.DoesNotContain("+0", cut.Markup);
        Assert.DoesNotContain("AC", stats);
    }

    [Fact]
    public void AHazardWithNoNumbers_PrintsNoStats()
    {
        var link = new HazardActorLink { RegionId = "r", LocationId = "p", Entry = "hazard/rolling-stone" };
        var cut = Card(new ActorDocument { Id = "stone", Name = "Rolling Stone", Kind = ActorKind.Monster, Visibility = ActorVisibility.Hidden, Hazard = link, Sheet = new Dictionary<string, object>() });

        Assert.Empty(cut.FindAll(Id("table-card-stats")));
        Assert.DoesNotContain("+0", cut.Markup);
    }
}
