using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using NSubstitute;

namespace Codex.Tests;

/// <summary>A character made from a model's draft keeps the scores it gave and does not fall back to the point-buy floor for the rest.</summary>
public class NpcSheetTests
{
    private static CastService Cast()
    {
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetUISchemas(Arg.Any<string>()).Returns(Enumerable.Empty<UISchema>());
        // NewSheet touches neither the runtime, the registry, the repositories nor the component registry.
        return new CastService(null!, null!, null!, catalog, null!, null!);
    }

    private static SystemDefinition Srd() => SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems/srd5e", "system.yaml"));

    [Fact]
    public void NewSheet_WithADraft_TypesInItsScores_AndLeavesTheOthersAtTheirDefault()
    {
        var system = Srd();
        var actor = new ActorDocument();
        var draft = new CharacterDraft("Lord Nasher", new Dictionary<string, object> { ["str"] = 16L, ["cha"] = 14L }, null);

        Cast().NewSheet(actor, "srd5e", system, draft);

        Assert.Equal("manual", actor.Sheet[SheetRuleEngine.MethodKey]);
        Assert.Equal(16L, actor.Sheet["base_str"]);
        Assert.Equal(14L, actor.Sheet["base_cha"]);
        Assert.Equal(10L, actor.Sheet["base_dex"]); // not the point-buy floor of 8
        Assert.Equal(10L, actor.Sheet["dex"]);
    }

    [Fact]
    public void NewSheet_WithADraftThatGaveNoScores_StillLeavesACompleteSheet()
    {
        var system = Srd();
        var actor = new ActorDocument();

        Cast().NewSheet(actor, "srd5e", system, new CharacterDraft("Lord Nasher", new Dictionary<string, object>(), null));

        Assert.All(system.Attributes, a => Assert.Equal((long)a.Default, actor.Sheet["base_" + a.Key]));
        Assert.Equal("manual", actor.Sheet[SheetRuleEngine.MethodKey]);
    }

    [Fact]
    public void NewSheet_WithoutADraft_StartsAtPointBuy()
    {
        var actor = new ActorDocument();

        Cast().NewSheet(actor, "srd5e", Srd(), null);

        Assert.Equal("point-buy", actor.Sheet[SheetRuleEngine.MethodKey]);
        Assert.Equal(8L, actor.Sheet["base_str"]);
    }

    [Fact]
    public void ArmorClassLines_NameWhatTheyAreMadeOf_ForTheUiToTranslate()
    {
        var summary = new ArmorClassSummary("auto", true, 18, new[]
        {
            new ArmorClassLine("body", "Chain Mail", 16, new ContentEntry { Id = "chain-mail", Name = "Chain Mail" }),
            new ArmorClassLine("dex", "Dexterity", 0),
            new ArmorClassLine("bonus", "Shield", 2)
        });

        Assert.Equal(new[] { "Chain Mail: 16", "Dexterity: +0", "Shield: +2" }, summary.Lines);
        Assert.Equal("chain-mail", summary.Parts[0].Entry!.Id);
    }
}
