using Codex.Core.Rules;

namespace Codex.Persistence.Runtime;

/// <summary>Why a prepared encounter's start changed nothing at the table.</summary>
public enum PreparedStartRefusal
{
    /// <summary>An encounter with participants was already running when the start reached the loop.</summary>
    EncounterRunning,

    /// <summary>None of the actors the start named is at the table (each failed to join).</summary>
    NobodyAtTable
}

/// <summary>
/// Starts a prepared encounter at the table (ENCOUNTER_BUILDER_DESIGN.md 4.2), on the loop, after <c>PreparedEncounterService.StartAsync</c> made its creatures.
/// It sets the encounter's place and plan, and rolls each named actor's initiative through <see cref="RollInitiativeCommand"/>. The bonus and tie-break come
/// from <see cref="TableInitiative"/> over the actor's live sheet, as the console works them out. Its own log line names the plan and the place as GM-only
/// terms. Each roll's own "Initiative" line follows it, as a roll at the console writes it (<see cref="IFollowedByEvents"/>), so a Hidden creature's roll is
/// redacted exactly as today. Results are read off the command once it is applied, as <see cref="MakeCheckCommand.Result"/> is.
/// </summary>
public sealed record StartPreparedEncounterCommand(
    string PreparedId,
    string Name,
    string? PlaceId,
    string PlaceLabel,
    IReadOnlyList<string> ActorIds,
    string RollerUserId) : IRuntimeCommand, IFollowedByEvents
{
    private readonly List<RollInitiativeCommand> _rolls = new();
    private readonly List<string> _skipped = new();

    /// <summary>Once applied: why nothing changed, or null when the encounter started.</summary>
    public PreparedStartRefusal? Refusal { get; private set; }

    /// <summary>Once applied and started: the live encounter's id.</summary>
    public string? EncounterId { get; private set; }

    /// <summary>Once applied: the named actors that are not in the order. Either the table does not hold them, or their roll could not be read.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>Once applied: the actors that rolled and joined the order.</summary>
    public IReadOnlyList<string> Joined => _rolls.Select(r => r.ActorId).ToList();

    // Counters that refill on entering the order change the actor's state: each inner roll reports them (SetInitiativeCommand), and they are persisted.
    public IReadOnlyCollection<string> AffectedActorIds => _rolls.SelectMany(r => r.AffectedActorIds).Distinct().ToArray();

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.ActiveEncounter is { Participants.Count: > 0 })
        {
            Refusal = PreparedStartRefusal.EncounterRunning;
            return;
        }

        var held = new List<(string Id, ActorDocument Doc)>();
        foreach (var id in ActorIds.Distinct(StringComparer.Ordinal))
        {
            if (runtime.GetActorDocument(id) is not { } doc || doc.Retired || runtime.GetEntity(id) == null)
            {
                _skipped.Add(id);
                continue;
            }

            held.Add((id, doc));
        }

        // Before GetOrCreateEncounter, so a start that joins nobody leaves no empty encounter behind.
        if (held.Count == 0)
        {
            Refusal = PreparedStartRefusal.NobodyAtTable;
            return;
        }

        var system = runtime.System;
        var engine = system == null ? null : new SheetRuleEngine(system, runtime.Resolver);
        var die = TableInitiative.Die(system);
        foreach (var (id, doc) in held)
        {
            // Everything that can fail for one actor (its sheet, a die the system wrote badly) skips that actor and no one else: the numbers are worked out before the roll
            // and the roll fails before it records anything, so a skipped actor leaves nothing half-applied.
            try
            {
                var bonus = TableInitiative.Bonus(system, engine, () => runtime.LiveSheetOf(doc), () => runtime.LinkedFor(doc.Sheet));
                var roll = new RollInitiativeCommand(id, TableInitiative.Expression(die, bonus), RollerUserId, TableInitiative.TieBreak(system, doc.Sheet));
                roll.Apply(runtime);
                _rolls.Add(roll);
            }
            catch (Exception)
            {
                _skipped.Add(id);
            }
        }

        // Nobody rolled (the system's die is unreadable): nothing was added to the order, so there is nothing to start. No plan or place is set either.
        if (_rolls.Count == 0 || runtime.ActiveEncounter is not { } encounter)
        {
            Refusal = PreparedStartRefusal.NobodyAtTable;
            return;
        }

        encounter.PreparedEncounterId = PreparedId;
        if (!string.IsNullOrWhiteSpace(PlaceId)) encounter.LocationId = PlaceId;
        runtime.MarkEncounterDirty();
        EncounterId = encounter.Id;
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = EventTerm.GmOnly(Name);
        if (Refusal is { } refused)
        {
            return SessionEvent.From("EncounterStarted", new[]
            {
                EventMessage.Of(refused == PreparedStartRefusal.EncounterRunning ? "Encounter.StartRefused" : "Encounter.StartEmpty", name)
            });
        }

        var parts = new List<EventMessage>
        {
            string.IsNullOrWhiteSpace(PlaceId) ? EventMessage.Of("Encounter.StartedNoPlace", name) : EventMessage.Of("Encounter.Started", name, EventTerm.GmOnly(PlaceLabel))
        };
        if (_skipped.Count > 0) parts.Add(EventMessage.Of("Encounter.NotJoined", name, _skipped.Count));
        return SessionEvent.From("EncounterStarted", parts);
    }

    public IReadOnlyList<SessionEvent> EventsAfter(CampaignRuntime runtime) => _rolls.Select(r => r.Describe(runtime)).ToList();
}
