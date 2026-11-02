using Codex.Web.Services;
using Microsoft.JSInterop;
using NSubstitute;

namespace Codex.Tests;

public class ViewerClockDayTests
{
    private static async Task<ViewerClock> ClockInAsync(string zone)
    {
        var js = Substitute.For<IJSRuntime>();
        js.InvokeAsync<string>("codexTimeZone", Arg.Any<object?[]?>()).Returns(new ValueTask<string>(zone));
        var clock = new ViewerClock();
        await clock.EnsureAsync(js);
        return clock;
    }

    // R10: a DM five hours behind UTC picks 1 November; the server's own zone plays no part.
    [Fact]
    public async Task APickedDay_IsStoredAsThatDayInTheViewersZone_AndReadsBackAsItAsync()
    {
        var clock = await ClockInAsync("America/New_York");

        var stored = clock.DayToUtc(new DateTime(2026, 11, 1));

        Assert.Equal(new DateTime(2026, 11, 1, 17, 0, 0, DateTimeKind.Utc), stored);
        Assert.Equal(new DateTime(2026, 11, 1), clock.ToViewer(stored).Date);
    }

    [Fact]
    public async Task APickedDay_ReadsAsTheSameDay_ForAViewerNineHoursAheadAsync()
    {
        var dm = await ClockInAsync("America/New_York");
        var player = await ClockInAsync("Asia/Dubai");

        var stored = dm.DayToUtc(new DateTime(2026, 11, 1));

        Assert.Equal(new DateTime(2026, 11, 1), player.ToViewer(stored).Date);
    }
}
