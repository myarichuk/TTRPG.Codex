using System.Globalization;
using System.Text;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Golden;

/// <summary>
/// A small golden corpus for the Forged in the Dark system on the real pack (docs/SHEETKINDS_FEATURES_DESIGN.md, section 5): a plain
/// scoundrel, Mule at each load, Battleborn with its box marked, Foresight half spent, Expertise unpicked, picked and stale, and crews
/// with and without Fiends. The sheets are built here; <c>fitd/expected.txt</c> holds what the engine makes of each. A difference writes
/// <c>expected.actual.txt</c> beside it. When a change is meant, rerun with <c>CODEX_GOLDEN_FITD=update</c> (its own switch, so the
/// srd5e corpus is never rewritten by it) and review the diff of expected.txt like code.
/// </summary>
public class FitdGoldenCorpusTests : IAsyncLifetime
{
    private static readonly string ExpectedFile = Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Golden", "fitd", "expected.txt");
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "srd-fitd-2017" };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;
    private IEntryResolver _resolver = null!;

    public FitdGoldenCorpusTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition("fitd-srd")!;
        _resolver = new PackResolver(_registry);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("fitd-srd", type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("fitd-srd", type, Packs);
    }

    private static Dictionary<string, object> Sheet(string kind, params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = kind };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static List<object> Picks(params object[] picks) => picks.ToList();

    private SortedDictionary<string, Dictionary<string, object>> Sheets()
    {
        var engine = new SheetRuleEngine(_system, _resolver);
        var expertisePicked = Sheet("scoundrel", ("abilities", Picks("expertise")));
        engine.ChangeChoice(expertisePicked, "choice__expertise__expertise", new[] { "skirmish" });
        var expertiseStale = Sheet("scoundrel", ("abilities", Picks("expertise")));
        engine.ChangeChoice(expertiseStale, "choice__expertise__expertise", new[] { "no-such-action" });
        var actions = new Dictionary<string, object> { ["hunt"] = new Dictionary<string, object> { ["rating"] = 2L }, ["attune"] = new Dictionary<string, object> { ["rating"] = 1L } };
        return new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
        {
            ["scoundrel-plain"] = Sheet("scoundrel", ("actions", actions), ("stress", 3L), ("armor", 1L)),
            ["scoundrel-mule-light"] = Sheet("scoundrel", ("abilities", Picks("mule")), ("load", "light")),
            ["scoundrel-mule-normal"] = Sheet("scoundrel", ("abilities", Picks("mule")), ("load", "normal")),
            ["scoundrel-mule-heavy"] = Sheet("scoundrel", ("abilities", Picks("mule")), ("load", "heavy")),
            ["scoundrel-battleborn-box-marked"] = Sheet("scoundrel", ("abilities", Picks("battleborn")), ("special_armor", 1L)),
            ["scoundrel-two-armor-abilities"] = Sheet("scoundrel", ("abilities", Picks("battleborn", "shadow"))),
            ["scoundrel-box-marked-without-an-ability"] = Sheet("scoundrel", ("special_armor", 1L)),
            ["scoundrel-foresight-half-spent"] = Sheet("scoundrel", ("abilities", Picks("foresight", "analyst")), ("uses_foresight", 1L)),
            ["scoundrel-iron-will"] = Sheet("scoundrel", ("actions", actions), ("abilities", Picks("iron-will"))),
            ["scoundrel-expertise-unpicked"] = Sheet("scoundrel", ("abilities", Picks("expertise"))),
            ["scoundrel-expertise-picked"] = expertisePicked,
            ["scoundrel-expertise-stale"] = expertiseStale,
            ["crew-plain"] = Sheet("crew", ("turf", 2L), ("heat", 4L)),
            ["crew-lots-of-turf"] = Sheet("crew", ("turf", 8L)),
            ["crew-fiends"] = Sheet("crew", ("turf", 2L), ("wanted", 2L), ("abilities", Picks("fiends")))
        };
    }

    [Fact]
    public void EverySheetInTheCorpus_EvaluatesAsItDidWhenTheCorpusWasMade()
    {
        var actual = Render(Sheets());
        if (Environment.GetEnvironmentVariable("CODEX_GOLDEN_FITD") == "update")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExpectedFile)!);
            File.WriteAllText(ExpectedFile, actual);
            return;
        }

        Assert.True(File.Exists(ExpectedFile), $"{ExpectedFile} is missing; run with CODEX_GOLDEN_FITD=update to create it.");
        var expected = File.ReadAllText(ExpectedFile).ReplaceLineEndings("\n");
        if (expected == actual)
        {
            File.Delete(Path.ChangeExtension(ExpectedFile, ".actual.txt"));
            return;
        }

        File.WriteAllText(Path.ChangeExtension(ExpectedFile, ".actual.txt"), actual);
        var differing = expected.Split('\n').Zip(actual.Split('\n')).Where(p => p.First != p.Second).Take(10).Select(p => $"  - {p.First}\n  + {p.Second}");
        Assert.Fail("The golden fitd corpus changed. If the change is meant, rerun with CODEX_GOLDEN_FITD=update and review the diff.\n" + string.Join("\n", differing));
    }

    private string Render(SortedDictionary<string, Dictionary<string, object>> sheets)
    {
        var builder = new StringBuilder();
        builder.Append("# Golden fitd corpus: what SheetRuleEngine.Recompute makes of the sheets built in FitdGoldenCorpusTests.\n");
        builder.Append("# Generated (CODEX_GOLDEN_FITD=update). Review changes here like code.\n");
        foreach (var (id, input) in sheets)
        {
            builder.Append('\n').Append("## ").Append(id).Append('\n');
            foreach (var line in RenderSheet(input)) builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    private IEnumerable<string> RenderSheet(Dictionary<string, object> input)
    {
        var sheet = new Dictionary<string, object>(input);
        var evaluation = new SheetRuleEngine(_system, _resolver).Recompute(sheet);

        foreach (var key in sheet.Keys.Union(input.Keys).Order(StringComparer.Ordinal))
        {
            var before = input.TryGetValue(key, out var b) ? Format(b) : null;
            var after = sheet.TryGetValue(key, out var a) ? Format(a) : null;
            if (before == after) continue;
            yield return after == null ? $"sheet {key} removed" : $"sheet {key} = {after}";
        }

        foreach (var (key, value) in (evaluation.Model?.Values ?? new Dictionary<string, object>()).OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (!key.Contains('.')) yield return $"value {key} = {Format(value)}";
        }

        foreach (var (target, applied) in (evaluation.Model?.Modifiers ?? new Dictionary<string, IReadOnlyList<AppliedModifier>>()).OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            foreach (var m in applied) yield return $"modifier {target} {m.Amount.ToString("R", CultureInfo.InvariantCulture)} {m.Type} from {m.Source}{(m.Counted ? "" : " (not counted)")}";
        }

        foreach (var feature in evaluation.Features) yield return $"feature {feature.Name}{(feature.OnKind ? " on-kind" : "")}";
        foreach (var counter in evaluation.Counters) yield return $"counter {counter.Key} max={counter.Max} rests=[{string.Join(", ", counter.RecoversOn)}] on=[{string.Join(", ", (counter.Recovers ?? Array.Empty<RecoverRule>()).Select(r => r.On))}]";
        foreach (var choice in evaluation.PendingChoices) yield return $"pending {choice.Key} target={choice.Target} options={choice.Options.Count}";
        foreach (var choice in evaluation.MadeChoices) yield return $"made {choice.Key} target={choice.Target} picks=[{string.Join(", ", choice.Made)}]";
        var view = new SheetView(_system, _resolver, sheet);
        foreach (var track in new[] { "special_armor" })
        {
            if (view.TrackSources(track) is { } sources) yield return $"track {track} given by [{string.Join(", ", sources)}]";
        }

        foreach (var violation in evaluation.Violations) yield return $"violation {violation.FieldKey} {violation.Key ?? "-"}: {violation.Message.ReplaceLineEndings(" ")}";
        foreach (var warning in evaluation.Warnings) yield return $"warning {warning.FieldKey} {warning.Key ?? "-"}: {warning.Message.ReplaceLineEndings(" ")}";
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s => s.ReplaceLineEndings(" "),
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object>().Select(k => k.ToString()!).Order(StringComparer.Ordinal).Select(k => $"{k}: {Format(map[k])}")) + "}",
        System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Format)) + "]",
        _ => value.ToString() ?? ""
    };
}
