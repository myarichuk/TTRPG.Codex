namespace Codex.Web.Services;

/// <summary>
/// One AI request a component can stop. <see cref="Begin"/> hands out the token to pass down, Stop cancels it, and
/// disposing the component cancels whatever is still running, so leaving the page stops the call too.
/// </summary>
/// <example><code>
/// var ct = aiCall.Begin();
/// try { text = await Ai.RewriteAsync(..., ct); }
/// catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* stopped: not an error */ }
/// finally { aiCall.End(ct); }
/// </code></example>
public sealed class AiCall : IDisposable
{
    private CancellationTokenSource? run;

    public bool IsRunning => run != null;

    /// <summary>Starts a run (stopping one still going) and returns its token.</summary>
    public CancellationToken Begin()
    {
        run?.Cancel();
        run = new CancellationTokenSource();
        return run.Token;
    }

    public void Stop() => run?.Cancel();

    /// <summary>Ends the run <paramref name="token"/> belongs to; a newer run is left alone.</summary>
    public void End(CancellationToken token)
    {
        if (run == null || run.Token != token) return;
        run.Dispose();
        run = null;
    }

    public void Dispose()
    {
        run?.Cancel();
        run?.Dispose();
        run = null;
    }
}
