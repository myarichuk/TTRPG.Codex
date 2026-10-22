namespace Codex.Persistence;

/// <summary>
/// Lets the live-table runtime hear that a campaign is about to be deleted without the campaign
/// repository depending on the runtime manager (which itself depends on the repository).
/// </summary>
public sealed class CampaignLifecycleHooks
{
    private readonly List<Func<string, Task>> _deleting = new();

    /// <summary>Registers a callback that runs, with the campaign id, before a campaign's data is removed.</summary>
    public void OnDeleting(Func<string, Task> callback)
    {
        lock (_deleting)
        {
            _deleting.Add(callback);
        }
    }

    public async Task RunDeletingAsync(string campaignId)
    {
        Func<string, Task>[] callbacks;
        lock (_deleting)
        {
            callbacks = _deleting.ToArray();
        }

        foreach (var callback in callbacks)
        {
            await callback(campaignId);
        }
    }
}
