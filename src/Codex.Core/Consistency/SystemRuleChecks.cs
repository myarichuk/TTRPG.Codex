using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Consistency;

/// <summary>
/// The checks a game system adds (<c>issueChecks</c> in system.yaml): rules over the sheet's own keys (layer 2) and compiled checks (layer 3). Every
/// rule is data read here, with no formula and no model; a compiled check is called inside a guard, and one that cannot be had, throws or returns what
/// cannot be read is one <c>info</c> issue of its own (<see cref="CheckNames.CheckFailed"/>), never a failure of the run. A retired character is never
/// checked, and nothing is reported where the campaign's rule enforcement is <c>off</c> (the DM has said the system's rules do not apply here).
/// <para>A compiled check runs on a thread of its own with a time limit (<see cref="CompiledTimeout"/>, 5 s unless the host says otherwise) and a token
/// (<c>ICodexIssueCheck.Check(world, cancel)</c>, cancelled at the limit or when the run is): one that does not return in time is one <c>warning</c>
/// issue (<see cref="CheckNames.CheckFailed"/>, <c>…:issueChecks:&lt;id&gt;:timed-out</c>) and the run goes on with the next check. .NET cannot stop a thread,
/// so a check that ignores its token is abandoned, not killed: it runs on until it returns, its result and any exception it ends with are dropped (observed,
/// never unobserved), and while it still runs the same check is not started again (each later run reports it as timed out at once), so a check that never
/// returns holds one thread, not one per run. Its memory cannot be bounded in the process at all.</para>
/// </summary>
public sealed class SystemRuleCheck : IConsistencyCheck
{
    public string Name => CheckNames.SystemRule;

    /// <summary>The most issues one compiled check may return; the rest are dropped.</summary>
    public const int MaxCompiledIssues = 200;

    /// <summary>How long a compiled check may take when the host does not say: generous for code over plain copies of a campaign, short enough that a
    /// check that never returns does not hold up a run.</summary>
    public static readonly TimeSpan DefaultCompiledTimeout = TimeSpan.FromSeconds(5);

    public SystemRuleCheck() : this(DefaultCompiledTimeout)
    {
    }

    /// <param name="compiledTimeout">How long each compiled check may take (<see cref="CompiledTimeout"/>); <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    public SystemRuleCheck(TimeSpan compiledTimeout)
    {
        if (compiledTimeout < TimeSpan.Zero && compiledTimeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(compiledTimeout));
        CompiledTimeout = compiledTimeout;
    }

    /// <summary>How long each compiled check (layer 3) may take before it is given up on and reported as timed out. Data rules (layer 2) have no limit
    /// of their own: they are the host's code and look at the run's token.</summary>
    public TimeSpan CompiledTimeout { get; }

    // The compiled checks given up on that have not returned yet, by instance (one instance serves every run of its system): a check still running is not
    // started again. Keyed weakly, so a system that is unloaded takes its entries with it.
    private static readonly ConditionalWeakTable<ICodexIssueCheck, Task> StillRunning = new();

    public IEnumerable<Issue> Run(WorldSnapshot s) => Run(s, CancellationToken.None);

    public IEnumerable<Issue> Run(WorldSnapshot s, CancellationToken cancel)
    {
        if (s.System is not { IssueChecks.Count: > 0 } system || s.Enforcement == "off") yield break;
        var keys = IssueCheckProblems.Keys(system);
        foreach (var check in system.IssueChecks.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
        {
            cancel.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(check.Type))
            {
                foreach (var issue in Compiled(s, system, check, cancel)) yield return issue;
            }
            else
            {
                foreach (var issue in Rule(s, system, check, keys)) yield return issue;
            }
        }
    }

    // ---- layer 2: rules ----

    private IEnumerable<Issue> Rule(WorldSnapshot s, SystemDefinition system, IssueCheckDefinition check, IReadOnlyDictionary<string, string?> keys)
    {
        if (string.IsNullOrWhiteSpace(check.Field) || string.IsNullOrWhiteSpace(check.Rule)) yield break;
        var field = Canonical(keys, check.Field);
        var label = LabelOf(system, field);
        var roles = check.Roles.Count == 0 ? new[] { "pc", "npc" } : check.Roles.ToArray();
        var severity = check.Severity switch { "error" => IssueSeverity.Error, "info" => IssueSeverity.Info, _ => IssueSeverity.Warning };
        var editable = SheetProposal.IsEditable(system, field);

        foreach (var actor in s.Actors.Where(a => !a.Retired && roles.Contains(RoleWord(a.Role))))
        {
            if (check.When is { } when && !Holds(when, actor.Sheet, keys)) continue;
            var found = Judge(check, field, actor, s, system);
            if (found == null) continue;
            var (value, shown) = found.Value;
            var message = Fill(check.Message ?? string.Empty, actor.Name, label, shown, check);
            var args = new[] { actor.Name, label, shown };
            yield return Make.Issue(Name, severity, "actor", actor.Id, field, check.Id, check.Code ?? "Consistency.System." + Pascal(check.Id), args, message,
                check.FixHint ?? DefaultHint(check, label),
                // A sheet edit never reaches a bestiary creature or a hazard (the checks of their sheets are not the gate's), so a rule that names them is report-only.
                editable && actor.Role != ActorRole.Monster ? FixVia.Sheet : FixVia.ReportOnly);
        }
    }

    // The value shown in the message, or null when the sheet is fine.
    private static (object? Value, string Shown)? Judge(IssueCheckDefinition check, string field, ActorView actor, WorldSnapshot s, SystemDefinition system)
    {
        actor.Sheet.TryGetValue(field, out var value);
        switch (check.Rule)
        {
            case "required":
                return IsEmpty(value) ? (value, "—") : null;
            case "empty":
                return IsEmpty(value) ? null : (value, Display(value));
            case "range":
                if (IsEmpty(value) || ToNumber(value) is not { } number) return null;
                return check.Min is { } lo && number < lo || check.Max is { } hi && number > hi ? (value, Num(number)) : null;
            case "count":
                var count = SheetRuleEngine.RawList(actor.Sheet, field).Count;
                return check.Min is { } min && count < min || check.Max is { } max && count > max ? (count, Num(count)) : null;
            case "reference":
                var types = TypesOf(check, field, system);
                if (types.Count == 0) return null;
                var missing = (value is string text ? new List<string> { text } : SheetRuleEngine.RawList(actor.Sheet, field).Select(p => SheetRuleEngine.ParsePick(p).Id).ToList())
                    .Where(id => id.Length > 0 && !types.Any(t => s.Library.Resolve(t, id) != null)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
                return missing.Count == 0 ? null : (missing[0], string.Join(", ", missing));
            default:
                return null;
        }
    }

    private static IReadOnlyList<string> TypesOf(IssueCheckDefinition check, string field, SystemDefinition system)
    {
        if (!string.IsNullOrWhiteSpace(check.TargetEntityType)) return new[] { check.TargetEntityType };
        if (system.ActorFields.FirstOrDefault(f => string.Equals(f.Key, field, StringComparison.OrdinalIgnoreCase)) is { Type: FieldType.EntityReference, TargetEntityType: { Length: > 0 } t }) return new[] { t };
        if (system.SheetRules?.Inventory is { } inventory && string.Equals(inventory.Key, field, StringComparison.OrdinalIgnoreCase)) return inventory.Types;
        if (system.SheetRules?.Lists.FirstOrDefault(l => string.Equals(l.Key, field, StringComparison.OrdinalIgnoreCase)) is { } list) return new[] { list.TargetEntityType };
        return Array.Empty<string>();
    }

    private static bool Holds(IssueCondition when, IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, string?> keys)
    {
        sheet.TryGetValue(Canonical(keys, when.Field), out var value);
        if (when.NotEmpty == true) return !IsEmpty(value);
        if (when.Empty == true) return IsEmpty(value);
        var text = Display(value);
        if (when.Is != null) return string.Equals(text, when.Is, StringComparison.OrdinalIgnoreCase);
        return when.In?.Any(o => string.Equals(text, o, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string Canonical(IReadOnlyDictionary<string, string?> keys, string key) => keys.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;

    private static string LabelOf(SystemDefinition system, string key) =>
        system.Attributes.FirstOrDefault(a => a.Key == key)?.Label ?? system.ActorFields.FirstOrDefault(f => f.Key == key)?.Label
        ?? (system.SheetRules?.Inventory?.Key == key ? system.SheetRules.Inventory.Label : null) ?? system.SheetRules?.Lists.FirstOrDefault(l => l.Key == key)?.Label ?? key;

    private static string RoleWord(ActorRole role) => role switch { ActorRole.PlayerCharacter => "pc", ActorRole.NonPlayerCharacter => "npc", _ => "monster" };

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        System.Collections.IEnumerable list => !list.Cast<object?>().Any(i => !string.IsNullOrWhiteSpace(i?.ToString())),
        _ => false
    };

    private static double? ToNumber(object? value) => value switch
    {
        null => null,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        IConvertible c when value is not bool => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => null
    };

    private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Display(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        System.Collections.IEnumerable list => string.Join(", ", list.Cast<object?>().Select(i => i?.ToString() ?? string.Empty)),
        IConvertible c => Convert.ToString(c, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string Fill(string message, string name, string label, string value, IssueCheckDefinition check) =>
        Regex.Replace(message, @"\{(name|field|value|min|max)\}", m => m.Groups[1].Value switch
        {
            "name" => Make.Quote(name),
            "field" => label,
            "value" => Make.Quote(value),
            "min" => check.Min is { } lo ? Num(lo) : string.Empty,
            _ => check.Max is { } hi ? Num(hi) : string.Empty
        });

    private static string DefaultHint(IssueCheckDefinition check, string label) => check.Rule switch
    {
        "required" => $"Ask the DM what {label} should be, then propose a sheet edit of it with exactly what they say. Do not invent a value.",
        "empty" => $"Ask the DM whether {label} should be cleared; if so, propose a sheet edit that clears it. Do not decide for them.",
        "range" => $"Ask the DM what {label} should be within the limit, then propose a sheet edit. Do not guess a value.",
        "reference" => $"Ask the DM which entry {label} should name (search_library lists them), then propose a sheet edit. Do not invent an id.",
        _ => $"Ask the DM what {label} should hold, then propose a sheet edit. Do not invent entries."
    };

    private static string Pascal(string id) => string.Concat(id.Split('-', '_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    // ---- layer 3: compiled checks ----

    private IEnumerable<Issue> Compiled(WorldSnapshot s, SystemDefinition system, IssueCheckDefinition check, CancellationToken cancel)
    {
        var (issues, failure) = Call(s, system, check, cancel);
        var owner = s.Campaign.Id.Length > 0 ? s.Campaign.Id : system.Id;
        if (ReferenceEquals(failure, TimedOut))
        {
            var seconds = CompiledTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            yield return Make.Issue(CheckNames.CheckFailed, IssueSeverity.Warning, "campaign", owner, "issueChecks", check.Id + ":timed-out", "Consistency.CheckTimedOut",
                new[] { check.Id, seconds },
                $"The system's check \"{Make.Quote(check.Id)}\" did not finish within {seconds} s, so what it looks for was not checked.",
                "Tell the DM; the system's own check is too slow or never ends (a fault in the system, not in the campaign). Nothing can be proposed about it.", FixVia.ReportOnly);
            yield break;
        }

        if (failure != null)
        {
            yield return Make.Issue(CheckNames.CheckFailed, IssueSeverity.Info, "campaign", owner, "issueChecks", check.Id, "Consistency.CheckFailed", new[] { check.Id, failure },
                $"The system's check \"{Make.Quote(check.Id)}\" could not run ({failure}), so what it looks for was not checked.",
                "Tell the DM; this is a fault in the system's own check, not in the campaign. Nothing can be proposed about it.", FixVia.ReportOnly);
            yield break;
        }

        foreach (var issue in issues!) yield return issue;
    }

    // The failure that is a timeout (compared by reference), and what says so inside (never an exception a check can throw itself).
    private static readonly string TimedOut = new("timed out".AsSpan());

    private sealed class GaveUp : Exception;

    // Not in the iterator: it cannot yield from a catch.
    private (List<Issue>? Issues, string? Failure) Call(WorldSnapshot s, SystemDefinition system, IssueCheckDefinition check, CancellationToken cancel)
    {
        if (!system.Compiled.TryGetValue(check.Type!, out var supplier)) return (null, "its type was not looked for");
        if (supplier.Problem != null) return (null, supplier.Problem);
        if (supplier.Instance is not ICodexIssueCheck instance) return (null, "its type is not an ICodexIssueCheck");

        List<CodexIssue> returned;
        try
        {
            returned = Bounded(instance, WorldOf(s, system), cancel);
        }
        catch (GaveUp)
        {
            return (null, TimedOut);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return (null, e.GetType().Name);
        }

        var issues = new List<Issue>();
        foreach (var row in returned)
        {
            if (Read(row, check) is not { } issue) return (null, "it returned an issue the host cannot read (an unknown kind, severity or an empty id)");
            issues.Add(issue);
        }

        return (issues, null);
    }

    /// <summary>
    /// What <paramref name="instance"/> returns for <paramref name="world"/>, at most <see cref="MaxCompiledIssues"/> rows (read on the check's own thread, so
    /// a list that is slow to read is inside the limit too). Throws <see cref="GaveUp"/> when it does not return within <see cref="CompiledTimeout"/>
    /// (or is still running from an earlier run that gave up on it), <see cref="OperationCanceledException"/> when <paramref name="cancel"/> is cancelled first,
    /// and what the check threw otherwise. A check given up on is told so through its token and left to finish alone; what it ends with is observed.
    /// </summary>
    private List<CodexIssue> Bounded(ICodexIssueCheck instance, CodexIssueWorld world, CancellationToken cancel)
    {
        lock (StillRunning)
        {
            if (StillRunning.TryGetValue(instance, out var earlier) && !earlier.IsCompleted) throw new GaveUp();
        }

        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var token = stop.Token;
        // A thread of its own (not one of the pool's): a check that never returns must not hold a pool thread the app's requests need.
        var task = Task.Factory.StartNew(() => (instance.Check(world, token) ?? Array.Empty<CodexIssue>()).Take(MaxCompiledIssues).ToList(),
            CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var limit = CompiledTimeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Min(int.MaxValue, Math.Ceiling(CompiledTimeout.TotalMilliseconds));
        bool finished;
        try
        {
            finished = task.IsCompleted || WaitFor(task, limit, cancel);
        }
        catch (OperationCanceledException)
        {
            GiveUp(instance, task, stop);
            throw;
        }

        if (!finished)
        {
            GiveUp(instance, task, stop);
            throw new GaveUp();
        }

        stop.Dispose();
        if (task.IsFaulted) ExceptionDispatchInfo.Capture(task.Exception!.InnerException ?? task.Exception).Throw();
        // Cancelled by itself (it threw a cancellation of its own, the run's token being still live): a failure of the check like any other.
        if (task.IsCanceled) throw new TaskCanceledException();
        return task.Result;
    }

    // True when the task ended within the limit, false when it did not; a cancellation of the run throws.
    private static bool WaitFor(Task task, int limit, CancellationToken cancel)
    {
        try
        {
            return task.Wait(limit, cancel);
        }
        catch (AggregateException)
        {
            return true; // it ended, faulted or cancelled: the caller reads which
        }
    }

    // Tells the check to stop, and leaves it: what it ends with is observed, its token source is let go when it ends, and until then it is not started again.
    private static void GiveUp(ICodexIssueCheck instance, Task task, CancellationTokenSource stop)
    {
        // Its token's callbacks run elsewhere (CancelAsync), so one the check registered can neither hold up nor throw into the run; what they throw is observed.
        Abandoned.Observe(stop.CancelAsync());
        Abandoned.Observe(task);
        task.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), stop, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (StillRunning)
        {
            StillRunning.AddOrUpdate(instance, task);
        }
    }

    private Issue? Read(CodexIssue row, IssueCheckDefinition check)
    {
        if (row == null || row.Kind is not ("actor" or "page" or "place") || string.IsNullOrWhiteSpace(row.EntityId) || string.IsNullOrWhiteSpace(row.Message)) return null;
        var severity = row.Severity switch { "error" => IssueSeverity.Error, "warning" => IssueSeverity.Warning, "info" => IssueSeverity.Info, _ => (IssueSeverity?)null };
        if (severity == null) return null;
        var detail = string.IsNullOrWhiteSpace(row.Detail) ? check.Id : check.Id + "." + WikiText.Slugify(row.Detail);
        return Make.Issue(Name, severity.Value, row.Kind, row.EntityId.Trim(), string.IsNullOrWhiteSpace(row.Field) ? "-" : row.Field.Trim(), detail,
            string.IsNullOrWhiteSpace(row.Code) ? "Consistency.System." + Pascal(check.Id) : row.Code.Trim(), (row.Args ?? Array.Empty<string>()).Select(a => a ?? string.Empty).ToList(),
            row.Message.Trim(), string.IsNullOrWhiteSpace(row.FixHint) ? "Tell the DM; the system's own check found this." : row.FixHint.Trim(), FixVia.ReportOnly);
    }

    private static CodexIssueWorld WorldOf(WorldSnapshot s, SystemDefinition system) => new(system.Id,
        s.Actors.Select(a => new CodexIssueActor(a.Id, a.Name, RoleWord(a.Role), a.Retired, a.Sheet.ToDictionary(p => p.Key, p => Plain(p.Value), StringComparer.Ordinal))).ToList(),
        s.Pages.Select(p => new CodexIssuePage(p.Id, p.Title, p.Kind, p.PartyVisible, (p.Tags ?? Array.Empty<string>()).ToList())).ToList(),
        s.Places.Select(p => new CodexIssuePlace(p.Id, p.RegionId, p.Name, p.ParentId)).ToList());

    // A copy a check may change without changing the snapshot: numbers, text and flags as they are, lists as new lists.
    private static object? Plain(object? value) => value switch
    {
        null => null,
        string or bool => value,
        System.Collections.IDictionary => null,
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Plain).ToList(),
        IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
