using Codex.ContentImport;

// Usage:
//   dotnet run --project tools/Codex.ContentImport -- srd5e --source <dir> --out <pack-dir> [--download] [--edition 2024|2014]
//
// --source   directory holding the 5e-bits JSON files (5e-SRD-Classes.json, ...). With --download
//            the files are fetched there first, from the pinned upstream commit.
// --out      the pack directory to (re)create, e.g. content/systems/srd5e/packs/srd-5.2.1
//
// The tool only transforms. Licensing is handled by the manifest it writes: the verbatim SRD 5.2.1
// attribution statement (CC-BY-4.0), a modification notice, and the 5e-bits MIT notice for the JSON
// transcription the data was read from.

var options = ImportOptions.Parse(args);
if (options == null)
{
    Console.Error.WriteLine(ImportOptions.Usage);
    return 2;
}

try
{
    if (options.Download)
    {
        await SrdDownloader.DownloadAsync(options.Source, options.Edition, Console.Out);
    }

    var importer = new Srd5eImporter(options.Source, options.Edition);
    var result = await importer.ImportAsync(options.Out, Console.Out);
    Console.WriteLine($"Wrote {result.EntryCount} entries across {result.Types.Count} types to {options.Out}");
    foreach (var (type, count) in result.Types.OrderBy(t => t.Key))
    {
        Console.WriteLine($"  {type,-12} {count,5}");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
