using Codex.Persistence;

namespace Codex.Tests;

/// <summary>Server settings held in memory, for code that reads them without a database.</summary>
public sealed class InMemoryServerSettings(ServerSettingsDocument? start = null) : IServerSettingsStore
{
    private ServerSettingsDocument value = start ?? new ServerSettingsDocument();
    public List<AdminLogDocument> Log { get; } = new();

    public Task<ServerSettingsDocument> GetAsync() => Task.FromResult(value.Copy());

    public ServerSettingsDocument Get() => value.Copy();

    public Task<ServerSettingsDocument> UpdateAsync(Action<ServerSettingsDocument> change)
    {
        var next = value.Copy();
        change(next);
        value = next;
        return Task.FromResult(next.Copy());
    }

    public Task LogAsync(AdminLogDocument entry)
    {
        Log.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AdminLogDocument>> RecentLogAsync(int count) =>
        Task.FromResult<IReadOnlyList<AdminLogDocument>>(Log.OrderByDescending(e => e.At).Take(count).ToList());

    public Task<bool> MoveExistingServerAsync() => Task.FromResult(false);
}
