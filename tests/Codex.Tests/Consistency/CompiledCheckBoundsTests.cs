using System.Diagnostics;
using Codex.Core.Consistency;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;

namespace Codex.Tests.Consistency;

/// <summary>
/// A system's compiled checks (layer 3, <see cref="ICodexIssueCheck"/>) run with a time limit and a token (docs/WORLD_BUILDER.md, section 9): one that runs
/// over is one warning with a stable id and the run goes on; one that ignores its token is abandoned, not killed, and not started again while it runs; the
/// checks that finished give what they always gave; a check written before the token existed works unchanged.
/// </summary>
public class CompiledCheckBoundsTests
{
    private const string TimedOutId = "check-failed:campaign:c1:issueChecks:slow:timed-out";

    private static readonly ActorView[] Cast = { Fx.Actor("actors/1-A", "Bartholomew Featherstonehaugh"), Fx.Actor("actors/2-A", "Ann") };

    /// <summary>A system whose issue checks are the given compiled instances, in order, each under its id.</summary>
    private static SystemDefinition SystemOf(params (string Id, ICodexIssueCheck Check)[] checks)
    {
        var yaml = "id: demo\nname: Demo\nissueChecks:\n" + string.Concat(checks.Select(c => $"  - {{ id: {c.Id}, type: \"Tests.{c.Id}, Tests\" }}\n"));
        var system = SystemDefinitionLoader.Parse(yaml);
        foreach (var (id, check) in checks) system.AddCompiled($"Tests.{id}, Tests", new CompiledSupplier(check, null));
        return system;
    }

    private static IReadOnlyList<Issue> Run(SystemDefinition system, TimeSpan timeout, CancellationToken cancel = default) =>
        ConsistencyChecker.Run(Fx.Snap(actors: Cast, system: system, campaign: Fx.Campaign()), new IConsistencyCheck[] { new SystemRuleCheck(timeout) }, cancel);

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    // ---- the checks ----

    /// <summary>Written before the token existed: implements only <c>Check(world)</c>. A long name is an info issue.</summary>
    private sealed class OldStyle : ICodexIssueCheck
    {
        public IReadOnlyList<CodexIssue> Check(CodexIssueWorld world) => world.Actors.Where(a => a.Name.Length > 20).OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new CodexIssue("actor", a.Id, "-", "info", "Consistency.System.LongName", new[] { a.Name }, $"\"{a.Name}\" has a long name.")).ToList();
    }

    /// <summary>Overrides the token overload only meaningfully: the host must call it.</summary>
    private sealed class NewStyle : ICodexIssueCheck
    {
        public IReadOnlyList<CodexIssue> Check(CodexIssueWorld world) => throw new InvalidOperationException("the host must call the overload with the token");

        public IReadOnlyList<CodexIssue> Check(CodexIssueWorld world, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            return new[] { new CodexIssue("actor", "actors/2-A", "-", "warning", "Consistency.System.New", Array.Empty<string>(), "Ann is new.") };
        }
    }

    private sealed class Throws : ICodexIssueCheck
    {
        public IReadOnlyList<CodexIssue> Check(CodexIssueWorld world) => throw new InvalidOperationException("boom");
    }

    /// <summary>Waits for its token (up to 30 s) and says when it saw it.</summary>
    private sealed class HonoursTheToken : ICodexIssueCheck
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CodexIssue> Check(CodexIssueWorld world) => throw new InvalidOperationException("the host must call the overload with the token");

        public IReadOnlyList<CodexIssue> Check(CodexIssueWorld world, CancellationToken cancel)
        {
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30))) Stopped.TrySetResult();
            cancel.ThrowIfCancellationRequested();
            return Array.Empty<CodexIssue>();
        }
    }

    /// <summary>Knows nothing of tokens and runs until the test lets it go; then returns an issue, or throws.</summary>
    private sealed class IgnoresTheToken(bool throwAtTheEnd = false) : ICodexIssueCheck
    {
        private int starts;

        public ManualResetEventSlim Release { get; } = new(false);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts => Volatile.Read(ref starts);

        public IReadOnlyList<CodexIssue> Check(CodexIssueWorld world)
        {
            Interlocked.Increment(ref starts);
            try
            {
                Release.Wait();
                if (throwAtTheEnd) throw new InvalidOperationException("late failure of an abandoned check");
                return new[] { new CodexIssue("actor", "actors/1-A", "-", "warning", "Consistency.System.Slow", Array.Empty<string>(), "Slow but sure.") };
            }
            finally
            {
                Ended.TrySetResult();
            }
        }
    }

    // ---- the tests ----

    [Fact]
    public void ACheckThatRunsOverItsLimit_IsOneWarningWithAStableId_AndTheOtherChecksStillRun()
    {
        var slow = new IgnoresTheToken();
        var system = SystemOf(("long-name", new OldStyle()), ("slow", slow), ("newer", new NewStyle()));
        try
        {
            var watch = Stopwatch.StartNew();
            var issues = Run(system, Short);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.ElapsedMilliseconds} ms");
            var timedOut = Assert.Single(issues, i => i.Id == TimedOutId);
            Assert.Equal((CheckNames.CheckFailed, IssueSeverity.Warning, FixVia.ReportOnly, "Consistency.CheckTimedOut"), (timedOut.Check, timedOut.Severity, timedOut.FixVia, timedOut.Code));
            Assert.Equal(new[] { "slow", "0.2" }, timedOut.Args);
            Assert.Equal("The system's check \"slow\" did not finish within 0.2 s, so what it looks for was not checked.", timedOut.Message);
            Assert.Contains(issues, i => i.Id == "system-rule:actor:actors/1-A:-:long-name");
            Assert.Contains(issues, i => i.Id == "system-rule:actor:actors/2-A:-:newer");
        }
        finally
        {
            slow.Release.Set();
        }
    }

    [Fact]
    public void TheTimeoutIssue_ReadsTheSameInTheAppsText_AndEveryLanguageFillsItsBlanks()
    {
        var slow = new IgnoresTheToken();
        try
        {
            var issue = Assert.Single(Run(SystemOf(("slow", slow)), Short));
            Assert.Equal(issue.Message, IssueText.Message(Localizer.For("en"), issue));
            foreach (var language in new[] { "he", "ru" })
            {
                var text = IssueText.Message(Localizer.For(language), issue);
                Assert.DoesNotMatch(@"\{\d", text);
                Assert.NotEqual(issue.Message, text);
                Assert.Contains("slow", text);
            }
        }
        finally
        {
            slow.Release.Set();
        }
    }

    [Fact]
    public void AThrowingCheck_IsStillTheInfoIssueItWas_AndTheRunGoesOn()
    {
        var issues = Run(SystemOf(("boom", new Throws()), ("long-name", new OldStyle())), Short);

        var failed = Assert.Single(issues, i => i.Id == "check-failed:campaign:c1:issueChecks:boom");
        Assert.Equal((IssueSeverity.Info, "Consistency.CheckFailed"), (failed.Severity, failed.Code));
        Assert.Equal(new[] { "boom", "InvalidOperationException" }, failed.Args);
        Assert.Contains(issues, i => i.Id == "system-rule:actor:actors/1-A:-:long-name");
    }

    [Fact]
    public async Task ACheckThatHonoursItsToken_IsToldToStop_AtTheLimit_AndStops()
    {
        var check = new HonoursTheToken();

        var issues = Run(SystemOf(("slow", check)), Short);

        Assert.Equal(new[] { TimedOutId }, issues.Select(i => i.Id));
        // The token was cancelled when the host gave up, and the check saw it: its thread is free again.
        await check.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ACheckThatIgnoresItsToken_IsAbandoned_NotStartedAgainWhileItRuns_AndRunsAgainOnceItHasEnded()
    {
        var check = new IgnoresTheToken();
        var system = SystemOf(("slow", check));
        try
        {
            Assert.Equal(new[] { TimedOutId }, Run(system, Short).Select(i => i.Id));
            Assert.Equal(1, check.Starts);

            // Still running: the next run reports it at once, the same issue, and starts no second thread for it.
            var watch = Stopwatch.StartNew();
            Assert.Equal(new[] { TimedOutId }, Run(system, TimeSpan.FromSeconds(10)).Select(i => i.Id));
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.ElapsedMilliseconds} ms");
            Assert.Equal(1, check.Starts);

            // Let go, it ends on its own (its late result is dropped); the next run starts it again and, given time, reads it.
            check.Release.Set();
            await check.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(50); // the host's note that it ended runs just after
            Assert.Equal(new[] { "system-rule:actor:actors/1-A:-:slow" }, Run(system, TimeSpan.FromSeconds(10)).Select(i => i.Id));
            Assert.Equal(2, check.Starts);
        }
        finally
        {
            check.Release.Set();
        }
    }

    [Fact]
    public async Task AnAbandonedCheckThatFailsLater_LeavesNoUnobservedException()
    {
        var seen = false;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == "late failure of an abandoned check")) seen = true;
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await AbandonAndFailAsync();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(seen, "the abandoned check's exception went unobserved");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    // Out of line, so nothing of it (the check, its system, the host's record of the abandoned run) is still reachable when the collector runs.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonAndFailAsync()
    {
        var check = new IgnoresTheToken(throwAtTheEnd: true);
        Assert.Equal(new[] { TimedOutId }, Run(SystemOf(("slow", check)), Short).Select(i => i.Id));
        check.Release.Set();
        await check.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
    }

    [Fact]
    public void TheChecksThatFinished_GiveTheSameIssuesInTheSameOrder_WithOrWithoutOneThatTimesOut_RunAfterRun()
    {
        var slow = new IgnoresTheToken();
        try
        {
            var without = Run(SystemOf(("long-name", new OldStyle()), ("newer", new NewStyle())), Short).Select(Fx.Flat).ToList();
            var with = Run(SystemOf(("long-name", new OldStyle()), ("slow", slow), ("newer", new NewStyle())), Short).Select(Fx.Flat).ToList();
            var again = Run(SystemOf(("long-name", new OldStyle()), ("slow", slow), ("newer", new NewStyle())), Short).Select(Fx.Flat).ToList();

            Assert.Equal(without, with.Where(i => !i.StartsWith(TimedOutId, StringComparison.Ordinal)));
            Assert.Single(with, i => i.StartsWith(TimedOutId + "|", StringComparison.Ordinal));
            // The second run finds the slow check still running and says so at once: byte for byte the same list.
            Assert.Equal(with, again);
        }
        finally
        {
            slow.Release.Set();
        }
    }

    [Fact]
    public void AnOldStylePlugin_ThatKnowsNothingOfTokens_GivesWhatItAlwaysGave()
    {
        var issues = Run(SystemOf(("long-name", new OldStyle())), SystemRuleCheck.DefaultCompiledTimeout);

        var issue = Assert.Single(issues);
        Assert.Equal(("system-rule:actor:actors/1-A:-:long-name", IssueSeverity.Info, "Consistency.System.LongName"), (issue.Id, issue.Severity, issue.Code));
        // The interface's token overload, which the host calls, falls back to the old method.
        ICodexIssueCheck old = new OldStyle();
        Assert.Single(old.Check(new CodexIssueWorld("demo", new[] { new CodexIssueActor("a", new string('x', 30), "pc", false, new Dictionary<string, object?>()) },
            Array.Empty<CodexIssuePage>(), Array.Empty<CodexIssuePlace>()), CancellationToken.None));
    }

    [Fact]
    public void ARunThatIsCancelled_WhileACompiledCheckRuns_StopsWithACancellation_NotAPartialList()
    {
        var slow = new IgnoresTheToken();
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var watch = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => Run(SystemOf(("long-name", new OldStyle()), ("slow", slow)), TimeSpan.FromSeconds(30), stop.Token));
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.ElapsedMilliseconds} ms");
        }
        finally
        {
            slow.Release.Set();
        }
    }

    [Fact]
    public void TheDefaultLimit_IsFiveSeconds_AndANegativeOneIsRefused()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new SystemRuleCheck().CompiledTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), ConsistencyChecker.Default.OfType<SystemRuleCheck>().Single().CompiledTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, new SystemRuleCheck(Timeout.InfiniteTimeSpan).CompiledTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemRuleCheck(TimeSpan.FromSeconds(-2)));
    }
}
