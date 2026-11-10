using System.Globalization;
using System.Text;
using System.Text.Json;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Golden;

/// <summary>
/// The golden corpus of overhaul section 5: srd5e sheets evaluated now and kept, so that moving the rules onto the primitives
/// (overhaul steps 2-6) can show it changed nothing. <c>srd5e/sheets.json</c> holds the inputs (every class at levels 1, 5, 11 and
/// 20, multiclass builds, sheets in manual and rolled modes, broken sheets, imported creatures); <c>srd5e/expected.txt</c> holds,
/// for each, what <see cref="SheetRuleEngine.Recompute"/> wrote onto the sheet and everything the evaluation reports.
/// <para>
/// A difference fails with the first lines that differ and writes the whole result next to the file as <c>expected.actual.txt</c>.
/// When a change is meant (a rule fixed, the pack corrected), rerun with <c>CODEX_GOLDEN=update</c> and review the diff of
/// expected.txt like code. <c>CODEX_GOLDEN=sheets</c> regenerates the inputs too, from the builds below; only needed when a build
/// is added or the pack's ids change.
/// </para>
/// </summary>
public class Srd5eGoldenCorpusTests : IAsyncLifetime
{
    private static readonly string Folder = Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Golden", "srd5e");
    private static readonly string SheetsFile = Path.Combine(Folder, "sheets.json");
    private static readonly string ExpectedFile = Path.Combine(Folder, "expected.txt");
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "srd-5.2.1" };

    private readonly ContentRegistry _registry = new();
    private readonly ComponentRegistry _components = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;
    private IEntryResolver _resolver = null!;

    public Srd5eGoldenCorpusTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, _components, new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition("srd5e")!;
        _resolver = new PackResolver(_registry);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type, Packs);
    }

    private SheetRuleEngine Engine() => new(_system, _resolver);

    [Fact]
    public void EverySheetInTheCorpus_EvaluatesAsItDidWhenTheCorpusWasMade()
    {
        var mode = Environment.GetEnvironmentVariable("CODEX_GOLDEN");
        if (mode == "sheets") WriteSheets(Generate());

        var sheets = ReadSheets();
        var actual = Render(sheets);

        if (mode is "update" or "sheets")
        {
            File.WriteAllText(ExpectedFile, actual);
            return;
        }

        Assert.True(File.Exists(ExpectedFile), $"{ExpectedFile} is missing; run with CODEX_GOLDEN=update to create it.");
        var expected = File.ReadAllText(ExpectedFile).ReplaceLineEndings("\n");
        if (expected == actual)
        {
            File.Delete(Path.ChangeExtension(ExpectedFile, ".actual.txt"));
            return;
        }

        File.WriteAllText(Path.ChangeExtension(ExpectedFile, ".actual.txt"), actual);
        Assert.Fail("The golden srd5e corpus changed. If the change is meant, rerun with CODEX_GOLDEN=update and review the diff.\n" + FirstDifferences(expected, actual));
    }

    [Fact]
    public void TheCorpusCoversWhatSectionFiveAsksFor()
    {
        var sheets = ReadSheets();
        var classes = _registry.GetEntries("srd5e", "class", Packs).Select(c => c.Id).ToList();

        foreach (var cls in classes)
        {
            foreach (var level in Levels) Assert.Contains($"{cls}-{level}", sheets.Keys);
        }

        Assert.True(sheets.Keys.Count(k => k.StartsWith("multiclass-", StringComparison.Ordinal)) >= 3);
        Assert.True(sheets.Keys.Count(k => k.StartsWith("creature-", StringComparison.Ordinal)) >= 8);
        Assert.Contains(sheets.Keys, k => k.StartsWith("manual-", StringComparison.Ordinal));
        // The corpus is only as good as its sheets: most class sheets break no rule, so they exercise the rules, not the error paths.
        var expected = File.ReadAllText(ExpectedFile);
        var withViolations = classes.SelectMany(c => Levels.Select(l => $"{c}-{l}")).Count(id => Section(expected, id).Contains("\nviolation ", StringComparison.Ordinal));
        Assert.True(withViolations <= 4, $"{withViolations} class sheets break a rule; the generator should build legal sheets.");
    }

    private static string Section(string text, string id)
    {
        var start = text.IndexOf($"## {id}\n", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = text.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    /// <summary>Per sheet, the lines that went and the lines that came: one line dropped must not read as every later line changed.</summary>
    private static string FirstDifferences(string expected, string actual)
    {
        var before = Sections(expected);
        var after = Sections(actual);
        var builder = new StringBuilder();
        var sheets = 0;
        foreach (var id in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var gone = Except(before.GetValueOrDefault(id), after.GetValueOrDefault(id));
            var came = Except(after.GetValueOrDefault(id), before.GetValueOrDefault(id));
            if (gone.Count == 0 && came.Count == 0) continue;
            if (++sheets > 8)
            {
                builder.Append("... and more sheets; see expected.actual.txt\n");
                break;
            }

            builder.Append("## ").Append(id).Append(before.ContainsKey(id) ? after.ContainsKey(id) ? "" : " (gone)" : " (new)").Append('\n');
            foreach (var line in gone.Take(6)) builder.Append("  - ").Append(line).Append('\n');
            foreach (var line in came.Take(6)) builder.Append("  + ").Append(line).Append('\n');
        }

        return builder.ToString();

        static Dictionary<string, List<string>> Sections(string text)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string>? current = null;
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal)) result[line[3..]] = current = new List<string>();
                else if (current != null && line.Length > 0) current.Add(line);
            }

            return result;
        }

        static List<string> Except(List<string>? from, List<string>? without)
        {
            var left = new List<string>(from ?? new List<string>());
            foreach (var line in without ?? new List<string>()) left.Remove(line);
            return left;
        }
    }

    // ---- Evaluation, rendered one fact per line so a diff reads as what changed ----

    private string Render(SortedDictionary<string, Dictionary<string, object>> sheets)
    {
        var builder = new StringBuilder();
        builder.Append("# Golden srd5e corpus: what SheetRuleEngine.Recompute makes of tests/Codex.Tests/Golden/srd5e/sheets.json.\n");
        builder.Append("# Generated; see Srd5eGoldenCorpusTests for how to update it. Review changes here like code.\n");
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
        var engine = Engine();
        var evaluation = engine.Recompute(sheet);

        // What the rules wrote onto the sheet (the creature's printed stat block, typed in, is input and not repeated).
        foreach (var key in sheet.Keys.Union(input.Keys).Order(StringComparer.Ordinal))
        {
            var before = input.TryGetValue(key, out var b) ? Format(b) : null;
            var after = sheet.TryGetValue(key, out var a) ? Format(a) : null;
            if (before == after) continue;
            yield return after == null ? $"sheet {key} removed" : $"sheet {key} = {after}";
        }

        foreach (var (key, value) in evaluation.Derived.OrderBy(d => d.Key, StringComparer.Ordinal)) yield return $"derived {key} = {Format(value)}";

        if (evaluation.Attributes is { } attributes)
        {
            yield return $"attributes method={attributes.Method} spent={attributes.PointsSpent} budget={Format(attributes.PointsBudget)} eligible=[{string.Join(", ", attributes.EligibleBonusAttributes)}]";
            yield return $"attributes bonuses={Format(attributes.Bonuses)} increases={Format(attributes.Increases)}";
            yield return $"attributes totals={Format(attributes.Totals)}";
        }

        foreach (var cls in evaluation.Classes) yield return $"class {cls.Id} level={cls.Level}{(cls.IsFirst ? " first" : "")}{(cls.SubclassId != null ? " subclass=" + cls.SubclassId : "")}{(cls.Entry == null ? " (not in library)" : "")}";
        if (evaluation.MulticlassOptions.Count > 0) yield return $"multiclass options={string.Join(", ", evaluation.MulticlassOptions.Select(e => e.Id))}";

        foreach (var (field, allowed) in evaluation.AllowedOptions.OrderBy(o => o.Key, StringComparer.Ordinal)) yield return $"allowed {field} = {allowed.Count}: {Ids(allowed)}";
        foreach (var (list, picks) in evaluation.ListPicks.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            var options = evaluation.ListOptions.GetValueOrDefault(list) ?? Array.Empty<ContentEntry>();
            var limit = evaluation.ListLimits.GetValueOrDefault(list);
            var missing = evaluation.ListMissing.GetValueOrDefault(list) ?? Array.Empty<MissingPick>();
            yield return $"list {list} picks={picks.Count} limit={Format(limit)} options={options.Count} missing={missing.Count}";
        }

        foreach (var grant in evaluation.Increases) yield return $"increase {grant.Pick.Raw} points={grant.Points} options=[{string.Join(", ", grant.Options)}] cap={Format(grant.Cap)}";
        foreach (var pool in evaluation.SlotPools) yield return $"slots {pool.Rule.Key} = {Format(pool.Max.OrderBy(m => m.Key).ToDictionary(m => m.Key.ToString(CultureInfo.InvariantCulture), m => m.Value))}";

        if (evaluation.HitPoints is { } hp)
        {
            yield return $"hp mode={hp.Mode} active={Format(hp.Active)} max={hp.Max} modifier={hp.Modifier} extra={hp.LevelExtra}{(hp.ExtraSource != null ? " from " + hp.ExtraSource : "")}";
            foreach (var level in hp.Levels) yield return $"hp level {level.Level} {level.ClassName} d{level.Die} base={level.Base}{(level.IsRolled ? " rolled" : "")} mod={level.Modifier} extra={level.Extra} gain={level.Gain}";
        }

        if (evaluation.ArmorClass is { } ac)
        {
            yield return $"ac mode={ac.Mode} active={Format(ac.Active)} total={ac.Total}";
            foreach (var line in ac.Lines) yield return $"ac line {line}";
        }

        foreach (var item in evaluation.Inventory) yield return $"item {item.Entry.Id} x{item.Quantity}{(item.Equipped ? " equipped" : "")}";
        if (evaluation.Inventory.Count > 0 || evaluation.CarryingCapacity != null) yield return $"carried {Format(evaluation.CarriedWeight)} of {Format(evaluation.CarryingCapacity)}";

        foreach (var save in evaluation.SavingThrows) yield return $"save {save.Key} {Signed(save.Bonus)}{Training(save)}";
        foreach (var skill in evaluation.Skills) yield return $"skill {skill.Key} {Signed(skill.Bonus)}{Training(skill)}";
        foreach (var passive in evaluation.Passives) yield return $"passive {passive.Key} {passive.Value}";
        foreach (var casting in evaluation.Spellcasting) yield return $"spellcasting {casting.ClassName} {casting.Attribute} dc={casting.SaveDc} attack={Signed(casting.AttackBonus)}";

        foreach (var violation in evaluation.Violations) yield return $"violation {violation.FieldKey} {violation.Key ?? "-"}: {OneLine(violation.Message)}";
        foreach (var warning in evaluation.Warnings) yield return $"warning {warning.FieldKey} {warning.Key ?? "-"}: {OneLine(warning.Message)}";
    }

    private static string Training(SkillScore score) => score.Expert ? " expert" : score.Proficient ? " proficient" : "";

    private static string Signed(int value) => value >= 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    private static string Ids(IEnumerable<ContentEntry> entries)
    {
        var ids = entries.Select(e => e.Id).ToList();
        return ids.Count <= 12 ? string.Join(", ", ids) : string.Join(", ", ids.Take(12)) + $", ... {ids.Count - 12} more";
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s => OneLine(s),
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object>().Select(k => k.ToString()!).Order(StringComparer.Ordinal).Select(k => $"{k}: {Format(map[k])}")) + "}",
        IEnumerable<KeyValuePair<string, int>> pairs => "{" + string.Join(", ", pairs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}: {p.Value}")) + "}",
        System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Format)) + "]",
        _ => value.ToString() ?? ""
    };

    // ---- Inputs: stored as JSON so numbers stay numbers and lists stay lists ----

    private static SortedDictionary<string, Dictionary<string, object>> ReadSheets()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SheetsFile));
        var result = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var sheet in document.RootElement.EnumerateObject())
        {
            result[sheet.Name] = sheet.Value.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)!);
        }

        return result;
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)!),
        _ => null
    };

    private static void WriteSheets(SortedDictionary<string, Dictionary<string, object>> sheets)
    {
        Directory.CreateDirectory(Folder);
        var ordered = sheets.ToDictionary(s => s.Key, s => new SortedDictionary<string, object>(s.Value, StringComparer.Ordinal));
        File.WriteAllText(SheetsFile, JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
    }

    // ---- The builds. Run once (CODEX_GOLDEN=sheets) and frozen into sheets.json, so the corpus does not move with the engine. ----

    private static readonly int[] Levels = { 1, 5, 11, 20 };

    private static readonly Dictionary<string, string> AbilityKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Strength"] = "str", ["Dexterity"] = "dex", ["Constitution"] = "con", ["Intelligence"] = "int", ["Wisdom"] = "wis", ["Charisma"] = "cha",
        ["STR"] = "str", ["DEX"] = "dex", ["CON"] = "con", ["INT"] = "int", ["WIS"] = "wis", ["CHA"] = "cha"
    };

    private SortedDictionary<string, Dictionary<string, object>> Generate()
    {
        var sheets = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        var classes = Entries("class");
        var species = Entries("species");
        var backgrounds = Entries("background");

        for (var i = 0; i < classes.Count; i++)
        {
            foreach (var level in Levels)
            {
                sheets[$"{classes[i].Id}-{level}"] = Build(classes[i], level, species[i % species.Count], backgrounds[i % backgrounds.Count]);
            }
        }

        // Multiclass: a half caster with a full caster, three classes, and pact magic beside slots.
        sheets["multiclass-paladin6-sorcerer2"] = Build(Entry("class", "paladin"), 8, Entry("species", "human"), Entry("background", "soldier"),
            new[] { ("sorcerer", 2) }, new() { ["str"] = 15, ["dex"] = 10, ["con"] = 14, ["int"] = 8, ["wis"] = 12, ["cha"] = 15 });
        sheets["multiclass-cleric3-wizard2-fighter1"] = Build(Entry("class", "cleric"), 6, Entry("species", "dwarf"), Entry("background", "acolyte"),
            new[] { ("wizard", 2), ("fighter", 1) }, new() { ["str"] = 13, ["dex"] = 10, ["con"] = 14, ["int"] = 13, ["wis"] = 15, ["cha"] = 8 });
        sheets["multiclass-warlock5-sorcerer3"] = Build(Entry("class", "warlock"), 8, Entry("species", "tiefling"), Entry("background", "sage"),
            new[] { ("sorcerer", 3) }, new() { ["str"] = 8, ["dex"] = 14, ["con"] = 13, ["int"] = 10, ["wis"] = 12, ["cha"] = 15 });
        sheets["multiclass-rogue11-ranger9"] = Build(Entry("class", "rogue"), 20, Entry("species", "elf"), Entry("background", "criminal"),
            new[] { ("ranger", 9) }, new() { ["str"] = 10, ["dex"] = 15, ["con"] = 13, ["int"] = 12, ["wis"] = 14, ["cha"] = 8 });

        // Manual and rolled modes: the sheet's own numbers stand, or the rolls typed in count.
        var manual = Build(Entry("class", "fighter"), 5, Entry("species", "human"), Entry("background", "soldier"));
        manual["hpMode"] = "manual";
        manual["HP_Max"] = 61L;
        manual["acMode"] = "manual";
        manual["armorClass"] = 19L;
        sheets["manual-fighter5"] = manual;
        var rolled = Build(Entry("class", "rogue"), 4, Entry("species", "halfling"), Entry("background", "criminal"));
        rolled["hpMode"] = "rolled";
        rolled["hpRolls"] = new List<object> { "6", "2", "8" };
        sheets["manual-rogue4-rolled"] = rolled;
        var typed = Build(Entry("class", "wizard"), 3, Entry("species", "gnome"), Entry("background", "sage"));
        typed[SheetRuleEngine.MethodKey] = "manual";
        foreach (var (key, score) in new Dictionary<string, long> { ["str"] = 7, ["dex"] = 18, ["con"] = 12, ["int"] = 20, ["wis"] = 9, ["cha"] = 11 }) typed[SheetRuleEngine.BasePrefix + key] = score;
        sheets["manual-wizard3-typed-scores"] = typed;

        // Broken sheets: what the rules say about them is part of the behaviour too.
        var broken = Build(Entry("class", "cleric"), 1, Entry("species", "orc"), Entry("background", "acolyte"));
        broken["cantrips"] = new List<object> { "fire-bolt", "no-such-spell", "guidance", "light", "sacred-flame", "thaumaturgy", "mending" };
        broken["spells"] = new List<object> { "fireball", "cure-wounds" };
        broken["inventory"] = new List<object> { "plate-armor|equipped:1", "shield|equipped:1", "shield|equipped:1" };
        broken["subclass"] = "evoker";
        broken[SheetRuleEngine.BasePrefix + "str"] = 18L;
        sheets["broken-cleric1"] = broken;
        var noClass = new Dictionary<string, object>();
        Engine().InitializeSheet(noClass);
        noClass["level"] = 3L;
        sheets["broken-no-class"] = noClass;

        // Imported creatures, spread across the bestiary: the stat block as printed, in manual mode.
        var creatures = _registry.GetActorsBySystem("srd5e").Where(a => a.PackId == "srd-5.2.1").OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < creatures.Count; i += creatures.Count / 9)
        {
            var actor = new ActorDocument();
            ActorSheetSync.SeedFromBlueprint(actor, creatures[i], _system, _components);
            sheets[$"creature-{creatures[i].Id}"] = actor.Sheet.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        return sheets;
    }

    private List<ContentEntry> Entries(string type) => _registry.GetEntries("srd5e", type, Packs).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

    private ContentEntry Entry(string type, string id) => Entries(type).Single(e => e.Id == id);

    /// <summary>A legal character: the standard array by the class's priorities (or <paramref name="scores"/> typed in), the
    /// background's bonuses, its origin feat, the class's subclass from level 3, the first skills it offers, armor it is trained
    /// in, and every list filled to its limit with the first options the rules allow (feat slots raise the main score first).</summary>
    private Dictionary<string, object> Build(ContentEntry cls, int level, ContentEntry species, ContentEntry background,
        (string Id, int Level)[]? others = null, Dictionary<string, int>? scores = null)
    {
        var engine = Engine();
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = cls.Id;
        sheet["level"] = (long)level;
        sheet["species"] = species.Id;
        sheet["background"] = background.Id;
        if (background.Properties.GetValueOrDefault("feat") is string feat) sheet["originFeat"] = feat;

        var priority = Priority(cls);
        if (scores == null)
        {
            sheet[SheetRuleEngine.MethodKey] = "standard-array";
            var array = _system.SheetRules!.Attributes!.StandardArray.OrderByDescending(v => v).ToList();
            for (var i = 0; i < priority.Count; i++) sheet[SheetRuleEngine.BasePrefix + priority[i]] = (long)array[i];
        }
        else
        {
            sheet[SheetRuleEngine.MethodKey] = "manual";
            foreach (var (key, score) in scores) sheet[SheetRuleEngine.BasePrefix + key] = (long)score;
        }

        // Background bonuses: +2 and +1 to the two of its three abilities the class wants most.
        var eligible = engine.EligibleBonusAttributes(_system.SheetRules!.Attributes!.Bonuses!, sheet);
        var ranked = eligible.OrderBy(a => priority.IndexOf(a)).ToList();
        if (ranked.Count > 0) sheet[SheetRuleEngine.BonusPrefix + ranked[0]] = 2L;
        if (ranked.Count > 1) sheet[SheetRuleEngine.BonusPrefix + ranked[1]] = 1L;

        if (level >= 3 && Entries("subclass").FirstOrDefault(s => Equals(s.Properties.GetValueOrDefault("class"), cls.Id)) is { } subclass) sheet["subclass"] = subclass.Id;
        if (others != null)
        {
            sheet["multiclass"] = others.Select(o => (object)SheetRuleEngine.FormatClassLevel(new OtherClass(o.Id, o.Level,
                o.Level >= 3 ? Entries("subclass").FirstOrDefault(s => Equals(s.Properties.GetValueOrDefault("class"), o.Id))?.Id : null))).ToList();
        }

        sheet["skillProficiencies"] = SkillChoices(cls).Cast<object>().ToList();
        if (Armor(cls) is { Count: > 0 } armor) sheet["inventory"] = armor.Cast<object>().ToList();

        foreach (var list in _system.SheetRules!.Lists)
        {
            var picks = new List<object>();
            for (var guard = 0; guard < 40; guard++)
            {
                sheet[list.Key] = picks;
                var evaluation = engine.Recompute(new Dictionary<string, object>(sheet));
                var limit = evaluation.ListLimits.GetValueOrDefault(list.Key);
                if (limit is not { } max || picks.Count >= max) break;
                var next = NextPick(list, evaluation, priority, picks);
                if (next == null) break;
                picks.Add(next);
            }

            if (picks.Count == 0) sheet.Remove(list.Key);
        }

        return sheet;
    }

    private string? NextPick(ListRule list, SheetEvaluation evaluation, IReadOnlyList<string> priority, List<object> picks)
    {
        var options = evaluation.ListOptions.GetValueOrDefault(list.Key) ?? Array.Empty<ContentEntry>();
        var increase = _system.SheetRules!.Attributes!.Increases;
        foreach (var option in options)
        {
            if (increase != null && list.Key == increase.FromList && option.Properties.TryGetValue(increase.Property, out var points))
            {
                // Spend a score increase on the class's best scores that are still under the cap.
                var amount = Convert.ToInt32(points, CultureInfo.InvariantCulture);
                var totals = evaluation.Attributes?.Totals ?? new Dictionary<string, int>();
                var cap = option.Properties.TryGetValue(increase.CapProperty, out var c) ? Convert.ToInt32(c, CultureInfo.InvariantCulture) : 20;
                var allowed = option.Properties.TryGetValue(increase.OptionsProperty, out var o) && o is IEnumerable<object> named && named.Any()
                    ? named.Select(n => AbilityKeys.GetValueOrDefault(n.ToString()!, n.ToString()!)).ToHashSet()
                    : priority.ToHashSet();
                var choices = new Dictionary<string, int>();
                foreach (var key in priority.Where(allowed.Contains))
                {
                    var room = cap - totals.GetValueOrDefault(key) - choices.GetValueOrDefault(key);
                    var give = Math.Min(room, amount - choices.Values.Sum());
                    if (give > 0) choices[key] = give;
                    if (choices.Values.Sum() == amount) break;
                }

                if (choices.Values.Sum() == amount) return SheetRuleEngine.FormatPick(option.Id, choices);
                continue;
            }

            if (picks.Contains(option.Id)) continue;
            return option.Id;
        }

        return null;
    }

    private static List<string> Priority(ContentEntry cls)
    {
        var order = new List<string>();
        void Add(string? key) { if (key != null && !order.Contains(key)) order.Add(key); }
        var primary = cls.Properties.GetValueOrDefault("primaryAbility")?.ToString() ?? "";
        foreach (var part in primary.Split(new[] { " and ", " or ", ",", " " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Add(AbilityKeys.GetValueOrDefault(part));
        Add("con");
        Add("dex");
        foreach (var key in new[] { "wis", "str", "cha", "int" }) Add(key);
        return order;
    }

    private static List<string> SkillChoices(ContentEntry cls)
    {
        var text = cls.Properties.GetValueOrDefault("skillChoices")?.ToString() ?? "";
        var colon = text.IndexOf(':');
        var count = System.Text.RegularExpressions.Regex.Match(text, "[0-9]+") is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
        if (colon < 0 || count == 0) return new List<string>();
        return text[(colon + 1)..].Replace(" or ", ",", StringComparison.Ordinal).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !s.StartsWith("any", StringComparison.OrdinalIgnoreCase)).Take(count).ToList();
    }

    private static List<string> Armor(ContentEntry cls)
    {
        var training = cls.Properties.GetValueOrDefault("armorProficiencies") is IEnumerable<object> list ? list.Select(t => t.ToString()!).ToList() : new List<string>();
        bool Has(string word) => training.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));
        var items = new List<string>();
        if (Has("heavy") || Has("all armor")) items.Add("chain-mail|equipped:1");
        else if (Has("medium")) items.Add("scale-mail|equipped:1");
        else if (Has("light")) items.Add("leather-armor|equipped:1");
        if (Has("shield")) items.Add("shield|equipped:1");
        return items;
    }
}
