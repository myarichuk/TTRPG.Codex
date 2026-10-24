using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Prefixes every label a system defines with "§", keeping the original word inside. Messages that merely show a label still
/// contain it, but any rule that compares a displayed label against pack or sheet data stops matching, which is exactly the
/// mistake a real translation would expose.
/// </summary>
public static class PseudoTranslation
{
    private static string P(string text) => "§" + text;

    public static SystemDefinition Localize(SystemDefinition system, string language = "ru")
    {
        var t = new SystemTranslation { Name = P(system.Name) };

        foreach (var a in system.Attributes)
            t.Attributes[a.Key] = new LabelTranslation { Label = P(a.Label), Abbreviation = a.Abbreviation == null ? null : P(a.Abbreviation) };
        foreach (var r in system.Resources)
            t.Resources[r.Key] = new LabelTranslation { Label = P(r.Label) };
        foreach (var f in system.ActorFields)
            t.ActorFields[f.Key] = Field(f);
        foreach (var e in system.EntityTypes)
        {
            var type = new TypeTranslation { Label = P(e.Label), PluralLabel = P(e.PluralOrLabel) };
            foreach (var f in e.Fields) type.Fields[f.Key] = Field(f);
            t.EntityTypes[e.Type] = type;
        }
        foreach (var c in system.Conditions)
            t.Conditions[c] = P(c);
        foreach (var option in system.EntityTypes.SelectMany(e => e.Fields).Concat(system.ActorFields).SelectMany(f => f.Options ?? new List<string>()).Distinct())
            t.Values[option] = P(option);

        if (system.SheetRules is { } rules)
        {
            foreach (var d in rules.Derived) t.SheetRules.Derived[d.Key] = P(d.Label);
            foreach (var l in rules.Lists) t.SheetRules.Lists[l.Key] = P(l.Label);
            foreach (var p in rules.SlotPools) t.SheetRules.SlotPools[p.Key] = P(p.Label);
            foreach (var s in rules.Skills?.List ?? new List<SkillDefinition>()) t.SheetRules.Skills[s.Key] = P(s.Label);
            foreach (var s in rules.Skills?.Passives ?? new List<PassiveRule>()) t.SheetRules.Passives[s.Key] = P(s.Label);
            if (rules.Skills != null) t.SheetRules.SkillsLabel = P(rules.Skills.Label);
            if (rules.Inventory != null) t.SheetRules.InventoryLabel = P(rules.Inventory.Label);
            if (rules.Multiclass != null) t.SheetRules.MulticlassLabel = P(rules.Multiclass.Label);
        }

        system.Translations[language] = t;
        return system.Localize(language);
    }

    private static LabelTranslation Field(FieldDefinition f) => new()
    {
        Label = P(f.Label),
        Description = f.Description == null ? null : P(f.Description),
        Category = f.Category == null ? null : P(f.Category)
    };
}
