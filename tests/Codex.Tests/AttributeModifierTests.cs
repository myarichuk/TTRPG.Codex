using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Phase 7: the one place an attribute's modifier is worked out.</summary>
public class AttributeModifierTests
{
    [Theory]
    [InlineData(1, -5)]
    [InlineData(8, -1)]
    [InlineData(9, -1)]
    [InlineData(10, 0)]
    [InlineData(11, 0)]
    [InlineData(15, 2)]
    [InlineData(30, 10)]
    public void HalfMinusFive_RoundsDown(int score, int modifier)
    {
        Assert.Equal(modifier, AttributeDefinition.HalfMinusFive(score));
        Assert.Equal(modifier, new AttributeDefinition { Modifier = AttributeDefinition.HalfMinusFiveRule }.ModifierFor(score));
    }

    [Fact]
    public void AnAttributeWithoutARule_HasNoModifier() =>
        Assert.Null(new AttributeDefinition { Modifier = "none" }.ModifierFor(18));
}
