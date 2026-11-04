using Codex.Core.AI;
using Codex.Core.Book;

namespace Codex.Tests;

public class BookBudgetTests
{
    // transportRetries 0 keeps the arithmetic readable; one test covers the multiplier.
    private static BookBudget Budget(long cap, int retries = 0) => new(cap, retries);

    [Fact]
    public void ACallThatDoesNotFitInTheCap_IsRefusedBeforeItIsMade()
    {
        var budget = Budget(1_000);
        using var first = budget.TryReserve(300, 400);
        Assert.NotNull(first);
        Assert.Null(budget.TryReserve(200, 200)); // 700 reserved + 400 > 1000
        Assert.True(budget.Exhausted);
        Assert.Equal(1, budget.Snapshot().Refused);
        Assert.Equal(300, budget.Snapshot().Remaining);
    }

    [Fact]
    public async Task ParallelWorkers_CannotOvershootTheCapTogether()
    {
        var budget = Budget(10_000);
        var granted = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                // Each call is worth 500 at worst; no settlement, so reservations pile up: exactly 20 fit.
                if (budget.TryReserve(300, 200) != null) Interlocked.Increment(ref granted);
            }
        })));
        Assert.Equal(20, granted);
        Assert.Equal(10_000, budget.Snapshot().ReservedTokens);
    }

    [Fact]
    public void Settling_ReplacesTheWorstCaseWithWhatWasReallyUsed_AndFreesTheRest()
    {
        var budget = Budget(1_000);
        using var call = budget.TryReserve(300, 400)!;
        call.Settle(250, 90);
        var s = budget.Snapshot();
        Assert.Equal((250, 90, 0, 1, false), (s.InputTokens, s.OutputTokens, s.ReservedTokens, s.Calls, s.Estimated));
        Assert.Equal(660, s.Remaining);
        call.Settle(999, 999); // settling twice changes nothing
        Assert.Equal(340, budget.Snapshot().Spent);
    }

    [Fact]
    public void AProviderThatReportsNothing_IsSettledAtTheEstimate_AndTheTotalsSayEstimated()
    {
        var budget = Budget(10_000);
        using var call = budget.TryReserve(300, 400)!;
        call.Settle(null, null, replyTokens: 120);
        var s = budget.Snapshot();
        Assert.True(s.Estimated);
        Assert.Equal((300, 120), (s.InputTokens, s.OutputTokens));

        // With nothing known about the reply, a quarter of the maximum: not zero, not the whole maximum.
        using var blind = budget.TryReserve(100, 400)!;
        blind.Settle(null, null);
        Assert.Equal(120 + 100, budget.Snapshot().OutputTokens);

        // The estimated reply never exceeds what the call could have written.
        using var wild = budget.TryReserve(10, 50)!;
        wild.Settle(null, null, replyTokens: 9_999);
        Assert.Equal(120 + 100 + 50, budget.Snapshot().OutputTokens);
    }

    [Fact]
    public void AFailedCall_IsChargedItsInput_ButANeverSentCallIsFree()
    {
        var budget = Budget(10_000);
        budget.TryReserve(300, 400)!.Failed();
        budget.TryReserve(500, 400)!.NotSent();
        using (budget.TryReserve(100, 400)) { } // dropped without saying: assumed sent
        var s = budget.Snapshot();
        Assert.Equal(400, s.InputTokens);
        Assert.Equal(0, s.OutputTokens);
        Assert.Equal((3, 3, 0, true), (s.Calls, s.Failed, s.ReservedTokens, s.Estimated));
    }

    [Fact]
    public void EveryAttemptReservesOnItsOwn_SoRetriesAreCountedAsSpending()
    {
        var budget = Budget(10_000);
        for (var attempt = 0; attempt < 3; attempt++) budget.TryReserve(1_000, 500)!.Settle(1_000, 200);
        Assert.Equal((3_000, 600, 3), (budget.Snapshot().InputTokens, budget.Snapshot().OutputTokens, budget.Snapshot().Calls));
    }

    [Fact]
    public void TheInputIsReservedOncePerTransportAttempt()
    {
        var budget = Budget(10_000, retries: 2);
        using var call = budget.TryReserve(1_000, 500)!;
        Assert.Equal(3_500, budget.Snapshot().ReservedTokens); // 1000 x 3 + 500
        call.Settle(1_000, 100);
        Assert.Equal(0, budget.Snapshot().ReservedTokens);
    }

    [Theory]
    [InlineData(long.MaxValue, 10)]
    [InlineData(10, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue)]
    [InlineData(-5, -5)]
    public void AbsurdEstimates_AreRefusedOrHarmless_NeverWrapAroundIntoFits(long input, long output)
    {
        var budget = Budget(1_000, retries: 2);
        var call = budget.TryReserve(input, output);
        if (input < 0 || output < 0) { Assert.NotNull(call); Assert.Equal(0, budget.Snapshot().ReservedTokens); }
        else Assert.Null(call);
    }

    [Fact]
    public void ACapOfNothing_RefusesEverything() => Assert.Null(Budget(0).TryReserve(1, 1));

    [Fact]
    public void ReportedUsageAboveTheReservation_IsRecordedHonestly_NotClamped()
    {
        var budget = Budget(1_000);
        budget.TryReserve(100, 100)!.Settle(900, 400);
        Assert.Equal(1_300, budget.Snapshot().Spent);
        Assert.True(budget.Exhausted);
    }

    [Fact]
    public void Cost_UsesThePricesTheUserTyped_AndIsUnknownWithoutThem()
    {
        var budget = Budget(10_000_000);
        budget.TryReserve(1, 1)!.Settle(1_000_000, 500_000);
        Assert.Equal(1.0m + 1.0m, budget.Snapshot().Cost(new AiPriceQuote(1m, 2m, "USD")));
        Assert.Null(budget.Snapshot().Cost(null));
        Assert.Null(budget.Snapshot().Cost(new AiPriceQuote(1m, null, "USD")));
    }
}

public class BookTokensTests
{
    [Fact]
    public void CyrillicAndHebrew_CountFarMoreThanEnglishOfTheSameLength()
    {
        var english = BookTokens.Estimate(new string('a', 1_000));
        var russian = BookTokens.Estimate(new string('я', 1_000));
        var hebrew = BookTokens.Estimate(new string('א', 1_000));
        Assert.True(russian * 10 >= english * 18, $"{russian} vs {english}");
        Assert.True(hebrew * 10 >= english * 18, $"{hebrew} vs {english}");
    }

    [Fact]
    public void Estimate_HandlesEmptyAndNull_AndIgnoresBidiMarks()
    {
        Assert.Equal(0, BookTokens.Estimate(null));
        Assert.Equal(0, BookTokens.Estimate(""));
        Assert.Equal(0, BookTokens.Estimate("‏‮‬"));
        Assert.True(BookTokens.Estimate("a") >= 1);
    }

    [Fact]
    public void TheRange_BracketsTheEstimate()
    {
        var (low, high) = BookTokens.Range(1_000);
        Assert.True(low < 1_000 && high > 1_000);
        Assert.Equal((0, 0), BookTokens.Range(0));
        Assert.Equal(1, BookTokens.Range(1).Low);
    }

    [Fact]
    public void Preflight_GivesRanges_RequestsAndAPriceOnlyWhenOneWasEntered()
    {
        var sections = new[] { new BookSectionLoad("a", 4_000, 2_000), new BookSectionLoad("b", 1_000, 2_000) };
        var plain = BookPreflight.Estimate(sections, 500, null);
        Assert.True(plain.InputLow < plain.InputHigh && plain.OutputLow < plain.OutputHigh);
        Assert.Equal((2, 4), (plain.RequestsMin, plain.RequestsMax));
        Assert.Null(plain.CostLow);
        Assert.Equal(plain.InputHigh * 2 + plain.OutputHigh * 2, plain.WorstCaseTokens);

        var priced = BookPreflight.Estimate(sections, 500, new AiPriceQuote(1m, 2m, "USD"));
        Assert.True(priced.CostLow > 0 && priced.CostHigh > priced.CostLow);
        Assert.Null(BookPreflight.Estimate(sections, 500, new AiPriceQuote(1m, null, "USD")).CostLow);
        Assert.Equal(0, BookPreflight.Estimate([], 500, null).RequestsMax);
    }
}
