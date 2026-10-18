namespace Codex.ContentImport;

/// <summary>
/// Fetches the 5e-bits JSON transcription of the SRD from a pinned commit, so a re-import is
/// reproducible and a change upstream is a deliberate bump of <see cref="Commit"/>.
/// </summary>
public static class SrdDownloader
{
    /// <summary>5e-bits/5e-srd-api main as of 2026-10-17.</summary>
    public const string Commit = "85acb251dad0b044a72c554c2d7f2d0c11e43504";

    public static readonly string[] Files =
    {
        "Ability-Scores", "Alignments", "Backgrounds", "Classes", "Conditions", "Damage-Types",
        "Equipment-Categories", "Equipment", "Feats", "Features", "Languages", "Levels", "Magic-Items",
        "Magic-Schools", "Monsters", "Proficiencies", "Skills", "Spells", "Subclasses",
        "Weapon-Properties"
    };

    private static readonly string[] Only2024 = { "Species", "Subspecies", "Traits", "Weapon-Mastery-Properties", "Poisons" };
    private static readonly string[] Only2014 = { "Races", "Subraces", "Traits", "Rules", "Rule-Sections" };

    public static string RawUrl(string edition, string file) =>
        $"https://raw.githubusercontent.com/5e-bits/5e-srd-api/{Commit}/packages/5e-database/src/{edition}/en/5e-SRD-{file}.json";

    public static async Task DownloadAsync(string targetDirectory, string edition, TextWriter log)
    {
        Directory.CreateDirectory(targetDirectory);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TTRPG.Codex-ContentImport/1.0");

        var files = Files.Concat(edition == "2024" ? Only2024 : Only2014);
        foreach (var file in files)
        {
            var url = RawUrl(edition, file);
            var target = Path.Combine(targetDirectory, $"5e-SRD-{file}.json");
            log.WriteLine($"Downloading {file}...");
            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                log.WriteLine($"  skipped ({(int)response.StatusCode})");
                continue;
            }

            await File.WriteAllBytesAsync(target, await response.Content.ReadAsByteArrayAsync());
        }
    }
}
