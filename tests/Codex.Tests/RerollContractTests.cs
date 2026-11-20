using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The generic <c>rerolls:</c> contract (system level), on an invented system: what the loader reads and what it refuses.</summary>
public class RerollContractTests
{
    private static SystemDefinition Invented(string rerolls) => SystemDefinitionLoader.Parse($$"""
        id: reroll-test
        name: Reroll test
        rerolls: {{rerolls}}
        sheetKinds:
          - kind: hero
            tracks:
              - { key: luck, label: Luck, max: 3 }
              - { key: spellSlots, label: Slots, display: slots }
        """);

    [Fact]
    public void TheLoader_ReadsARerollWithItsDefaults()
    {
        var reroll = Assert.Single(Invented("[{ key: lucky, label: Lucky reroll, spend: { track: luck } }]").Rerolls);

        Assert.Equal("lucky", reroll.Key);
        Assert.Equal("Lucky reroll", reroll.Label);
        Assert.Equal("luck", reroll.Spend!.Track);
        Assert.Equal(1, reroll.Spend.By);
        Assert.False(reroll.KeepsHigher);
    }

    [Fact]
    public void TheLoader_ReadsAnAmountAndKeepHigher()
    {
        var reroll = Assert.Single(Invented("[{ key: lucky, spend: { track: luck, by: 2 }, keep: higher }]").Rerolls);

        Assert.Equal(2, reroll.Spend!.By);
        Assert.True(reroll.KeepsHigher);
    }

    [Fact]
    public void ASystemWithNoRerolls_OffersNone()
    {
        Assert.Empty(Invented("[]").Rerolls);
    }

    [Fact]
    public void TheLoader_RefusesAnUnknownTrackWithAClearMessage()
    {
        var errors = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Invented("[{ key: lucky, spend: { track: fortune } }]")));

        Assert.Contains(errors, e => e.Contains("rerolls (lucky)") && e.Contains("'fortune' is not a track of any sheet kind"));
    }

    [Fact]
    public void TheLoader_RefusesASlotTrack()
    {
        var errors = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Invented("[{ key: lucky, spend: { track: spellSlots } }]")));

        Assert.Contains(errors, e => e.Contains("slot track"));
    }

    [Theory]
    [InlineData("[{ key: lucky }]", "needs a spend")]
    [InlineData("[{ label: No key, spend: { track: luck } }]", "needs a key")]
    [InlineData("[{ key: a, spend: { track: luck } }, { key: a, spend: { track: luck } }]", "used twice")]
    [InlineData("[{ key: a, spend: { track: luck, by: 0 } }]", "at least 1")]
    [InlineData("[{ key: a, spend: { track: luck }, keep: best }]", "new or higher")]
    public void TheLoader_RefusesABadReroll(string rerolls, string message)
    {
        var errors = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Invented(rerolls)));

        Assert.Contains(errors, e => e.Contains(message));
    }
}
