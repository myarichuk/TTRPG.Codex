using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>What an edition's AI work has used over all its runs, as the Ask AI tab shows it.</summary>
public sealed record BookBuildTotals(long InputTokens, long OutputTokens, int Calls, int Runs, bool Estimated, decimal Cost, int UnpricedRuns, string PriceLabel, DateTime UpdatedAt);

/// <summary>
/// What the editable book's AI edits and merges cost, added to the edition's spend totals (docs/BOOK_EXPORT.md 42, step 2): 41.16 left them
/// out, so a whole book shaped entry by entry was spent without a trace. The totals live in the edition's <see cref="BookBuildDocument"/>,
/// which once also held the old route's AI polish (deleted, 50); its stored chapters are no longer read. Called under the per-GM
/// <see cref="BookGate"/>, so the totals have one writer at a time. A run that sent nothing adds nothing.
/// </summary>
public static class BookSpend
{
    /// <summary>The edition's totals; null when nothing was spent yet, and for anyone who is not the campaign's GM now.</summary>
    public static async Task<BookBuildTotals?> ReadAsync(IBookBuildRepository? builds, CampaignAccess dm, string edition) =>
        builds == null || await builds.GetAsync(dm, edition) is not { Calls: > 0 } b ? null
            : new BookBuildTotals(b.InputTokens, b.OutputTokens, b.Calls, b.Runs, b.Estimated, b.Cost, b.UnpricedRuns, b.PriceLabel, b.UpdatedAt);

    public static async Task AddAsync(IBookBuildRepository? builds, CampaignAccess dm, string edition, BookBudgetSnapshot spent, AiPriceQuote? price)
    {
        if (builds == null || spent.Calls == 0) return;
        var build = await builds.GetAsync(dm, edition) ?? new BookBuildDocument { Audience = edition };
        build.Runs++;
        build.InputTokens += spent.InputTokens;
        build.OutputTokens += spent.OutputTokens;
        build.Calls += spent.Calls;
        build.Estimated |= spent.Estimated;
        if (spent.Cost(price) is { } cost) { build.Cost += cost; build.PriceLabel = price?.Label ?? build.PriceLabel; }
        else build.UnpricedRuns++;
        build.UpdatedAt = DateTime.UtcNow;
        await builds.SaveAsync(dm, build);
    }
}
