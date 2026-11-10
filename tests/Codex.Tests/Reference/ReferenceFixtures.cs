using Codex.SystemCheck;

namespace Codex.Tests.Reference;

/// <summary>Where the reference fixtures are, and the ledger of the expectations that pass today.</summary>
public static class ReferenceFixtures
{
    public static readonly string Root = Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", "reference");
    public static readonly string LedgerFile = Path.Combine(Root, "progress.txt");

    public static IReadOnlyList<string> Roots() =>
        Directory.GetDirectories(Root).Where(d => File.Exists(Path.Combine(d, ReferenceRunner.SpecFile))).Order(StringComparer.Ordinal).ToList();

    /// <summary>The ids that pass today, from progress.txt: one per line, <c>#</c> starts a comment.</summary>
    public static IReadOnlySet<string> Ledger() =>
        File.ReadAllLines(LedgerFile).Select(l => (l.IndexOf('#') is var hash and >= 0 ? l[..hash] : l).Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
}
