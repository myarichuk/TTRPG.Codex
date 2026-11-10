using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>
/// What the editable book's AI edits and merges cost, added to the edition's spend totals (the ones the book panel shows under the build,
/// docs/BOOK_EXPORT.md 42, step 2): 41.16 left them out, so a whole book shaped entry by entry was spent without a trace. Called under the
/// per-GM <see cref="BookGate"/>, which a build holds too, so the totals have one writer at a time. A run that sent nothing adds nothing.
/// </summary>
public static class BookSpend
{
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
