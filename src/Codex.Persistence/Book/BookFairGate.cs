namespace Codex.Persistence;

/// <summary>
/// A fixed number of places shared fairly between users (docs/BOOK_EXPORT.md 31.1, item 3). Nobody is held back while nobody else waits: one
/// user alone may hold every place. When a place comes free and several users are waiting, it goes to the waiting user who holds the fewest
/// places now (the one who asked first among those), so one GM's first build of many large pictures delays another's preview by about one
/// picture, not by the whole build.
/// </summary>
public sealed class BookFairGate(int places)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
    private int _free = Math.Max(1, places);
    private long _ticket;

    private sealed class Owner
    {
        public int Running;
        public readonly LinkedList<Waiter> Waiting = new();
    }

    private sealed class Waiter(long ticket)
    {
        public readonly long Ticket = ticket;
        public readonly TaskCompletionSource Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Users the gate knows now (holding or waiting for a place): a test reads it to see that nothing is kept for a user who left.</summary>
    public int Owners
    {
        get { lock (_lock) return _owners.Count; }
    }

    /// <summary>Waits for a place for <paramref name="owner"/>; dispose the result to give it back.</summary>
    public async Task<IDisposable> EnterAsync(string? owner, CancellationToken ct = default)
    {
        var key = owner ?? string.Empty;
        Waiter waiter;
        LinkedListNode<Waiter> node;
        Owner state;
        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
            state = Get(key);
            if (_free > 0 && !AnyoneWaiting())
            {
                _free--;
                state.Running++;
                return new Place(this, key);
            }

            waiter = new Waiter(++_ticket);
            node = state.Waiting.AddLast(waiter);
        }

        await using (ct.Register(() =>
                     {
                         lock (_lock)
                         {
                             // Still waiting: leave the line. Already given a place: the place is this request's, and it is given back below.
                             if (node.List == null) return;
                             state.Waiting.Remove(node);
                             Forget(key, state);
                             waiter.Granted.TrySetCanceled(ct);
                         }
                     }))
        {
            await waiter.Granted.Task;
        }

        return new Place(this, key);
    }

    private bool AnyoneWaiting() => _owners.Values.Any(o => o.Waiting.Count > 0);

    private Owner Get(string key)
    {
        if (!_owners.TryGetValue(key, out var state)) _owners[key] = state = new Owner();
        return state;
    }

    private void Forget(string key, Owner state)
    {
        if (state.Running == 0 && state.Waiting.Count == 0) _owners.Remove(key);
    }

    private void Leave(string key)
    {
        lock (_lock)
        {
            var state = _owners[key];
            state.Running--;
            Forget(key, state);

            // The place goes to whoever waits holding the fewest places, the earliest of them; with nobody waiting it is free.
            Owner? next = null;
            foreach (var o in _owners.Values)
            {
                if (o.Waiting.Count == 0) continue;
                if (next == null || o.Running < next.Running || o.Running == next.Running && o.Waiting.First!.Value.Ticket < next.Waiting.First!.Value.Ticket)
                {
                    next = o;
                }
            }

            if (next == null)
            {
                _free++;
                return;
            }

            var waiter = next.Waiting.First!.Value;
            next.Waiting.RemoveFirst();
            next.Running++;
            waiter.Granted.TrySetResult();
        }
    }

    private sealed class Place(BookFairGate gate, string key) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) gate.Leave(key);
        }
    }
}
