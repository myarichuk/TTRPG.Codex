using System.Collections.Concurrent;

namespace Codex.Web.Book;

/// <summary>
/// One paid book run per GM at a time, whatever kind: an AI edit or merge of the draft, an art director's run, a drawing. Every service takes
/// this one gate before its first await, so two tabs cannot start two paid runs together and spend twice (the A2 class of bug).
/// </summary>
public sealed class BookGate
{
    private readonly ConcurrentDictionary<string, bool> _busy = new();

    public bool TryEnter(string userId) => _busy.TryAdd(userId, true);

    public void Exit(string userId) => _busy.TryRemove(userId, out _);
}
