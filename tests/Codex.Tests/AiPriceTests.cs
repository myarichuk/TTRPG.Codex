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
