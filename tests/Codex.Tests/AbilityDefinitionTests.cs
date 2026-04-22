using System.Collections.Generic;
using System.Linq;
using Codex.Core.Models;
using Codex.Plugin.Abstractions;
using Xunit;

namespace Codex.Tests;

public class AbilityDefinitionTests
{
    [Fact]
    public void MergeFrom_Should_MergeTriggers()
    {
        var baseAbility = new AbilityDefinition
        {
            Name = "Base Name",
            Triggers = new List<TypedComponent> { new TypedComponent("BaseTrigger") }
        };

        var derivedAbility = new AbilityDefinition
        {
            Triggers = new List<TypedComponent> { new TypedComponent("DerivedTrigger") }
        };

        derivedAbility.MergeFrom(baseAbility);

        Assert.Equal("Base Name", derivedAbility.Name);
        Assert.Equal(2, derivedAbility.Triggers.Count);
        Assert.Contains(derivedAbility.Triggers, t => t.Type == "BaseTrigger");
        Assert.Contains(derivedAbility.Triggers, t => t.Type == "DerivedTrigger");
    }

    [Fact]
    public void MergeFrom_Should_MergeRequires()
    {
        var baseAbility = new AbilityDefinition
        {
            Requires = new List<TypedComponent> { new TypedComponent("BaseRequirement") }
        };

        var derivedAbility = new AbilityDefinition
        {
            Requires = new List<TypedComponent> { new TypedComponent("DerivedRequirement") }
        };

        derivedAbility.MergeFrom(baseAbility);

        Assert.Equal(2, derivedAbility.Requires.Count);
        Assert.Contains(derivedAbility.Requires, r => r.Type == "BaseRequirement");
        Assert.Contains(derivedAbility.Requires, r => r.Type == "DerivedRequirement");
    }

    [Fact]
    public void MergeFrom_Should_MergeCosts()
    {
        var baseAbility = new AbilityDefinition
        {
            Costs = new List<TypedComponent> { new TypedComponent("BaseCost") }
        };

        var derivedAbility = new AbilityDefinition
        {
            Costs = new List<TypedComponent> { new TypedComponent("DerivedCost") }
        };

        derivedAbility.MergeFrom(baseAbility);

        Assert.Equal(2, derivedAbility.Costs.Count);
        Assert.Contains(derivedAbility.Costs, c => c.Type == "BaseCost");
        Assert.Contains(derivedAbility.Costs, c => c.Type == "DerivedCost");
    }

    [Fact]
    public void MergeFrom_Should_MergeEffects()
    {
        var baseAbility = new AbilityDefinition
        {
            Effects = new List<TypedComponent> { new TypedComponent("BaseEffect") }
        };

        var derivedAbility = new AbilityDefinition
        {
            Effects = new List<TypedComponent> { new TypedComponent("DerivedEffect") }
        };

        derivedAbility.MergeFrom(baseAbility);

        Assert.Equal(2, derivedAbility.Effects.Count);
        Assert.Contains(derivedAbility.Effects, e => e.Type == "BaseEffect");
        Assert.Contains(derivedAbility.Effects, e => e.Type == "DerivedEffect");
    }

    [Fact]
    public void MergeFrom_Should_HandleNullsInDerived()
    {
        var baseAbility = new AbilityDefinition
        {
            Triggers = new List<TypedComponent> { new TypedComponent("BaseTrigger") },
            Requires = new List<TypedComponent> { new TypedComponent("BaseRequirement") },
            Costs = new List<TypedComponent> { new TypedComponent("BaseCost") },
            Effects = new List<TypedComponent> { new TypedComponent("BaseEffect") }
        };

        var derivedAbility = new AbilityDefinition();

        derivedAbility.MergeFrom(baseAbility);

        Assert.NotNull(derivedAbility.Triggers);
        Assert.Single(derivedAbility.Triggers);
        Assert.Equal("BaseTrigger", derivedAbility.Triggers[0].Type);

        Assert.NotNull(derivedAbility.Requires);
        Assert.Single(derivedAbility.Requires);
        Assert.Equal("BaseRequirement", derivedAbility.Requires[0].Type);

        Assert.NotNull(derivedAbility.Costs);
        Assert.Single(derivedAbility.Costs);
        Assert.Equal("BaseCost", derivedAbility.Costs[0].Type);

        Assert.NotNull(derivedAbility.Effects);
        Assert.Single(derivedAbility.Effects);
        Assert.Equal("BaseEffect", derivedAbility.Effects[0].Type);
    }

    [Fact]
    public void Serialization_Should_HandleTRCE()
    {
        var yaml = @"
id: fireball
name: Fireball
triggers:
  - type: Action
    params:
      Cost: 1
requires:
  - type: HasResource
    params:
      Resource: Mana
      Amount: 10
costs:
  - type: ConsumeResource
    params:
      Resource: Mana
      Amount: 10
effects:
  - type: Damage
    params:
      Amount: 20
";

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();

        var ability = deserializer.Deserialize<AbilityDefinition>(yaml);

        Assert.Equal("Fireball", ability.Name);

        Assert.NotNull(ability.Triggers);
        Assert.Single(ability.Triggers);
        Assert.Equal("Action", ability.Triggers[0].Type);
        Assert.Equal("1", ability.Triggers[0].Params["Cost"]); // YamlDotNet deserializes unquoted numbers as strings when type is object sometimes

        Assert.NotNull(ability.Requires);
        Assert.Single(ability.Requires);
        Assert.Equal("HasResource", ability.Requires[0].Type);
        Assert.Equal("Mana", ability.Requires[0].Params["Resource"]);

        Assert.NotNull(ability.Costs);
        Assert.Single(ability.Costs);
        Assert.Equal("ConsumeResource", ability.Costs[0].Type);
        Assert.Equal("Mana", ability.Costs[0].Params["Resource"]);

        Assert.NotNull(ability.Effects);
        Assert.Single(ability.Effects);
        Assert.Equal("Damage", ability.Effects[0].Type);
    }
}
