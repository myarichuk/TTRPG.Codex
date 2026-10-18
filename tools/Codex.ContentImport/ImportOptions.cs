namespace Codex.ContentImport;

public sealed class ImportOptions
{
    public const string Usage = """
        Codex.ContentImport - converts open rules data into TTRPG.Codex content packs.

          srd5e --source <dir> --out <dir> [--download] [--edition 2024|2014]

          --source    directory with the 5e-bits JSON files (created when --download is given)
          --out       pack directory to write (replaced if it exists)
          --download  fetch the JSON from the pinned 5e-bits commit into --source first
          --edition   2024 (SRD 5.2.1, default) or 2014 (SRD 5.1)
        """;

    public string Command { get; private init; } = string.Empty;
    public string Source { get; private init; } = string.Empty;
    public string Out { get; private init; } = string.Empty;
    public bool Download { get; private init; }
    public string Edition { get; private init; } = "2024";

    public static ImportOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "srd5e")
        {
            return null;
        }

        string? source = null, output = null, edition = "2024";
        var download = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" when i + 1 < args.Length:
                    source = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--edition" when i + 1 < args.Length:
                    edition = args[++i];
                    break;
                case "--download":
                    download = true;
                    break;
                default:
                    return null;
            }
        }

        if (source == null || output == null || edition is not ("2024" or "2014"))
        {
            return null;
        }

        return new ImportOptions { Command = args[0], Source = source, Out = output, Download = download, Edition = edition };
    }
}
