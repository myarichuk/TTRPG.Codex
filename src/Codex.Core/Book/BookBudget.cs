using Codex.Core.AI;

namespace Codex.Core.Book;

/// <summary>What a budget has recorded so far.</summary>
/// <param name="Estimated">True when any part of the totals is our own estimate because the provider reported no usage.</param>
/// <param name="Refused">How many reservations were refused for lack of budget (the build stops at the first, but parallel workers may see more).</param>
public sealed record BookBudgetSnapshot(long Cap, long InputTokens, long OutputTokens, long ReservedTokens, int Calls, int Failed, bool Estimated, int Refused)
{
    public long Spent => InputTokens + OutputTokens;

    public long Remaining => Math.Max(0, Cap - Spent - ReservedTokens);

    /// <summary>The cost so far by the user's prices, or null when it cannot be known. Still an estimate when <see cref="Estimated"/>.</summary>
    public decimal? Cost(AiPriceQuote? price) =>
        price == null ? null : AiPrice.Cost(price.InputPerMTok, price.OutputPerMTok, InputTokens, OutputTokens);
}

/// <summary>
/// The token budget of one book build. Every model call reserves its worst case first (what it may send plus the longest reply it
/// may write) and is refused when that no longer fits under the cap, so parallel workers cannot overshoot together, which counting
/// after the fact would allow. When the call ends, the reservation is replaced by what it really used. Three honesty rules:
/// <list type="bullet">
/// <item>A provider that reports no usage (many OpenAI-compatible servers when streaming) is settled at our estimate, and the totals say so.</item>
/// <item>A call that failed after it was sent is charged its input estimate, because a provider bills a request it received. Only a call that
/// never left (<see cref="Reservation.NotSent"/>) is free, and a reservation dropped without either is treated as sent.</item>
/// <item>Every attempt reserves on its own, so retries (transport, fallback to no <c>response_format</c>, a parse retry, a check retry) are
/// counted as the separate spending they are.</item>
/// </list>
/// </summary>
public sealed class BookBudget(long cap, int transportRetries = 2)
{
    private readonly Lock _gate = new();
    private long _input, _output, _reserved;
    private int _calls, _failed, _refused;
    private bool _estimated;

    /// <summary>The hard cap in tokens (input and output together). 0 or negative means nothing may be spent.</summary>
    public long Cap { get; } = Math.Max(0, cap);

    /// <summary>The cap has been reached or a reservation was refused: the build should stop and keep what exists.</summary>
    public bool Exhausted
    {
        get
        {
            lock (_gate)
            {
                return _refused > 0 || _input + _output + _reserved >= Cap;
            }
        }
    }

    public BookBudgetSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new BookBudgetSnapshot(Cap, _input, _output, _reserved, _calls, _failed, _estimated, _refused);
        }
    }

    /// <summary>
    /// Sets aside the worst case of one call, or returns null when it does not fit. The input is reserved once per transport attempt the
    /// client may make (the SDK retries a 429 or a 5xx by itself, and each try that got through is billed), the output once.
    /// </summary>
    public Reservation? TryReserve(long estimatedInput, long maxOutput)
    {
        var input = Math.Max(0, estimatedInput);
        var output = Math.Max(0, maxOutput);
        var worst = SaturatingAdd(SaturatingMultiply(input, 1 + Math.Max(0, transportRetries)), output);
        lock (_gate)
        {
            // Compared without overflow: a huge estimate must be refused, not wrap around into "fits".
            if (worst > Cap || _input + _output + _reserved > Cap - worst)
            {
                _refused++;
                return null;
            }

            _reserved += worst;
            return new Reservation(this, worst, input, output);
        }
    }

    private void Finish(Reservation r, long input, long output, bool estimated, bool failed)
    {
        lock (_gate)
        {
            _reserved -= r.Worst;
            _input += input;
            _output += output;
            _calls++;
            if (failed) _failed++;
            if (estimated) _estimated = true;
        }
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private static long SaturatingMultiply(long a, long b) => b != 0 && a > long.MaxValue / b ? long.MaxValue : a * b;

    /// <summary>The right to make one call. Settle it, or dispose it: an unsettled reservation counts as a failed call that was sent.</summary>
    public sealed class Reservation : IDisposable
    {
        private readonly BookBudget _owner;
        private readonly long _estimatedInput;
        private readonly long _maxOutput;
        private int _done;

        internal Reservation(BookBudget owner, long worst, long estimatedInput, long maxOutput)
        {
            _owner = owner;
            Worst = worst;
            _estimatedInput = estimatedInput;
            _maxOutput = maxOutput;
        }

        internal long Worst { get; }

        /// <summary>The call returned. <paramref name="reported"/> is what the provider said (either part may be missing); a missing part is
        /// settled at our estimate, the reply's own (<paramref name="replyTokens"/> when the caller knows it, else a quarter of the maximum) for output.</summary>
        public void Settle(long? reportedInput, long? reportedOutput, long? replyTokens = null)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return;
            }

            var estimated = reportedInput == null || reportedOutput == null;
            var input = reportedInput is { } i ? Math.Max(0, i) : _estimatedInput;
            var output = reportedOutput is { } o ? Math.Max(0, o) : Math.Min(_maxOutput, Math.Max(0, replyTokens ?? _maxOutput / 4));
            _owner.Finish(this, input, output, estimated, failed: false);
        }

        /// <summary>The call failed after it was sent: charged the input estimate, flagged as estimated.</summary>
        public void Failed() => Close(sent: true);

        /// <summary>The call never left (refused locally, cancelled before sending): nothing was spent.</summary>
        public void NotSent() => Close(sent: false);

        private void Close(bool sent)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return;
            }

            _owner.Finish(this, sent ? _estimatedInput : 0, 0, estimated: sent, failed: true);
        }

        public void Dispose() => Failed();
    }
}
