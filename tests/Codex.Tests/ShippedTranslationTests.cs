using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The translations the repo ships must only name things the system really defines, in every language.</summary>
public class ShippedTranslationTests
{
    public static IEnumerable<object[]> Systems() =>
    [
        ["systems/srd5e"],
        ["examples/simple-d6"],
    ];

    [Theory]
    [MemberData(nameof(Systems))]
    public void SystemTranslations_OnlyReferenceKeysTheSystemDefines(string folder)
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, folder, "system.yaml"));

        Assert.True(system.Translations.ContainsKey("ru"), "missing Russian translation");
        Assert.True(system.Translations.ContainsKey("he"), "missing Hebrew translation");

        foreach (var (language, t) in system.Translations)
        {
            var problems = new List<string>();
            void Check(string section, IEnumerable<string> used, IEnumerable<string> defined)
            {
                var known = defined.ToHashSet(StringComparer.Ordinal);
                problems.AddRange(used.Where(k => !known.Contains(k)).Select(k => $"{language}: {section} '{k}' is not defined by the system"));
            }

            Check("attribute", t.Attributes.Keys, system.Attributes.Select(a => a.Key));
            Check("resource", t.Resources.Keys, system.Resources.Select(r => r.Key));
            Check("actor field", t.ActorFields.Keys, system.ActorFields.Select(f => f.Key));
            Check("entity type", t.EntityTypes.Keys, system.EntityTypes.Select(e => e.Type));
            Check("condition", t.Conditions.Keys, system.Conditions);
            foreach (var (type, translated) in t.EntityTypes)
            {
                var defined = system.EntityTypes.First(e => e.Type == type);
                Check($"{type} field", translated.Fields.Keys, defined.EditorFields().Select(f => f.Key));
            }

            var rules = system.SheetRules;
            var labels = t.SheetRules;
            Check("derived stat", labels.Derived.Keys, rules?.Derived.Select(d => d.Key) ?? []);
            Check("pick list", labels.Lists.Keys, rules?.Lists.Select(l => l.Key) ?? []);
            Check("skill", labels.Skills.Keys, rules?.Skills?.List.Select(k => k.Key) ?? []);
            Check("passive", labels.Passives.Keys, rules?.Skills?.Passives.Select(p => p.Key) ?? []);
            Check("slot pool", labels.SlotPools.Keys, rules?.SlotPools.Select(p => p.Key) ?? []);

            Assert.Empty(problems);

            // Whatever the rules label, the language labels too.
            Assert.All(rules?.Derived ?? [], d => Assert.True(labels.Derived.ContainsKey(d.Key), $"{language}: derived stat {d.Key} untranslated"));
            Assert.All(rules?.Lists ?? [], l => Assert.True(labels.Lists.ContainsKey(l.Key), $"{language}: list {l.Key} untranslated"));
            Assert.All(rules?.Skills?.List ?? [], k => Assert.True(labels.Skills.ContainsKey(k.Key), $"{language}: skill {k.Key} untranslated"));
            Assert.All(rules?.Skills?.Passives ?? [], p => Assert.True(labels.Passives.ContainsKey(p.Key), $"{language}: passive {p.Key} untranslated"));
            Assert.All(rules?.SlotPools ?? [], p => Assert.True(labels.SlotPools.ContainsKey(p.Key), $"{language}: slot pool {p.Key} untranslated"));

            // Every attribute and condition is shown in the language, so a table is never half translated.
            Assert.All(system.Attributes, a => Assert.True(t.Attributes.ContainsKey(a.Key), $"{language}: attribute {a.Key} untranslated"));
            Assert.All(system.Conditions, c => Assert.True(t.Conditions.ContainsKey(c), $"{language}: condition {c} untranslated"));
            Assert.All(system.EntityTypes, e => Assert.True(t.EntityTypes.ContainsKey(e.Type), $"{language}: type {e.Type} untranslated"));
        }
    }

    [Theory]
    [MemberData(nameof(Systems))]
    public void LocalizedSystem_KeepsEveryKeyAndOption(string folder)
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, folder, "system.yaml"));

        foreach (var language in new[] { "ru", "he" })
        {
            var shown = system.Localize(language);
            Assert.Equal(system.Attributes.Select(a => a.Key), shown.Attributes.Select(a => a.Key));
            Assert.Equal(system.EntityTypes.Select(e => e.Type), shown.EntityTypes.Select(e => e.Type));
            for (var i = 0; i < system.EntityTypes.Count; i++)
            {
                Assert.Equal(system.EntityTypes[i].Fields.Select(f => (f.Key, f.Type)), shown.EntityTypes[i].Fields.Select(f => (f.Key, f.Type)));
                Assert.Equal(system.EntityTypes[i].Fields.Select(f => f.Options), shown.EntityTypes[i].Fields.Select(f => f.Options));
            }
            Assert.NotEqual(system.Attributes[0].Label, shown.Attributes[0].Label);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex Cyrillic = new(@"[\u0400-\u04FF]");
    private static readonly System.Text.RegularExpressions.Regex Hebrew = new(@"[\u0590-\u05FF]");

    private static IEnumerable<string> TranslationFiles(string language)
    {
        var root = TestPaths.RepoRoot;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src", "Codex.Localization", "Resources"), $"*.{language}.resx"))
            yield return f;
        foreach (var f in Directory.EnumerateFiles(TestPaths.ContentRoot, "*.yaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            if (name == $"system.{language}.yaml" || f.Contains($"{Path.DirectorySeparatorChar}codex-flavor-{language}{Path.DirectorySeparatorChar}"))
                yield return f;
        }
    }

    [Theory]
    [InlineData("ru", "Hebrew")]
    [InlineData("he", "Cyrillic")]
    public void Translations_NeverMixInTheOtherLanguagesScript(string language, string foreign)
    {
        var foreignScript = foreign == "Hebrew" ? Hebrew : Cyrillic;
        var files = TranslationFiles(language).ToList();
        Assert.True(files.Count >= 10, $"expected the {language} resource and content files, found {files.Count}");

        var offenders = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i + 1)))
            .Where(x => foreignScript.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(TestPaths.RepoRoot, x.f)}:{x.Item3}: {x.line.Trim()[..Math.Min(80, x.line.Trim().Length)]}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoWordMixesCyrillicAndHebrewLetters()
    {
        var mixed = new System.Text.RegularExpressions.Regex(@"[\u0400-\u04FF][\u0590-\u05FF]|[\u0590-\u05FF][\u0400-\u04FF]");
        var offenders = new[] { "ru", "he" }.SelectMany(TranslationFiles)
            .SelectMany(f => File.ReadLines(f).Where(l => mixed.IsMatch(l)).Select(l => $"{Path.GetFileName(f)}: {l.Trim()}"))
            .ToList();

        Assert.Empty(offenders);
    }
}
