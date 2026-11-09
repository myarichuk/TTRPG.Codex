using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Raven.Client.Documents;

namespace Codex.Persistence;

/// <summary>
/// One-time changes to stored documents, each run once per database. The start-up patches in <see cref="RavenDbService"/>
/// are safe to repeat, because nothing a user does can bring back what they fix (a renamed field, an 8-character invite
/// code). An upgrade here changes something a user may set back later on purpose, so running it on every start would undo
/// them. The ids of the upgrades a database has had are kept in one document, <see cref="LedgerId"/>.
/// <para>
/// An upgrade that could not finish (it needs a system that is not loaded) is not recorded, and runs again on the next
/// start. Each upgrade must therefore be safe to run again on what it already changed.
/// </para>
/// </summary>
public static class DatabaseUpgrades
{
    public const string LedgerId = "codex/upgrades";

    /// <summary>Creatures created from a stat block before stat blocks were "as printed" (2026-11-08) were stored with the
    /// manual method, which caps scores at the system's maximum (a pit fiend's Strength 26 showed a problem).</summary>
    public const string PrintedStatBlocks = "2026-11-08.printed-stat-blocks";

    /// <summary>Campaigns stored before they recorded their system (overhaul step 10) record it as it is at this start, so a
    /// later change to the system is one the GM is told about.</summary>
    public const string SystemStamps = "2026-11-09.system-stamps";

    /// <summary>The upgrades a database has had.</summary>
    public sealed class Ledger
    {
        public List<string> Applied { get; set; } = new();
    }

    /// <summary>Runs every upgrade <paramref name="store"/> has not had yet; returns what each did, for the log.
    /// <paramref name="systemFor"/> gives a loaded system by id, or null.</summary>
    public static IReadOnlyList<string> Run(IDocumentStore store, Func<string, SystemDefinition?> systemFor)
    {
        var log = new List<string>();
        Ledger ledger;
        using (var session = store.OpenSession())
        {
            ledger = session.Load<Ledger>(LedgerId) ?? new Ledger();
        }

        var applied = ledger.Applied.Count;
        if (!ledger.Applied.Contains(PrintedStatBlocks) && PrintStatBlocks(store, systemFor, log)) ledger.Applied.Add(PrintedStatBlocks);
        if (!ledger.Applied.Contains(SystemStamps) && StampSystems(store, systemFor, log)) ledger.Applied.Add(SystemStamps);

        if (ledger.Applied.Count == applied) return log;
        using (var session = store.OpenSession())
        {
            session.Store(ledger, LedgerId);
            session.SaveChanges();
        }

        return log;
    }

    /// <summary>
    /// <see cref="PrintedStatBlocks"/>: an actor made from a stat block (<see cref="ActorDocument.BlueprintId"/>) whose sheet
    /// says <see cref="SheetRuleEngine.ManualMethod"/> becomes <see cref="SheetRuleEngine.PrintedMethod"/>, when that leaves
    /// every score as it is. Printed totals are the base scores; manual adds the bonuses and increase picks on top. So a
    /// creature whose DM gave it a bonus or an increase since keeps manual, and the log names it: switching would lower its
    /// scores. Base scores the DM typed in are base scores either way and survive the switch. True when every candidate was
    /// decided, false when one belongs to a campaign whose system is not loaded (it is retried on the next start).
    /// </summary>
    internal static bool PrintStatBlocks(IDocumentStore store, Func<string, SystemDefinition?> systemFor, List<string> log)
    {
        var systemOfCampaign = new Dictionary<string, string>();
        var candidates = new List<(string Id, string Name, string CampaignId, Dictionary<string, object> Sheet)>();
        using (var session = store.OpenSession())
        {
            using (var campaigns = session.Advanced.Stream(session.Query<CampaignDocument>(), out var campaignStats))
            {
                if (!FromCollection(campaignStats, log)) return false;
                while (campaigns.MoveNext()) systemOfCampaign[campaigns.Current.Id] = campaigns.Current.Document.SystemId;
            }

            using var actors = session.Advanced.Stream(session.Query<ActorDocument>(), out var actorStats);
            if (!FromCollection(actorStats, log)) return false;
            while (actors.MoveNext())
            {
                var actor = actors.Current.Document;
                if (string.IsNullOrEmpty(actor.BlueprintId)) continue;
                if (!string.Equals(YamlValues.GetString(actor.Sheet, SheetRuleEngine.MethodKey), SheetRuleEngine.ManualMethod, StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add((actors.Current.Id, actor.Name, actor.CampaignId, actor.Sheet));
            }
        }

        var complete = true;
        var switching = new List<string>();
        foreach (var (id, name, campaignId, sheet) in candidates)
        {
            var systemId = systemOfCampaign.GetValueOrDefault(campaignId);
            if (systemId is not { Length: > 0 } || systemFor(systemId) is not { } system)
            {
                complete = false;
                log.Add($"Stat blocks as printed: '{name}' ({id}) waits for system '{systemId}', which is not loaded.");
                continue;
            }

            if (system.SheetRules?.Attributes == null) continue;
            var changed = ScoresChangedByPrinting(system, sheet);
            if (changed.Count == 0)
            {
                switching.Add(id);
                continue;
            }

            log.Add($"Stat blocks as printed: '{name}' ({id}) stays manual, since as printed it would lose its bonuses and increases ({string.Join(", ", changed)}).");
        }

        // Loaded in batches: one request per batch, well inside a session's request limit.
        foreach (var batch in switching.Chunk(256))
        {
            using var session = store.OpenSession();
            foreach (var actor in session.Load<ActorDocument>(batch).Values)
            {
                if (actor != null) actor.Sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod;
            }

            session.SaveChanges();
        }

        if (switching.Count > 0) log.Add($"Stat blocks as printed: {switching.Count} creature sheet(s) switched from manual; their scores are unchanged.");
        return complete;
    }

    /// <summary>
    /// <see cref="SystemStamps"/>: each campaign without a <see cref="CampaignDocument.SystemStamp"/> gets its system's
    /// fingerprint as loaded now. True when every one was decided, false when one's system is not loaded (it is retried on the
    /// next start; saving the campaign stamps it too).
    /// </summary>
    internal static bool StampSystems(IDocumentStore store, Func<string, SystemDefinition?> systemFor, List<string> log)
    {
        var unstamped = new List<(string Id, string SystemId)>();
        using (var session = store.OpenSession())
        {
            using var campaigns = session.Advanced.Stream(session.Query<CampaignDocument>(), out var stats);
            if (!FromCollection(stats, log)) return false;
            while (campaigns.MoveNext())
            {
                if (campaigns.Current.Document.SystemStamp == null) unstamped.Add((campaigns.Current.Id, campaigns.Current.Document.SystemId));
            }
        }

        var complete = true;
        var now = DateTime.UtcNow;
        var stamped = 0;
        foreach (var batch in unstamped.Chunk(256))
        {
            using var session = store.OpenSession();
            foreach (var campaign in session.Load<CampaignDocument>(batch.Select(c => c.Id)).Values)
            {
                if (campaign == null || campaign.SystemStamp != null) continue;
                if (systemFor(campaign.SystemId) is not { } system)
                {
                    complete = false;
                    log.Add($"System stamps: campaign '{campaign.Name}' ({campaign.Id}) waits for system '{campaign.SystemId}', which is not loaded.");
                    continue;
                }

                // A definition no loader read (a compiled plugin's own) has no fingerprint here; saving the campaign stamps it
                // from the catalog, which knows the plugin's DLL.
                if (system.Fingerprint is not { } fingerprint) continue;

                campaign.SystemStamp = SystemStamp.Of(campaign.SystemId, fingerprint, now);
                stamped++;
            }

            session.SaveChanges();
        }

        if (stamped > 0) log.Add($"System stamps: {stamped} campaign(s) now record the system they play under.");
        return complete;
    }

    /// <summary>
    /// A query with no filter is served from the collection itself, never from an index, so it cannot be stale. An index
    /// that is still catching up at start-up would hide documents, and the upgrade would be recorded as done without them.
    /// So this checks that the stream really came from the collection; if not, the run counts as unfinished and is retried.
    /// </summary>
    internal static bool FromCollection(Raven.Client.Documents.Session.StreamQueryStatistics stats, List<string> log)
    {
        if (stats.IndexName?.StartsWith("collection/", StringComparison.OrdinalIgnoreCase) == true) return true;
        log.Add($"Database upgrade: read through index '{stats.IndexName}', which may be stale; it runs again on the next start.");
        return false;
    }

    /// <summary>Each score that the printed method would total differently from manual on <paramref name="sheet"/>, as
    /// "str 28 → 26". Works on copies, so <paramref name="sheet"/> is left alone.</summary>
    internal static List<string> ScoresChangedByPrinting(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var engine = new SheetRuleEngine(system, NullEntryResolver.Instance);
        var manual = new Dictionary<string, object>(sheet) { [SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod };
        var printed = new Dictionary<string, object>(sheet) { [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod };
        engine.ApplyAttributeTotals(manual);
        engine.ApplyAttributeTotals(printed);
        return system.Attributes
            .Where(a => YamlValues.GetInt(manual, a.Key) != YamlValues.GetInt(printed, a.Key))
            .Select(a => $"{a.Key} {YamlValues.GetInt(manual, a.Key)} → {YamlValues.GetInt(printed, a.Key)}")
            .ToList();
    }
}
