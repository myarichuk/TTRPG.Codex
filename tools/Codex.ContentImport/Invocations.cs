using System.Globalization;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;

namespace Codex.ContentImport;

/// <summary>
/// The Warlock's Eldritch Invocation options as library entries (type <c>invocation</c>), so they can be picked by hand and by the drafter, and the table
/// of how many a Warlock knows at each level. The 5e-bits transcription has the class feature's intro but not its options, so the options are read from
/// <c>Data/srd521-invocations.yaml</c>, written from the SRD 5.2.1 document (CC BY 4.0; the pack's attribution covers them).
/// </summary>
public static class Invocations
{
    /// <summary>The class the options belong to.</summary>
    public const string ClassId = "warlock";

    /// <summary>The Levels file's column for how many invocations the class knows.</summary>
    public const string Column = "eldritch_invocations";

    /// <summary>The entries of <c>srd521-invocations.yaml</c>.</summary>
    public static IReadOnlyList<ContentEntry> Entries()
    {
        using var stream = typeof(Invocations).Assembly.GetManifestResourceStream("srd521-invocations.yaml")
            ?? throw new InvalidDataException("The importer carries no srd521-invocations.yaml.");
        using var reader = new StreamReader(stream);
        var raw = new DeserializerBuilder().Build().Deserialize<List<object>>(reader.ReadToEnd());
        var entries = new List<ContentEntry>();
        foreach (var item in raw.OfType<IDictionary<object, object>>())
        {
            string? Text(string key) => item.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
            var id = Text("id") ?? throw new InvalidDataException("An invocation has no id.");
            var prerequisites = Text("prerequisites");
            var body = Text("description")?.Trim() ?? string.Empty;
            var entry = new ContentEntry
            {
                Type = "invocation",
                Id = id,
                Name = Text("name") ?? id,
                // The prerequisite leads the text, as the document prints it under the name.
                Description = prerequisites == null ? body : $"**Prerequisite:** {prerequisites}\n\n{body}"
            };
            entry.Properties["class"] = ClassId;
            entry.Properties["minLevel"] = long.Parse(Text("minLevel") ?? "1", CultureInfo.InvariantCulture);
            if (prerequisites != null) entry.Properties["prerequisites"] = prerequisites;
            if (Text("repeatable") == "true") entry.Properties["repeatable"] = true;
            entry.Tags.Add(ClassId);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Gives the class its <c>invocationSlots</c> table: one row per level, <c>slots</c> the number the Levels file gives.</summary>
    public static void AddSlots(IReadOnlyList<ContentEntry> entries, ClassColumns columns)
    {
        foreach (var entry in entries.Where(e => e.Type == "class" && columns.Has(e.Id, Column)))
        {
            entry.Properties["invocationSlots"] = Enumerable.Range(1, 20)
                .Select(level => (object)new Dictionary<string, object> { ["level"] = (long)level, ["slots"] = (long)(columns.ValueAt(entry.Id, Column, level) ?? 0) })
                .ToList();
        }
    }
}
