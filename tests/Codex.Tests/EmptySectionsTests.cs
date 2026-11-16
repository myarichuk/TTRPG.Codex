using Codex.Core.Systems;

namespace Codex.Tests;

/// <summary>A section written with nothing under it (<c>derived:</c>) means "none", not a crash while the system loads.</summary>
public class EmptySectionsTests
{
    [Theory]
    [InlineData("derived:")]
    [InlineData("lists:")]
    [InlineData("ranked:")]
    [InlineData("features:")]
    [InlineData("actions:")]
    public void AnEmptySheetRulesSection_LoadsAsEmpty(string section)
    {
        var system = SystemDefinitionLoader.Parse($$"""
            id: bare
            name: Bare
            attributes:
              - { key: str, label: Strength, default: 10 }
            sheetRules:
              {{section}}
            """);
        var rules = system.SheetRules!;
        Assert.Empty(rules.Derived);
        Assert.Empty(rules.Lists);
        Assert.Empty(rules.Ranked);
        Assert.Empty(rules.Features);
        Assert.Empty(rules.Actions);
    }

    [Fact]
    public void AnEmptyTopLevelSection_LoadsAsEmpty()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: bare
            name: Bare
            attributes:
            actorFields:
            sheetRules:
              enforcement: warn
            """);
        Assert.Empty(system.Attributes);
        Assert.Empty(system.ActorFields);
    }
}
