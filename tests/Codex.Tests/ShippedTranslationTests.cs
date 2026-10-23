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

            Assert.Empty(problems);

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
}
