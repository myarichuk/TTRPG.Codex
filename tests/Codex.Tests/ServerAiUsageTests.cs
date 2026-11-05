using Codex.Localization;
using Codex.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>F14: every account on a LAN server falls back to the server's AI key, so each gets a daily allowance on it, and
/// the admin can see who used it.</summary>
public class ServerAiUsageTests
{
    private sealed class FakeTimeProvider(DateTimeOffset now = default) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static ServerAiUsage Usage(FakeTimeProvider clock, int text = 2, int images = 1) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:DailyLimitPerUser"] = text.ToString(),
            ["AIImage:DailyLimitPerUser"] = images.ToString()
        }).Build(), NullLogger<ServerAiUsage>.Instance, clock);

    [Fact]
    public void EachAccount_GetsItsOwnAllowance_AndANewDayStartsItAgain()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var usage = Usage(clock);

        usage.Take("aria", ServerAiUsage.Kind.Text, unlimited: false);
        usage.Take("aria", ServerAiUsage.Kind.Text, unlimited: false);
        var spent = Assert.Throws<LocalizedException>(() => usage.Take("aria", ServerAiUsage.Kind.Text, unlimited: false));
        Assert.Equal("Service.Ai.ServerAllowanceSpent", spent.Key);
        Assert.Equal(2, usage.UsedToday("aria", ServerAiUsage.Kind.Text));

        // Pictures are counted apart, another account has its own allowance, and the admin has none.
        usage.Take("aria", ServerAiUsage.Kind.Image, unlimited: false);
        usage.Take("bram", ServerAiUsage.Kind.Text, unlimited: false);
        for (var i = 0; i < 5; i++) usage.Take("admin", ServerAiUsage.Kind.Text, unlimited: true);

        Assert.Equal(new[] { "admin", "aria", "bram" }, usage.Today().Select(u => u.UserId));
        Assert.Equal(new ServerAiUsage.Use("aria", 2, 1), usage.Today().Single(u => u.UserId == "aria"));

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Empty(usage.Today());
        usage.Take("aria", ServerAiUsage.Kind.Text, unlimited: false);
    }

    [Fact]
    public void ALimitOfNought_MeansNoLimit()
    {
        var usage = Usage(new FakeTimeProvider(), text: 0);
        for (var i = 0; i < 50; i++) usage.Take("aria", ServerAiUsage.Kind.Text, unlimited: false);
        Assert.Equal(50, usage.UsedToday("aria", ServerAiUsage.Kind.Text));
    }

    [Fact]
    public void AReservation_IsAllOrNothing_AndTheUnusedGoesBack()
    {
        var usage = Usage(new FakeTimeProvider(), text: 10);
        usage.Take("aria", ServerAiUsage.Kind.Text, unlimited: false); // 1 used, 9 left

        // 10 do not fit in 9: nothing is taken, so a build is refused at the door, not at section 9 of 10.
        var refused = Assert.Throws<LocalizedException>(() => usage.Reserve("aria", ServerAiUsage.Kind.Text, 10, unlimited: false));
        Assert.Equal("Service.Ai.ServerAllowanceSpent", refused.Key);
        Assert.Equal(1, usage.UsedToday("aria", ServerAiUsage.Kind.Text));

        var reservation = usage.Reserve("aria", ServerAiUsage.Kind.Text, 6, unlimited: false);
        Assert.Equal(7, usage.UsedToday("aria", ServerAiUsage.Kind.Text));
        Assert.True(reservation.TryUse());
        Assert.True(reservation.TryUse());
        Assert.Equal(4, reservation.Left);

        reservation.Dispose();
        reservation.Dispose(); // twice is harmless: only the 4 unused go back, once
        Assert.Equal(3, usage.UsedToday("aria", ServerAiUsage.Kind.Text));
        Assert.False(reservation.TryUse());
    }

    [Fact]
    public void AReservation_CannotWrapAroundOnAHugeCount_AndTheAdminIsNotLimited()
    {
        var usage = Usage(new FakeTimeProvider(), text: 10);
        Assert.Throws<LocalizedException>(() => usage.Reserve("aria", ServerAiUsage.Kind.Text, int.MaxValue, unlimited: false));
        Assert.Equal(0, usage.UsedToday("aria", ServerAiUsage.Kind.Text));
        usage.Reserve("admin", ServerAiUsage.Kind.Text, 500, unlimited: true).Dispose();
        Assert.Equal(0, usage.UsedToday("admin", ServerAiUsage.Kind.Text));
    }

    [Fact]
    public async Task ParallelCalls_NeverSpendMoreThanWasReserved()
    {
        var usage = Usage(new FakeTimeProvider(), text: 100);
        using var reservation = usage.Reserve("aria", ServerAiUsage.Kind.Text, 50, unlimited: false);
        var spent = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) if (reservation.TryUse()) Interlocked.Increment(ref spent);
        })));
        Assert.Equal(50, spent);
    }
}
