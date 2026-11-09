using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.SystemCheck;
using Microsoft.Extensions.Logging.Abstractions;

// Usage:
//   dotnet run --project tools/Codex.SystemCheck -- <system folder> [--types] [--pack <file.zip>] [--quiet]
//   dotnet run --project tools/Codex.SystemCheck -- --schema <folder>
//
// Loads the system in the folder (its system.yaml, scripts and packs) the way the server does and lists every problem the
// loader finds: unknown keys, handlers, rolls, layouts, campaign objects, scripts. When the folder has a spec.yaml (format:
// tests/Codex.Tests/Fixtures/reference/README.md), runs each expectation and says what it came to. --types writes codex.d.ts
// beside system.yaml, for completion in a script editor. --pack writes the system as the zip the app's install takes, once the
// check passes (a system with compiled code cannot be one: it is installed by copying its folder). --quiet lists only what is
// wrong.
//
// --schema writes system.schema.json (into <folder>/schema) and SYSTEM_YAML_REFERENCE.md (into <folder>), generated from the
// app's model: the repository keeps them in docs/.
//
// Exit code 0 when the system loads and no expectation fails; 1 when the system does not load, an expectation fails or the
// archive cannot be written; 2 for a wrong command line. Problems the loader reports do not fail the check: the server loads
// such a system too.

string? Option(string name) => Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

if (args.Contains("--schema"))
{
    if (Option("--schema") is not { } docs)
    {
        Console.Error.WriteLine("usage: --schema <folder>");
        return 2;
    }

    Directory.CreateDirectory(Path.Combine(docs, "schema"));
    File.WriteAllText(Path.Combine(docs, "schema", SystemSchema.SchemaFile), SystemSchema.Json());
    File.WriteAllText(Path.Combine(docs, SystemSchema.ReferenceFile), SystemSchema.Reference());
    Console.WriteLine($"WROTE   {Path.Combine(docs, "schema", SystemSchema.SchemaFile)}");
    Console.WriteLine($"WROTE   {Path.Combine(docs, SystemSchema.ReferenceFile)}");
    return 0;
}

var pack = Option("--pack");
var folder = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || args[i - 1] != "--pack")).FirstOrDefault();
if (folder == null || !Directory.Exists(folder) || (args.Contains("--pack") && pack == null))
{
    Console.Error.WriteLine("usage: <system folder> [--types] [--pack <file.zip>] [--quiet]  |  --schema <folder>");
    return 2;
}

var quiet = args.Contains("--quiet");
var code = SystemCheck.Run(Path.GetFullPath(folder), args.Contains("--types"), quiet, Console.Out);
if (code != 0 || pack == null) return code;

using (var output = File.Create(pack))
{
    if (SystemArchive.Write(folder, output) is { Count: > 0 } refused)
    {
        output.Close();
        File.Delete(pack);
        foreach (var why in refused) Console.WriteLine($"FAILED  {why}");
        return 1;
    }
}

if (!quiet) Console.WriteLine($"WROTE   {Path.GetFullPath(pack)}");
return 0;

namespace Codex.SystemCheck
{
    public static class SystemCheck
    {
        public static int Run(string folder, bool writeTypes, bool quiet, TextWriter output)
        {
            var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
            loader.LoadAndInitializeAsync(folder).GetAwaiter().GetResult();
            foreach (var failed in loader.FailedSystems)
            {
                output.WriteLine($"FAILED  {failed.Name}: {string.Join(" ", failed.Errors)}");
            }

            foreach (var problem in loader.LoadProblems)
            {
                output.WriteLine($"PROBLEM {problem.Subject}: {problem.Message}");
            }

            var ids = loader.LoadedSystemIds.ToList();
            if (ids.Count == 0)
            {
                if (loader.FailedSystems.Count == 0) output.WriteLine($"FAILED  no system.yaml under {folder}");
                return 1;
            }

            foreach (var id in ids)
            {
                var system = loader.GetDefinition(id)!;
                if (!quiet) output.WriteLine($"LOADED  {system.Id} {system.Version} ({system.Name})");
                if (writeTypes && FolderOf(folder, system.Id) is { } systemFolder)
                {
                    var path = Path.Combine(systemFolder, ScriptTypings.FileName);
                    File.WriteAllText(path, ScriptTypings.For(system));
                    if (!quiet) output.WriteLine($"WROTE   {path}");
                }
            }

            if (!File.Exists(Path.Combine(folder, ReferenceRunner.SpecFile))) return 0;
            var results = ReferenceRunner.RunAll(new CurrentEngineHost(), new[] { folder });
            foreach (var result in results)
            {
                if (quiet && result.Status == CheckStatus.Pass) continue;
                var status = result.Status switch { CheckStatus.Pass => "PASS   ", CheckStatus.Fail => "FAIL   ", _ => "CANNOT " };
                output.WriteLine(string.IsNullOrEmpty(result.Detail) ? $"{status} {result.Id}" : $"{status} {result.Id}: {result.Detail}");
            }

            var failing = results.Count(r => r.Status == CheckStatus.Fail);
            output.WriteLine($"{results.Count(r => r.Status == CheckStatus.Pass)} of {results.Count} expectations pass; {failing} fail.");
            return failing > 0 ? 1 : 0;
        }

        /// <summary>The folder of the system.yaml that declares <paramref name="id"/>.</summary>
        private static string? FolderOf(string root, string id) =>
            Directory.EnumerateFiles(root, SystemDefinitionLoader.FileName, SearchOption.AllDirectories)
                .FirstOrDefault(f => IdOf(f) == id) is { } file ? Path.GetDirectoryName(file) : null;

        private static string? IdOf(string systemFile)
        {
            try
            {
                return SystemDefinitionLoader.Load(systemFile).Id;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }
}
