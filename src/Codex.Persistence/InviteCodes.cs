using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Codex.Persistence;

/// <summary>
/// Campaign invite codes (D14). A code is the only thing between a stranger and a seat at the table, and on a server open
/// to the internet the only thing between a stranger and an account, so it is 128 random bits, not a slice of a GUID.
/// </summary>
public static class InviteCodes
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A new code: 128 bits from <see cref="RandomNumberGenerator"/>, written in lower-case base32 (26 characters).</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        var text = new char[26];
        int buffer = 0, bits = 0, length = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text[length++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        text[length] = Alphabet[(buffer << (5 - bits)) & 31];
        return new string(text);
    }

    /// <summary>A code as typed or pasted: trimmed and lower-cased, as every code is stored.</summary>
    public static string Clean(string? code) => (code ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Whether <paramref name="code"/> lets <paramref name="userId"/> in now: it is the campaign's code, it has not
    /// expired, and when it works once, no one else has used it. The one who used it may follow it again (they are a member
    /// already, and the page joins twice when it prerenders).</summary>
    public static bool Admits(CampaignDocument campaign, string code, string? userId, DateTime now) =>
        campaign.InviteCode.Length > 0
        && string.Equals(campaign.InviteCode, code, StringComparison.Ordinal)
        && (campaign.InviteExpiresAt == null || campaign.InviteExpiresAt > now)
        && (!campaign.InviteSingleUse || campaign.InviteUsedBy == null || campaign.InviteUsedBy == userId);
}

/// <summary>
/// Failed guesses per key in a sliding window: at <c>max</c> failures within <c>window</c>, <see cref="Allows"/> is false
/// until the oldest ages out. In memory, so a restart forgets; that is a few more guesses, not a way round it.
/// </summary>
public class RecentFailures(int max, TimeSpan window, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> failures = new(StringComparer.Ordinal);

    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public bool Allows(string key)
    {
        if (!failures.TryGetValue(key, out var queue)) return true;
        lock (queue)
        {
            Prune(queue);
            return queue.Count < max;
        }
    }

    public void Failed(string key)
    {
        var queue = failures.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            Prune(queue);
            queue.Enqueue(Now);
        }
    }

    private void Prune(Queue<DateTime> queue)
    {
        while (queue.Count > 0 && Now - queue.Peek() >= window) queue.Dequeue();
    }
}

/// <summary>
/// Wrong invite codes per user (D14). The HTTP limiter counts requests to the join path, but inside the app the join page is
/// reached over the open circuit, where it counts nothing; this counts in the join itself. Ten wrong codes in fifteen minutes
/// and that user's joins are refused until the oldest ages out. The page joins twice when it prerenders, so one bad link is
/// two of the ten.
/// </summary>
public sealed class InviteAttempts(TimeProvider? clock = null) : RecentFailures(MaxFailures, Window, clock)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Wrong passwords per account and client address (R13). The HTTP limiter rations each address across every account; this
/// stops one address working through one account's password at that rate. Keyed by address as well, so a stranger cannot
/// lock the DM out by typing their name wrong on purpose.
/// </summary>
public sealed class LoginAttempts(TimeProvider? clock = null) : RecentFailures(MaxFailures, Window, clock)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public static string Key(string username, string client) => $"{username.Trim().ToLowerInvariant()}|{client}";
}
