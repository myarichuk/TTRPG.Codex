using System.Collections.Concurrent;

namespace Codex.Web.Book;

/// <summary>
/// One paid book run per GM at a time, whatever kind: a chapter build, an art director's run, a discard. Both services take this one gate
/// before their first await, so two tabs cannot start a text build and a director run together and spend twice (the A2 class of bug).
/// </summary>
public sealed class BookGate
{
    private readonly ConcurrentDictionary<string, bool> _busy = new();

    public bool TryEnter(string userId) => _busy.TryAdd(userId, true);

    public void Exit(string userId) => _busy.TryRemove(userId, out _);
}
