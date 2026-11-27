using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.Core.WorldBuilder;
using Codex.Persistence;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

// The tools the world-building chat's model calls, and what each hands back to it.
public sealed partial class WorldChat
{
    private List<AITool> Tools(Action<WorldChatStep> progress)
    {
        // Every call is recorded as a step: what was asked, what came back, how long it took, and whether it failed.
        // data: the result is the campaign's text (pages, entries, names), which anyone who can write a page could have written. It
        // reaches the model fenced off with a tag it cannot guess, so a page that says "ignore your instructions" is read as a page.
        async Task<string> Step(string tool, object arguments, string? subject, int? count, Func<Task<string>> run, bool data = true)
        {
            var step = new WorldChatStep
            {
                Tool = tool,
                Subject = subject,
                Count = count,
                Arguments = JsonSerializer.Serialize(arguments, StepJson),
                Status = WorldChatStepStatus.Running,
                StartedAt = DateTime.UtcNow
            };
            trail.Add(step);
            progress(step);
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await run();
                step.Result = result.Length <= MaxStepResult ? result : result[..MaxStepResult] + "\n[…]";
                step.Rejected = result.StartsWith("Nothing was", StringComparison.Ordinal);
                step.Status = WorldChatStepStatus.Done;
                return data ? Fenced(result) : result;
            }
            catch (Exception ex)
            {
                step.Error = ex.Message;
                step.Status = WorldChatStepStatus.Failed;
                throw;
            }
            finally
            {
                step.DurationMs = watch.ElapsedMilliseconds;
                progress(step);
            }
        }

        return new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("Words to look for in names and page texts; empty for an overview of the whole campaign.")] string? query) =>
                    Step("search_world", new { query }, string.IsNullOrWhiteSpace(query) ? null : query.Trim(), null,
                        () => builder.SearchWorldAsync(campaign, access, query)),
                "search_world",
                "Finds the campaign's wiki pages, places and cast members by name or text. Each line gives the key a proposal uses to link to it or change it."),
            AIFunctionFactory.Create(
                ([Description("The key of a page (existing:slug) or its title; or existing:place:<id> for a place's description, existing:actor:<id> for a cast member's history, existing:fact:<id> for a secret in Lore, existing:region:<id> for a region, existing:sheet:<id> for a character\'s sheet values.")] string page) =>
                    Step("get_page", new { page }, page, null,
                        async () => await builder.PageAsync(access, page, campaign) ?? $"There is no page \"{page}\". Use search_world to find one."),
                "get_page",
                "Reads one wiki page in full, or a place's description or a cast member's history by their key, which is what an edit replaces; or a character's sheet keys with their values, which a sheet edit sets."),
            AIFunctionFactory.Create(
                () => Step("list_places", new { }, null, null, () => builder.PlacesAsync(access)),
                "list_places",
                "Lists every region and the places inside it as a tree, with the key a new place uses as its parent."),
            AIFunctionFactory.Create(
                ([Description("A name, or part of it; empty lists the type's entries.")] string query,
                    [Description("A content type to search (\"monster\", \"spell\", \"magic-item\", \"equipment\"); empty searches creatures.")] string? type) =>
                    Step("search_library", new { query, type }, query, null, () => builder.SearchLibraryAsync(campaign, access, query, type)),
                "search_library",
                "Searches the rules Library, packs and the campaign's homebrew: for the creature whose statistics suit a person (\"stats\"), an entry to make a variant of (\"basedOn\"), " +
                "a homebrew entry to change (its key), or to see what already exists."),
            AIFunctionFactory.Create(
                ([Description("The entry as search_library lists it: type/id (\"spell/fireball\"), or a homebrew entry's key.")] string entry) =>
                    Step("get_entry", new { entry }, entry, null, () => builder.EntryAsync(campaign, access, entry)),
                "get_entry",
                "Reads one Library entry in full: its rules text, every field with its value, and its tags. Read an entry before you change it or make a variant of it."),
            AIFunctionFactory.Create(
                ([Description("The homebrew type, as listed in the instructions.")] string type) =>
                    Step("homebrew_fields", new { type }, type, null, () => builder.HomebrewFieldsAsync(campaign, access, type)),
                "homebrew_fields",
                "Lists the fields a homebrew entry of one type fills, for its \"properties\"."),
            AIFunctionFactory.Create(
                () => Step("list_tags", new { }, null, null, () => builder.TagsAsync(access)),
                "list_tags",
                "Lists the tags the campaign's wiki pages and homebrew entries use, with how many of each, so new tags match the old ones."),
            AIFunctionFactory.Create(
                ([Description("What to list: \"all\" (or empty), the name of a check such as broken-link, or one thing as page:<id>, actor:<id>, place:<id> or fact:<id>.")] string? scope = null,
                    [Description("The least severe level to list: error, warning or info; empty lists all.")] string? severity = null) =>
                    Step("list_issues", new { scope, severity }, string.IsNullOrWhiteSpace(scope) ? null : scope.Trim(), null, () => builder.IssuesAsync(campaign, access, scope, severity)),
                "list_issues",
                "Lists what the app's own checks found wrong or missing in the campaign (broken links, secrets that may leak, empty histories), most severe first, at most 50. " +
                "The checks are code: you never judge whether something is broken. An edit entry fixes one by naming its id in \"resolves\"."),
            AIFunctionFactory.Create(
                ([Description("A place as list_places gives it (existing:place:<id>); the encounters at it and under it. Leave it out to list them all.")] string? place = null) =>
                    Step("list_encounters", new { place }, string.IsNullOrWhiteSpace(place) ? null : place.Trim(), null, () => builder.ListEncountersAsync(campaign, access, place)),
                "list_encounters",
                "Lists the prepared encounters, one line each: its key (existing:encounter:<id>), its place, its lineup in short, its status and, where the system rates difficulty, the band. GM only."),
            AIFunctionFactory.Create(
                ([Description("An encounter's key, as list_encounters gives it: existing:encounter:<id>.")] string encounter) =>
                    Step("get_encounter", new { encounter }, encounter, null,
                        async () => await builder.EncounterAsync(campaign, access, encounter) ?? $"There is no prepared encounter \"{encounter}\". Use list_encounters to find one."),
                "get_encounter",
                "Reads one prepared encounter in full: each line with its key, count and note, its notes, the hazards at its place and the difficulty readout. Read it before you change it."),
            AIFunctionFactory.Create(
                ([Description("A place's key, as list_places gives it: existing:place:<id>.")] string place) =>
                    Step("place_hazards", new { place }, place, null, () => builder.PlaceHazardsAsync(campaign, access, place)),
                "place_hazards",
                "Lists the hazards at a place and under it (their keys, names, states and whether they take a turn), and the creatures its features say live there. Build an encounter on what the place already has."),
            AIFunctionFactory.Create(
                ([Description("The lineup to rate: the same lines an encounter's \"creatures\" take (each \"entry\" and \"count\"), of Library, homebrew or cast entries that exist already.")] List<ProposalCreature> creatures) =>
                    Step("rate_encounter", new { creatures }, null, creatures?.Count ?? 0, () => builder.RateEncounterAsync(campaign, access, creatures)),
                "rate_encounter",
                "Rates a draft lineup for the party, so you can try a lineup before you propose it. Says so when the system declares no difficulty."),
            AIFunctionFactory.Create(
                (AdditionInput additions) =>
                    Step("add_entries", additions, null, additions.Entries?.Count ?? 0, () => WithGearAsync(AddEntriesAsync(additions)), data: false),
                "add_entries",
                "Adds entries to the proposal already waiting for the game master, without resending it: pass ONLY the new entries, each with a ref the proposal does not use yet. " +
                "They may use the refs already in the proposal as a place's parent, in \"links\" and as [[ref]]. The game master's ticks are kept. With no proposal waiting, it makes one."),
            AIFunctionFactory.Create(
                (ProposalInput proposal) =>
                    Step("propose_changes", proposal, null, proposal.Entries?.Count ?? 0, () => WithGearAsync(ProposeAsync(proposal)), data: false),
                "propose_changes",
                "Proposes related entries to add to the campaign or changes to what it has, all in one proposal, for the game master to review and apply. Saves nothing. " +
                "Returns errors to fix (then call it again with the whole corrected proposal) or confirms the proposal is ready for review.")
        };
    }

    // Once a proposal stands (its entries carried over from the one before it), what its people would carry is picked, so the DM
    // reviews that too and applying asks the model nothing. Only for people with no pick yet; a refused proposal changes nothing.
    private async Task<string> WithGearAsync(Task<string> proposing)
    {
        var result = await proposing;
        if (Proposal is { } proposal) await builder.PickGearAsync(campaign, access, proposal, running?.Token ?? CancellationToken.None);
        return result;
    }

    /// <summary>A tool's result as the model reads it: between markers with a tag made for this result alone, so text inside cannot
    /// close the fence and go on as if the app were speaking (AiPrompts.WorldChat says what the markers mean).</summary>
    public static string Fenced(string result)
    {
        var tag = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        return $"[campaign data {tag}]\n{result}\n[end of campaign data {tag}]";
    }

    // New entries beside the waiting proposal's: checked together (a new place can sit inside an old one), then what the game master
    // did with the old entries (ticks, the cast box) and what saving already made of them are put back on them.
    private async Task<string> AddEntriesAsync(AdditionInput additions)
    {
        var added = additions.Entries ?? new List<ProposalEntry>();
        if (Proposal is not { } before || Held is not { } input) return await ProposeAsync(new ProposalInput { Entries = added });

        // A ref the proposal already uses would replace that entry (and every link to it) without a word; a missing one would
        // match every other missing one. Either way the game master's proposal would change under them.
        var used = before.Drafts.Select(d => d.Ref).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clash = added.Select(e => e.Ref?.Trim() ?? string.Empty).Where(r => r.Length == 0 || used.Contains(r)).ToList();
        if (clash.Count > 0)
        {
            return "Nothing was added: every new entry needs its own \"ref\" that the proposal does not use yet (" +
                   (clash.Any(r => r.Length == 0) ? "one has none" : $"already used: {string.Join(", ", clash.Distinct(StringComparer.OrdinalIgnoreCase))}") +
                   $"). The proposal has: {string.Join(", ", used)}. Call add_entries again.";
        }

        var room = WorldBundleParser.MaxDrafts - before.Drafts.Count;
        if (added.Count > room)
        {
            return $"Nothing was added: the proposal already holds {before.Drafts.Count} entries and at most {WorldBundleParser.MaxDrafts} fit, so {Math.Max(room, 0)} more can. " +
                   "Add fewer, or tell the game master to apply the proposal first and ask for the rest after.";
        }

        // A claim on a waiting entry that is no longer an open issue (the game master dismissed it, or something fixed it, since) is dropped, not held
        // against the new entries: add_entries cannot change a waiting entry, so refusing would leave the model nothing it could do. The result says which.
        var (waiting, dropped) = await WithoutStaleClaimsAsync(input.Entries ?? new List<ProposalEntry>());
        var merged = new ProposalInput { Summary = input.Summary, Region = input.Region, RegionKind = input.RegionKind, Entries = waiting.Concat(added).ToList() };
        var result = await ProposeAsync(merged, "add_entries", $"only the new entries (the {input.Entries?.Count ?? 0} waiting ones come first when errors are numbered, so your first new entry is entries[{input.Entries?.Count ?? 0}])", fresh: false);
        if (!ReferenceEquals(Proposal, before) && Proposal != null)
        {
            CarryOver(before, Proposal, decisions: true);
            if (dropped.Count > 0)
            {
                result += "\nDropped from waiting entries, because they are no longer open issues (the game master dismissed them or they were fixed): " +
                          string.Join("; ", dropped.Select(d => $"{d.Ref}: {JsonSerializer.Serialize(d.Ids, ProposalJson)}")) + ". Those entries stay in the proposal without those claims.";
            }
        }

        return result;
    }

    // The waiting entries as copies, each with only the claims that are still open issues; the ones that lost a claim carry an explicit (possibly
    // empty) list, so the claims they had are replaced and not carried back over them. No check runs when no waiting entry claims anything.
    private async Task<(List<ProposalEntry> Entries, List<(string Ref, List<string> Ids)> Dropped)> WithoutStaleClaimsAsync(List<ProposalEntry> waiting)
    {
        var dropped = new List<(string Ref, List<string> Ids)>();
        if (!waiting.Any(e => e.Resolves is { Count: > 0 })) return (waiting, dropped);
        IReadOnlyList<Codex.Core.Consistency.Issue>? open;
        try
        {
            open = await builder.OpenIssuesAsync(campaign, access);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            open = null; // the checks could not run: nothing is dropped, and the check of the proposal says what it says
        }

        if (open == null) return (waiting, dropped);
        var openIds = open.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var kept = new List<ProposalEntry>();
        foreach (var entry in waiting)
        {
            var stale = entry.Resolves?.Select(id => id.Trim()).Where(id => id.Length > 0 && !openIds.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
            if (stale is not { Count: > 0 })
            {
                kept.Add(entry);
                continue;
            }

            var copy = JsonSerializer.Deserialize<ProposalEntry>(JsonSerializer.Serialize(entry, ProposalJson), ProposalJson)!;
            copy.Resolves = entry.Resolves!.Where(id => !stale.Contains(id.Trim())).ToList();
            kept.Add(copy);
            dropped.Add((entry.Ref ?? string.Empty, stale));
        }

        return (kept, dropped);
    }

    /// <summary>Puts what belongs to the entries of <paramref name="before"/> on the same entries (by ref and kind) of
    /// <paramref name="after"/>. Always what saving already made of them (<see cref="WorldDraft.SavedAs"/>), so an apply that failed
    /// part way never makes those again. With <paramref name="decisions"/> (more entries added to the same proposal), also what the
    /// DM decided (ticks, the cast box, a new name), a person's gear and, for an edit, the text it was proposed against: an edit
    /// whose target changed since stays refused. For a proposal made afresh, only an entry that is plainly the same one keeps what it
    /// became (and a person what was picked for them to carry): the same name, or for an edit the same target and text (one with a new text should be applied, and is checked against the stamp).</summary>
    internal static void CarryOver(WorldBundle before, WorldBundle after, bool decisions)
    {
        foreach (var draft in after.Drafts)
        {
            if (before.Find(draft.Ref) is not { } old || old.Kind != draft.Kind) continue;
            // The issues an edit says it fixes belong to what it edits: a revision of it keeps them (unless it lists its own "resolves", even an empty
            // list, which replaces them: the way to withdraw a claim), and one that now edits something else does not.
            if (old.Resolves.Count > 0 && !draft.ResolvesGiven && string.Equals(old.Target, draft.Target, StringComparison.OrdinalIgnoreCase))
            {
                draft.Resolves = draft.Resolves.Concat(old.Resolves).Distinct(StringComparer.Ordinal).ToList();
            }

            if (decisions)
            {
                draft.Selected = old.Selected;
                draft.Cast = old.Cast;
                draft.Bestiary = old.Bestiary;
                draft.PageTitle = old.PageTitle;
                draft.Stats ??= old.Stats;
                draft.Gear ??= old.Gear;
                if (!string.IsNullOrWhiteSpace(old.Name) && old.Name != old.OutlineName) draft.Name = old.Name;
                if (old.Kind == DraftKinds.Edit && string.Equals(old.Target, draft.Target, StringComparison.OrdinalIgnoreCase))
                {
                    draft.Before = old.Before;
                    draft.BeforeStamp = old.BeforeStamp;
                    draft.BeforeProperties = old.BeforeProperties;
                    draft.BeforeTags = old.BeforeTags;
                    draft.BeforeSheet = old.BeforeSheet;
                    draft.BeforePlace = old.BeforePlace;
                    draft.BeforeLineup = old.BeforeLineup;
                    draft.BeforeStatus = old.BeforeStatus;
                }
            }
            else
            {
                // The same person proposed again carries what was picked for them.
                if (draft.Kind == DraftKinds.Npc && SameEntry(old, draft)) draft.Gear ??= old.Gear;
                if (old.SavedAs.Count == 0 || !SameEntry(old, draft)) continue;
            }

            draft.SavedAs = new Dictionary<string, string>(old.SavedAs);
        }

        static bool SameEntry(WorldDraft old, WorldDraft draft) => old.Kind switch
        {
            // The same change to the same thing: a rename, a move, a retype or a delete is not the text edit that came before it.
            DraftKinds.Edit => string.Equals(old.Target, draft.Target, StringComparison.OrdinalIgnoreCase) && old.Body == draft.Body && old.Remove == draft.Remove
                && old.Rename == draft.Rename && old.MoveTo == draft.MoveTo && old.Parent == draft.Parent && old.PlaceKind == draft.PlaceKind
                && old.RegionKind == draft.RegionKind && old.SuggestedStats == draft.SuggestedStats
                && old.Place == draft.Place && old.PlaceGiven == draft.PlaceGiven && old.LineupGiven == draft.LineupGiven && old.Status == draft.Status && old.Retag == draft.Retag
                && old.Tags.SequenceEqual(draft.Tags, StringComparer.Ordinal) && SameLineup(old.Lineup, draft.Lineup)
                && old.HazardsLeftOutGiven == draft.HazardsLeftOutGiven && old.HazardsLeftOut.SequenceEqual(draft.HazardsLeftOut, StringComparer.Ordinal)
                && old.SheetSet.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(draft.SheetSet.OrderBy(p => p.Key, StringComparer.Ordinal)) && old.SheetClear.SequenceEqual(draft.SheetClear),
            DraftKinds.Tags => false, // tagging again changes nothing that is already tagged
            _ => new[] { old.OutlineName, old.Name }.Any(name => string.Equals(name.Trim(), draft.Name.Trim(), StringComparison.OrdinalIgnoreCase))
        };
    }

    private static bool SameLineup(List<DraftLine> a, List<DraftLine> b) =>
        a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First.Entry, p.Second.Entry, StringComparison.OrdinalIgnoreCase) && p.First.Count == p.Second.Count && p.First.Note == p.Second.Note && p.First.Hidden == p.Second.Hidden);

    private async Task<string> ProposeAsync(ProposalInput input, string tool = "propose_changes", string with = "the whole proposal", bool fresh = true)
    {
        var json = JsonSerializer.Serialize(input, ProposalJson);
        var check = await builder.CheckAsync(campaign, access, json);
        if (!check.Ok)
        {
            // The errors quote the campaign's own names (a page title, a place), which anyone who can write a page chose: fenced, as
            // every other result holding campaign text is (R14).
            return $"Nothing was proposed. Fix these and call {tool} again with {with}:\n" +
                   Fenced("- " + string.Join("\n- ", check.Errors) + (check.Warnings.Count > 0 ? "\nAlso worth fixing:\n- " + string.Join("\n- ", check.Warnings) : ""));
        }

        Held = input;
        if (fresh)
        {
            Document.ProposalEpoch++;
            Document.Decided = new List<string>();
            // The issues open before an earlier proposal's half-done apply belong to that proposal: this one's claims were checked against the issues open now,
            // and its apply takes its own "before". Kept, they would read issues fixed meanwhile as fixed by it and hide one it brings back.
            Document.ApplyBeforeIssueIds = null;
            // A proposal made again after an apply that failed part way: what was made then is not made twice.
            if (Proposal is { } waiting) CarryOver(waiting, check.Bundle, decisions: false);
        }

        Proposal = check.Bundle;
        Document.ProposalWarnings = check.Warnings.ToList();
        // The difficulty of an encounter is information for the model to weigh, never a warning: nothing is wrong with a hard fight.
        var readouts = check.Bundle.Drafts.Where(d => d.Readout != null).Select(d => $"{d.Name}: {d.Readout}").ToList();
        return $"The proposal ({check.Bundle.Drafts.Count} entries) is ready for the game master to review and apply; nothing is saved yet. Tell them in a sentence or two what it adds." +
               (check.Warnings.Count > 0 ? "\nWarnings (fix them with a new proposal if they matter):\n" + Fenced("- " + string.Join("\n- ", check.Warnings)) : "") +
               (readouts.Count > 0 ? "\nDifficulty readout for the party (information, not a problem):\n" + Fenced("- " + string.Join("\n- ", readouts)) : "");
    }

    private static readonly JsonSerializerOptions ProposalJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // A step's arguments as the DM reads them: indented, nothing escaped that needn't be.
    private static readonly JsonSerializerOptions StepJson = new(ProposalJson) { WriteIndented = true };
}
