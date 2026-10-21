using Microsoft.JSInterop;

namespace Codex.Web.Services;

/// <summary>
/// Shows times in the viewer's own time zone. The server doesn't know it, so the first interactive
/// component asks the browser once per circuit; until then (and on pages that never become interactive)
/// times fall back to the server's zone.
/// </summary>
public sealed class ViewerClock
{
    private Task<TimeZoneInfo>? _lookup;
    private TimeZoneInfo? _zone;

    public TimeZoneInfo Zone => _zone ?? TimeZoneInfo.Local;

    public bool IsKnown => _zone != null;

    public Task EnsureAsync(IJSRuntime js) => _lookup ??= LookupAsync(js);

    private async Task<TimeZoneInfo> LookupAsync(IJSRuntime js)
    {
        try
        {
            var id = await js.InvokeAsync<string>("codexTimeZone");
            _zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TimeZoneNotFoundException or JSDisconnectedException or TaskCanceledException)
        {
            _zone = TimeZoneInfo.Local;
        }

        return _zone;
    }

    /// <summary>A stored time (UTC, or unspecified and so taken as UTC) in the viewer's zone.</summary>
    public DateTime ToViewer(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);
    }

    public string Format(DateTime value, string format) => ToViewer(value).ToString(format);
}
