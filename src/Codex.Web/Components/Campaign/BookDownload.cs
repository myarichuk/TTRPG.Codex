namespace Codex.Web.Components.Campaign;

/// <summary>What <c>codex.downloadFrom</c> reports for a file the server builds (status, the server's reason, the printed pages cut off).</summary>
public sealed class BookDownload
{
    public int Status { get; set; }
    public string? Detail { get; set; }
    public string? Code { get; set; }

    /// <summary>The editable book's <c>X-Book-Overflow</c> header: the pages that print cut off (<see cref="BookEndpoints.OverflowHeader"/>).</summary>
    public string? Overflow { get; set; }
}
