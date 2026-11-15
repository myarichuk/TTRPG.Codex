using System.Globalization;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;

namespace Codex.ContentImport;

/// <summary>
/// The SRD 5.2.1's eight traps as library entries (type <c>hazard</c>). The 5e-bits transcription has no traps, so they are read from
/// <c>Data/srd521-traps.yaml</c>, written in this project's own words (CC BY 4.0; the pack's attribution covers them).
/// </summary>
public static class Traps
{
    /// <summary>The entries of <c>srd521-traps.yaml</c>.</summary>
    public static IReadOnlyList<ContentEntry> Entries()
    {
        using var stream = typeof(Traps).Assembly.GetManifestResourceStream("srd521-traps.yaml")
            ?? throw new InvalidDataException("The importer carries no srd521-traps.yaml.");
        using var reader = new StreamReader(stream);
        return Read(reader.ReadToEnd());
    }

    /// <summary>Reads the file's text: one entry per item, its <c>properties</c> as the entry's properties.</summary>
    public static IReadOnlyList<ContentEntry> Read(string yaml)
    {
        var raw = new DeserializerBuilder().Build().Deserialize<List<object>>(yaml) ?? new List<object>();
        var entries = new List<ContentEntry>();
        foreach (var item in raw.OfType<IDictionary<object, object>>())
        {
            var id = Text(item, "id") ?? throw new InvalidDataException("A trap has no id.");
            var entry = new ContentEntry
            {
                Type = "hazard",
                Id = id,
                Name = Text(item, "name") ?? id,
                Description = Text(item, "description")?.Trim() ?? string.Empty
            };
            if (item.TryGetValue("properties", out var properties) && properties is IDictionary<object, object> map)
            {
                foreach (var (key, value) in map)
                {
                    var name = System.Convert.ToString(key, CultureInfo.InvariantCulture)!;
                    entry.Properties[name] = Value(name, value);
                }
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static string? Text(IDictionary<object, object> item, string key) =>
        item.TryGetValue(key, out var value) ? System.Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>A DC is a number; every other value stays as written (a damage of <c>1</c> is text, as a field of dice is). A row of a collection is read the same way.</summary>
    private static object Value(string key, object? value) => value switch
    {
        IList<object> rows => rows.Select(row => Value(key, row)).ToList(),
        IDictionary<object, object> row => row.ToDictionary(
            p => System.Convert.ToString(p.Key, CultureInfo.InvariantCulture)!,
            p => Value(System.Convert.ToString(p.Key, CultureInfo.InvariantCulture)!, p.Value)),
        _ => IsDc(key) && long.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static bool IsDc(string key) => key.Equals("dc", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Dc", StringComparison.Ordinal);
}
