using Codex.Core.Consistency;
using Codex.Persistence;

namespace Codex.Web.Services;

// What the chat's list_issues tool reads: the consistency checker's open issues (docs/WORLD_BUILDER.md, section 9).
public sealed partial class WorldBuilderService
{
    /// <summary>
    /// The open issues <paramref name="scope"/> and <paramref name="severity"/> ask for, as <see cref="IssueListing"/> writes them. The
    /// checks run in code; nothing here asks a model whether something is broken. Issues the DM dismissed are not listed. A scope or a
    /// severity that is not one of the known ones is refused in words ("Nothing was listed"), never answered with an empty list that
    /// would read as all clear. DM only.
    /// </summary>
    public async Task<string> IssuesAsync(CampaignDocument campaign, CampaignAccess access, string? scope, string? severity)
    {
        DmOnly(access);
        if (!IssueFilter.IsKnownScope(scope))
        {
            var shown = scope!.Length > 80 ? scope[..80] + "…" : scope;
            return $"Nothing was listed: \"{shown}\" is not a scope. Use \"all\" (or leave it out), the name of a check ({string.Join(", ", CheckNames.All)}), " +
                   "or one thing as page:<id>, actor:<id>, place:<id> or fact:<id>, with an issue's entity_id.";
        }

        if (!IssueListing.TryParseSeverity(severity, out var least))
        {
            var shown = severity!.Length > 40 ? severity[..40] + "…" : severity;
            return $"Nothing was listed: \"{shown}\" is not a severity. Use {string.Join(", ", IssueListing.SeverityNames)} (the least severe level to list), or leave it out for all.";
        }

        if (consistency == null) return "Nothing was listed: the consistency checker is not available here.";
        return IssueListing.Render(await consistency.ListAsync(campaign, access, new IssueFilter(scope, least)));
    }
}
