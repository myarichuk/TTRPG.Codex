using Codex.Localization;

namespace Codex.Web.Services;

/// <summary>
/// Who has used the key in the server's own configuration today, and how much (F14). On a LAN server every account falls back
/// to that key, so each one gets a daily allowance of requests (<c>AI:DailyLimitPerUser</c>) and of pictures
/// (<c>AIImage:DailyLimitPerUser</c>); 0 means no limit. The ServerAdmin, whose key it is, has none. The counts live in memory,
/// so a restart starts the day over: that buys a few more requests, not a way round the allowance.
/// </summary>
public sealed class ServerAiUsage(IConfiguration configuration, ILogger<ServerAiUsage> logger, TimeProvider? clock = null)
{
    public enum Kind { Text, Image }

    /// <summary>One account's use of the server's key today.</summary>
    public sealed record Use(string UserId, int Text, int Images);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string UserId, Kind Kind), int> _counts = new();
    private DateOnly _day;

    public int LimitOf(Kind kind) =>
        Math.Max(0, kind == Kind.Text ? configuration.GetValue("AI:DailyLimitPerUser", 200) : configuration.GetValue("AIImage:DailyLimitPerUser", 20));

    public int UsedToday(string userId, Kind kind)
    {
        lock (_gate)
        {
            StartTheDay();
            return _counts.GetValueOrDefault((userId, kind));
        }
    }

    /// <summary>Counts one request on the server's key for <paramref name="userId"/>, or refuses it with
    /// <c>Service.Ai.ServerAllowanceSpent</c> when they have used today's allowance. <paramref name="unlimited"/> for the admin.</summary>
    public void Take(string userId, Kind kind, bool unlimited)
    {
        var limit = unlimited ? 0 : LimitOf(kind);
        int used;
        lock (_gate)
        {
            StartTheDay();
            used = _counts.GetValueOrDefault((userId, kind));
            if (limit == 0 || used < limit)
            {
                _counts[(userId, kind)] = ++used;
                limit = 0;
            }
        }

        if (limit > 0)
        {
            logger.LogInformation("User {UserId} has used today's allowance of {Limit} {Kind} requests on the server's AI key.", userId, limit, kind);
            throw new LocalizedException("Service.Ai.ServerAllowanceSpent", limit);
        }

        logger.LogInformation("Server AI key: {Kind} request by user {UserId} ({Used} today).", kind, userId, used);
    }

    /// <summary>Requests set aside for one long job (a book build): counted against the allowance up front, so the job is refused at the
    /// door instead of dying half way with the money spent. The job's calls draw from it (<see cref="Reservation.TryUse"/>); what
    /// is left over goes back with <see cref="Reservation.Dispose"/>.</summary>
    public sealed class Reservation : IDisposable
    {
        private readonly ServerAiUsage _owner;
        private readonly string _userId;
        private readonly Kind _kind;
        private int _left;

        internal Reservation(ServerAiUsage owner, string userId, Kind kind, int count)
        {
            _owner = owner;
            _userId = userId;
            _kind = kind;
            _left = count;
        }

        public int Left => Volatile.Read(ref _left);

        /// <summary>Spends one reserved request; false when none is left (the caller then goes through <see cref="Take"/> like anyone).</summary>
        public bool TryUse()
        {
            while (true)
            {
                var now = Volatile.Read(ref _left);
                if (now <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _left, now - 1, now) == now)
                {
                    return true;
                }
            }
        }

        /// <summary>Gives back every request that was not used. Safe to call twice.</summary>
        public void Dispose()
        {
            var unused = Interlocked.Exchange(ref _left, 0);
            if (unused > 0)
            {
                _owner.Refund(_userId, _kind, unused);
            }
        }
    }

    /// <summary>Sets aside <paramref name="count"/> requests for <paramref name="userId"/>, all or nothing: when they do not fit in
    /// what is left of today's allowance, nothing is taken and <c>Service.Ai.ServerAllowanceSpent</c> is thrown.</summary>
    public Reservation Reserve(string userId, Kind kind, int count, bool unlimited)
    {
        count = Math.Max(0, count);
        var limit = unlimited ? 0 : LimitOf(kind);
        var refused = false;
        lock (_gate)
        {
            StartTheDay();
            var used = _counts.GetValueOrDefault((userId, kind));
            // Compared in long: a huge count must not wrap around into "fits".
            if (limit == 0 || (long)used + count <= limit)
            {
                _counts[(userId, kind)] = used + count;
            }
            else
            {
                refused = true;
            }
        }

        if (refused)
        {
            logger.LogInformation("User {UserId} asked to set aside {Count} {Kind} requests on the server's AI key, which does not fit in today's allowance of {Limit}.", userId, count, kind, limit);
            throw new LocalizedException("Service.Ai.ServerAllowanceSpent", limit);
        }

        return new Reservation(this, userId, kind, count);
    }

    private void Refund(string userId, Kind kind, int count)
    {
        lock (_gate)
        {
            StartTheDay();
            if (_counts.TryGetValue((userId, kind), out var used))
            {
                _counts[(userId, kind)] = Math.Max(0, used - count);
            }
        }
    }

    /// <summary>Everyone who used the server's key today, most first.</summary>
    public IReadOnlyList<Use> Today()
    {
        lock (_gate)
        {
            StartTheDay();
            return _counts.GroupBy(c => c.Key.UserId)
                .Select(g => new Use(g.Key, g.Where(c => c.Key.Kind == Kind.Text).Sum(c => c.Value), g.Where(c => c.Key.Kind == Kind.Image).Sum(c => c.Value)))
                .OrderByDescending(u => u.Text + u.Images)
                .ToList();
        }
    }

    // The counts are for one UTC day; the first look on a new day forgets the last one.
    private void StartTheDay()
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (today != _day)
        {
            _counts.Clear();
            _day = today;
        }
    }
}
