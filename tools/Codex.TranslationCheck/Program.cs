using Codex.Core;
using Codex.Core.Scripting;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.TranslationCheck;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;

// Usage:
//   dotnet run --project tools/Codex.TranslationCheck -- --lang ru --translation <pack-dir> [--content content] [--system srd5e] [--quiet]
//
// Checks every item of a translation pack against the entry it translates: numbers and dice unchanged, markdown shape kept,
// the right script, only translatable fields, and the agreed terms (from system.<lang>.yaml). Exit code 1 if any check
// reports an error; warnings are listed for a person to look at.

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var language = Arg("--lang");
var translationDir = Arg("--translation");
if (language == null || translationDir == null)
{
    Console.Error.WriteLine("usage: --lang <ru|he> --translation <pack dir> [--content <root>] [--system <id>] [--quiet]");
    return 2;
}

var contentRoot = Arg("--content") ?? "content";
var systemId = Arg("--system") ?? "srd5e";
var quiet = args.Contains("--quiet");

var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
await loader.LoadAndInitializeAsync(contentRoot);
var systemFile = Path.Combine(contentRoot, "systems", systemId, "system.yaml");
if (!File.Exists(systemFile) && !File.Exists(systemFile = Path.Combine(contentRoot, "examples", systemId, "system.yaml")))
{
    Console.Error.WriteLine($"system '{systemId}' was not found under {contentRoot}");
    return 2;
}

var system = SystemDefinitionLoader.Load(systemFile);

// --prepare <type> --batch-size N --out <dir>: writes batch-NN/input.md with the exact English text (blank lines visible)
// of every entry of the type, for translators to work from.
if (Arg("--prepare") is { } prepareType)
{
    var size = int.TryParse(Arg("--batch-size"), out var n) && n > 0 ? n : 12;
    var outDir = Arg("--out") ?? "batches";
    var def = system.FindType(prepareType);
    var textKeys = (def?.Fields ?? new List<FieldDefinition>()).Where(f => f.Type is FieldType.Text or FieldType.LongText).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var entries = registry.GetEntries(systemId, prepareType).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
    var batchNo = 0;
    foreach (var batch in entries.Chunk(size))
    {
        var dir = Path.Combine(outDir, $"batch-{++batchNo:00}");
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder();
        foreach (var e in batch)
        {
            sb.AppendLine($"## id: {e.Id}").AppendLine($"name: {e.Name}").AppendLine("description (exact text, blank lines and trailing double spaces matter):").AppendLine("<<<");
            sb.AppendLine((e.Description ?? "").Replace("\r\n", "\n")).AppendLine(">>>");
            foreach (var (key, value) in e.Properties.Where(kv => textKeys.Contains(kv.Key) && kv.Value is string { Length: > 0 }))
                sb.AppendLine($"property {key}: {value}");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "input.md"), sb.ToString());
    }

    Console.WriteLine($"{entries.Count} {prepareType} entries in {batchNo} batches under {outDir}");
    return 0;
}

var checker = new TranslationChecker(language, TranslationChecker.GlossaryOf(system, language));
var deserializer = new DeserializerBuilder().Build();
var findings = new List<Finding>();
var checkedItems = 0;
var seen = new HashSet<string>();

foreach (var folder in Directory.GetDirectories(translationDir).Where(d => !Path.GetFileName(d).StartsWith('.')))
{
    var typeFolder = Path.GetFileName(folder);
    var type = system.FindType(typeFolder);
    var typeKey = type?.Type ?? typeFolder.ToLowerInvariant();
    var textFields = new HashSet<string>(ContentEntry.FlavorKeys, StringComparer.OrdinalIgnoreCase);
    foreach (var f in type?.Fields ?? new List<FieldDefinition>())
        if (f.Type is FieldType.Text or FieldType.LongText or FieldType.Collection) textFields.Add(f.Key);

    foreach (var file in Directory.GetFiles(folder, "*.yaml", SearchOption.AllDirectories))
    {
        var parsed = deserializer.Deserialize<object>(File.ReadAllText(file));
        var items = parsed switch
        {
            System.Collections.IList list => list.OfType<System.Collections.IDictionary>().ToList(),
            System.Collections.IDictionary one => new List<System.Collections.IDictionary> { one },
            _ => new List<System.Collections.IDictionary>()
        };

        foreach (var raw in items)
        {
            var item = new Dictionary<string, object>();
            foreach (System.Collections.DictionaryEntry e in raw)
                if (e.Key?.ToString() is { } k && e.Value != null) item[k] = e.Value;

            var id = item.TryGetValue("id", out var v) ? v.ToString() ?? "" : "";
            if (!seen.Add($"{typeKey}:{id}"))
                findings.Add(new Finding(Severity.Error, $"{typeKey}:{id}", "duplicate", $"the id appears twice (second time in {Path.GetFileName(file)})"));

            checkedItems++;
            findings.AddRange(checker.Check(registry.GetEntry(systemId, typeKey, id), typeKey, item, textFields));
        }
    }
}

var errors = findings.Count(f => f.Severity == Severity.Error);
var warnings = findings.Count - errors;
if (!quiet)
{
    foreach (var group in findings.GroupBy(f => f.Entry).OrderBy(g => g.Key))
        foreach (var f in group)
            Console.WriteLine($"{(f.Severity == Severity.Error ? "ERROR" : "warn ")} {f.Entry} [{f.Code}] {f.Message}");
}

Console.WriteLine($"{checkedItems} items checked: {errors} errors, {warnings} warnings ({findings.Where(f => f.Severity == Severity.Error).Select(f => f.Entry).Distinct().Count()} entries with errors)");
foreach (var code in findings.GroupBy(f => (f.Severity, f.Code)).OrderByDescending(g => g.Count()))
    Console.WriteLine($"  {code.Key.Severity,-7} {code.Key.Code,-18} {code.Count()}");
return errors == 0 ? 0 : 1;
