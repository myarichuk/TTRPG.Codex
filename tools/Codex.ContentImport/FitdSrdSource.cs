namespace Codex.ContentImport;

/// <summary>
/// Where the Forged in the Dark SRD text comes from: a Markdown transcription published on GitHub,
/// pinned to one commit so a re-import is reproducible and a change upstream is a deliberate bump
/// of <see cref="Commit"/>.
/// </summary>
public static class FitdSrdSource
{
    public const string Repository = "amazingrando/blades-in-the-dark-srd-content";

    /// <summary>The commit the transcription was read from.</summary>
    public const string Commit = "f141ed2c4ed8061b6833a57e37a314a03e4166ce";

    public const string FileName = "Blades-in-the-Dark-SRD.md";

    public static string RawUrl => $"https://raw.githubusercontent.com/{Repository}/{Commit}/{FileName}";

    /// <summary>The file a download is written to: the path itself when it names a Markdown file,
    /// otherwise <see cref="FileName"/> inside that directory.</summary>
    public static string DownloadTarget(string source) =>
        source.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? source : Path.Combine(source, FileName);

    /// <summary>Resolves <c>--source</c> (a Markdown file, or a directory holding one) to the file to read.</summary>
    public static string FindFile(string source)
    {
        if (File.Exists(source))
        {
            return source;
        }

        if (Directory.Exists(source))
        {
            var preferred = Path.Combine(source, FileName);
            if (File.Exists(preferred))
            {
                return preferred;
            }

            var candidates = Directory.GetFiles(source, "*.md");
            if (candidates.Length == 1)
            {
                return candidates[0];
            }
        }

        throw new FileNotFoundException($"No SRD Markdown file found at '{source}'. Pass --download to fetch it, or point --source at the file.");
    }

    public static async Task DownloadAsync(string source, TextWriter log)
    {
        var target = DownloadTarget(source);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TTRPG.Codex-ContentImport/1.0");

        log.WriteLine($"Downloading {RawUrl} ...");
        using var response = await http.GetAsync(RawUrl);
        response.EnsureSuccessStatusCode();
        await File.WriteAllBytesAsync(target, await response.Content.ReadAsByteArrayAsync());
    }
}
