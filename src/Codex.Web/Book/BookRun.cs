using Codex.Core.AI;
using Codex.Core.Book;

namespace Codex.Web.Book;

public enum BookRunStatus { Running, Done, Stopped, Failed }

public enum BookRowStatus { Queued, Cached, Refined, Kept, Skipped }

public sealed record BookRunRow(string Id, string Title, BookRowStatus Status, string? Reason);

/// <summary>A consistent picture of a run for the page: never a half-updated one.</summary>
public sealed record BookRunSnapshot(
    BookRunStatus Status, IReadOnlyList<BookRunRow> Rows, BookBudgetSnapshot Budget, AiPriceQuote? Price, string Model, string? Failure, bool SaveFailed)
{
    public int Finished => Rows.Count(r => r.Status != BookRowStatus.Queued);
    public int Refined => Rows.Count(r => r.Status == BookRowStatus.Refined);
}

/// <summary>The GM already has a paid run under way (<see cref="BookGate"/>): a director run, a drawing, an AI edit or a merge.</summary>
public sealed class BookBuildBusyException() : Exception("A paid run of this book is already under way.");

/// <summary>One running (or finished) paid run (the art director's or a drawing), as the page watches it. The work does not belong to the page: closing the tab does not
/// throw away what the calls have already cost, and the page finds the run again when it is opened.</summary>
public sealed class BookRun
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private BookRunRow[] _rows;
    private BookRunStatus _status = BookRunStatus.Running;
    private string? _failure;
    private bool _saveFailed;
    private readonly BookBudget _budget;
    private BookBudgetSnapshot? _final;

    internal BookRun(string key, string userId, IEnumerable<BookRunRow> rows, BookBudget budget, AiPriceQuote? price, string model)
    {
        Key = key;
        UserId = userId;
        _rows = rows.ToArray();
        _budget = budget;
        Price = price;
        Model = model;
        Started = DateTime.UtcNow;
    }

    public string Key { get; }
    public string UserId { get; }
    public AiPriceQuote? Price { get; }
    public string Model { get; }
    public DateTime Started { get; }
    internal CancellationToken Token => _cancel.Token;
    /// <summary>Completes when the run has ended and everything it kept has been stored.</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    /// <summary>Raised after every change. The page re-reads <see cref="Snapshot"/> on its own thread.</summary>
    public event Action? Changed;

    public bool Running { get { lock (_gate) return _status == BookRunStatus.Running; } }

    public BookRunSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new BookRunSnapshot(_status, _rows.ToArray(), _final ?? _budget.Snapshot(), Price, Model, _failure, _saveFailed);
        }
    }

    /// <summary>Stops the run: calls in flight are cancelled (and charged as the sent calls they may be), and nothing else is sent.</summary>
    public void Cancel() => _cancel.Cancel();

    internal void Row(string id, BookRowStatus status, string? reason)
    {
        lock (_gate)
        {
            var i = Array.FindIndex(_rows, r => r.Id == id);
            if (i >= 0) _rows[i] = _rows[i] with { Status = status, Reason = reason };
        }

        Changed?.Invoke();
    }

    internal void SaveFailed()
    {
        lock (_gate) _saveFailed = true;
        Changed?.Invoke();
    }

    internal void Finish(BookRunStatus status, string? failure)
    {
        lock (_gate)
        {
            _status = status;
            _failure = failure;
            _final = _budget.Snapshot();
        }

        Changed?.Invoke();
    }
}
