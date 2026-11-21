namespace Codex.Core.Consistency;

/// <summary>
/// Waiting for work that cannot itself be stopped (a repository call that takes no token, a system's compiled check that ignores its token): the caller
/// stops waiting when its token is cancelled, and the work goes on alone. Whatever that work throws later is observed here, so an abandoned task never
/// ends as an unobserved exception (<see cref="TaskScheduler.UnobservedTaskException"/>).
/// </summary>
public static class Abandoned
{
    /// <summary><paramref name="task"/>'s result, or an <see cref="OperationCanceledException"/> as soon as <paramref name="cancel"/> is cancelled; then
    /// the task is left to finish on its own and its exception, if it has one, is observed.</summary>
    public static async Task<T> WaitObservedAsync<T>(this Task<T> task, CancellationToken cancel)
    {
        try
        {
            return await task.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Observe(task);
            throw;
        }
    }

    /// <summary>Observes <paramref name="task"/>'s exception whenever it ends faulted; nothing else is done with it.</summary>
    public static void Observe(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
