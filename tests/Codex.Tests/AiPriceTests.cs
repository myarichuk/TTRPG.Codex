using System.Globalization;
using Codex.Core.AI;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Components.Campaign;

namespace Codex.Tests;

/// <summary>The app ships no price table: a cost exists only when the user typed the prices, and an unknown cost is never shown as 0.</summary>
public class AiPriceTests
{
    [Fact]
    public void Cost_IsTokensTimesThePerMillionPrice()
    {
        Assert.Equal(0.75m, AiPrice.Cost(0.15m, 0.6m, 1_000_000, 1_000_000));
        Assert.Equal(0.00075m, AiPrice.Cost(0.15m, 0.6m, 1_000, 1_000));
    }

    [Fact]
    public void CachedInput_IsPartOfTheInput_ReadsAtTheCachedPrice_WritesAtAQuarterMoreThanInput()
    {
        // 1M input of which 600k came from the cache and 100k went into it; $2 in, $0.10 cached, $10 out.
        var cost = AiPrice.Cost(2m, 10m, 0.10m, 1_000_000, 100_000, 600_000, 100_000);

        // 300k at $2, 600k at $0.10, 100k at $2.50, 100k out at $10.
        Assert.Equal(0.6m + 0.06m + 0.25m + 1m, cost);
    }

    [Fact]
    public void CachedInput_WithNoCachedPrice_CostsWhatItCostBeforeCaching_AndNeverLess()
    {
        Assert.Equal(AiPrice.Cost(2m, 10m, 1_000_000, 0), AiPrice.Cost(2m, 10m, null, 1_000_000, 0, 600_000, 0));
        // Counts that cannot be true (more cached than sent) are held to what was sent.
        Assert.Equal(AiPrice.Cost(2m, 10m, 0.5m, 1_000, 0, 1_000, 0), AiPrice.Cost(2m, 10m, 0.5m, 1_000, 0, 9_999_999, 0));
        Assert.Null(AiPrice.Cost(2m, 10m, 0.1m, null, 5, 1, 1));
    }

    [Fact]
    public void ATurnsCost_UsesItsCacheNumbersAndTheCachedPriceItRanUnder()
    {
        var turn = new WorldChatUsage
        {
            InputTokens = 1_000_000, OutputTokens = 0, CacheReadTokens = 900_000, InputPricePerMTok = 2m, OutputPricePerMTok = 10m, CachedInputPricePerMTok = 0.1m
        };

        Assert.Equal(0.2m + 0.09m, WorldChatView.Cost(turn));
        // The same turn from before there was a cache: all of it at the input price.
        Assert.Equal(2m, WorldChatView.Cost(new WorldChatUsage { InputTokens = 1_000_000, OutputTokens = 0, InputPricePerMTok = 2m, OutputPricePerMTok = 10m }));
    }

    [Theory]
    [InlineData(null, 1.0, 10L, 10L)]
    [InlineData(1.0, null, 10L, 10L)]
    [InlineData(-1.0, 1.0, 10L, 10L)]
    [InlineData(1.0, 1.0, null, 10L)]
    [InlineData(1.0, 1.0, 10L, null)]
    [InlineData(100001.0, 1.0, 10L, 10L)]
    public void Cost_IsUnknown_NotZero_WhenAnythingIsMissingOrAbsurd(double? input, double? output, long? tokensIn, long? tokensOut) =>
        Assert.Null(AiPrice.Cost((decimal?)input, (decimal?)output, tokensIn, tokensOut));

    [Fact]
    public void AFreeModel_CostsNothing_BecauseTheUserSaidSo() =>
        Assert.Equal(0m, AiPrice.Cost(0m, 0m, 5_000, 5_000));

    [Fact]
    public void Label_LosesControlAndBidiCharacters_AndIsCut()
    {
        Assert.Equal("USD", AiPrice.CleanLabel("  U‮S\u0000D‏ "));
        Assert.Equal(AiPrice.MaxLabelLength, new System.Globalization.StringInfo(AiPrice.CleanLabel(new string('x', 100))).LengthInTextElements);
        Assert.Equal(string.Empty, AiPrice.CleanLabel(null));
        Assert.Equal("€", AiPrice.CleanLabel("€"));
    }

    [Fact]
    public void Format_ShowsMoreDigitsForSmallSums()
    {
        var c = CultureInfo.InvariantCulture;
        Assert.Equal("0.0008 USD", AiPrice.Format(0.00075m, "USD", c));
        Assert.Equal("0.750 USD", AiPrice.Format(0.75m, "USD", c));
        Assert.Equal("12.00", AiPrice.Format(12m, null, c));
    }

    [Fact]
    public void ATurn_KeepsThePricesItRanUnder_SoALaterPriceChangeCannotRewriteIt()
    {
        var l = Localizer.For("en");
        var turn = new WorldChatUsage { InputTokens = 1_000_000, OutputTokens = 1_000_000, InputPricePerMTok = 1m, OutputPricePerMTok = 2m, PriceLabel = "USD" };
        Assert.Equal(3m, WorldChatView.Cost(turn));
        Assert.Contains(WorldChatView.Usage(l, turn), part => part.StartsWith("cost ", StringComparison.Ordinal));
        Assert.DoesNotContain(WorldChatView.Usage(l, new WorldChatUsage { InputTokens = 5, OutputTokens = 5 }), part => part.StartsWith("cost ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheChatTotal_SaysSoWhenOnlySomeTurnsHaveAPrice()
    {
        var l = Localizer.For("en");
        WorldChatLine Line(WorldChatUsage u) => new() { Speaker = WorldChatSpeaker.Model, Text = "x", Usage = u };
        var priced = new WorldChatUsage { InputTokens = 1_000_000, OutputTokens = 0, InputPricePerMTok = 1m, OutputPricePerMTok = 1m, PriceLabel = "USD" };
        var unpriced = new WorldChatUsage { InputTokens = 10, OutputTokens = 10 };

        Assert.Null(WorldChatView.Total(l, new[] { Line(new WorldChatUsage()) }));
        Assert.DoesNotContain("cost", WorldChatView.Total(l, new[] { Line(unpriced) })!, StringComparison.Ordinal);
        Assert.Contains("cost", WorldChatView.Total(l, new[] { Line(priced), Line(priced) })!, StringComparison.Ordinal);
        var mixed = WorldChatView.Total(l, new[] { Line(priced), Line(unpriced) })!;
        Assert.Contains("only 1 of 2", mixed, StringComparison.Ordinal);
    }
}
