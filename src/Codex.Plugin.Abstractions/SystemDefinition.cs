using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// Licence and attribution metadata for a system or a content pack. Content under CC-BY (the 5E
/// SRD), ORC, or similar must be displayed with its attribution wherever it is used, so this
/// travels with the data rather than living in a README nobody at the table ever sees.
/// </summary>
public class LicenseInfo
{
    /// <summary>Short licence identifier, e.g. "CC-BY-4.0", "ORC", "MIT", "All rights reserved".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Link to the licence's legal text.</summary>
    public string? Url { get; set; }

    /// <summary>The verbatim attribution statement the licensor requires, if any.</summary>
    public string? Attribution { get; set; }

    /// <summary>Where the material came from (the SRD page, the upstream repository, ...).</summary>
    public string? SourceUrl { get; set; }

    /// <summary>CC-BY §3(a)(1)(B): say so if the material was changed on the way in.</summary>
    public bool Modified { get; set; }

    /// <summary>Free text for additional notices (e.g. an upstream MIT notice for a data transcription).</summary>
    public List<string> Notices { get; set; } = new();
}

/// <summary>A character attribute (ability score, characteristic, stat) the system tracks per actor.</summary>
public class AttributeDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Abbreviation { get; set; }

    /// <summary>The label and abbreviation as system.yaml wrote them. A localized copy shows translated text in
    /// <see cref="Label"/>, but sheet data and pack entries refer to the original words ("STR 13+", "Strength"),
    /// so rules match on these as well.</summary>
    public string? CanonicalLabel { get; set; }
    public string? CanonicalAbbreviation { get; set; }
    public int Default { get; set; } = 10;
    public int? Min { get; set; }
    public int? Max { get; set; }

    /// <summary>
    /// Optional derived-modifier rule. Supported: <c>"half-minus-five"</c> (floor((v-10)/2), the d20
    /// family) or <c>"none"</c>. Systems that need anything else define a derived field instead.
    /// </summary>
    public string Modifier { get; set; } = "none";
}

/// <summary>A depletable pool (hit points, mana, strain, luck) on an actor.</summary>
public class ResourceDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>Pool key that holds the maximum, e.g. "HP_Max". Null for pools without a cap.</summary>
    public string? MaxKey { get; set; }

    public int Default { get; set; }

    /// <summary>True for the pool damage/healing target by default (usually hit points).</summary>
    public bool IsPrimary { get; set; }
}

/// <summary>
/// One kind of content a system's packs can carry (class, feat, spell, monster, item, ...), with the
/// field schema the Library browser and homebrew editor render it with.
/// </summary>
public class ContentTypeDefinition
{
    /// <summary>Stable key, singular, lower-case: "spell".</summary>
    public string Type { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
    public string? PluralLabel { get; set; }

    /// <summary>Pack folder holding entries of this type; defaults to <see cref="Type"/>.</summary>
    public string? Folder { get; set; }

    public string? Icon { get; set; }
    public string? Description { get; set; }

    public List<FieldDefinition> Fields { get; set; } = new();

    /// <summary>
    /// The fields an editor for this type offers: <see cref="Fields"/>, then the lore every entry can carry
    /// (<see cref="ContentEntry.FlavorKeys"/>) unless the system declares the key itself, so a table's own
    /// entries can have flavor text and art prompts like the shipped ones. Creature types also get a
    /// habitat and a behavior line.
    /// </summary>
    public List<FieldDefinition> EditorFields()
    {
        const string group = "Lore";
        var lore = new List<FieldDefinition>
        {
            new("flavor", "Flavor", FieldType.LongText, description: "A few sentences of lore, shown as a quote above the rules text.", category: group),
            new("imagePrompt", "Art prompt", FieldType.LongText, description: "One visual sentence describing it, used when generating art.", category: group)
        };

        if (ActorBlueprint)
        {
            lore.Insert(1, new FieldDefinition("habitat", "Habitat", FieldType.Text, description: "Where it lives.", category: group));
            lore.Insert(2, new FieldDefinition("behavior", "Behavior", FieldType.Text, description: "How it acts.", category: group));
        }

        return Fields.Concat(lore.Where(l => !Fields.Any(f => string.Equals(f.Key, l.Key, System.StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    /// <summary>Property keys shown in list rows next to the name (e.g. level, school for spells).</summary>
    public List<string> SummaryFields { get; set; } = new();

    /// <summary>Property keys offered as facet filters in the browser (e.g. level, school, class).</summary>
    public List<string> FilterFields { get; set; } = new();

    /// <summary>
    /// True when entries of this type describe creatures the DM can drop into an encounter. They are
    /// registered as actor blueprints as well as library entries.
    /// </summary>
    public bool ActorBlueprint { get; set; }

    /// <summary>For actor blueprints: the property holding starting hit points (default "hitPoints").</summary>
    public string? HitPointsProperty { get; set; }

    /// <summary>For actor blueprints: property -> attribute key map (e.g. "strength" -> "str").</summary>
    public Dictionary<string, string> AttributeProperties { get; set; } = new();

    /// <summary>True for types a player may pick on their own sheet (class, species, background...).</summary>
    public bool PlayerFacing { get; set; } = true;

    public string FolderOrType => string.IsNullOrWhiteSpace(Folder) ? Type : Folder;
    public string PluralOrLabel => string.IsNullOrWhiteSpace(PluralLabel) ? Label : PluralLabel;
}

/// <summary>Dice conventions the runtime uses when it needs to roll something for a system.</summary>
public class DiceConventions
{
    public string Initiative { get; set; } = "1d20";
    public string Check { get; set; } = "1d20";

    /// <summary>The derived field (see <c>sheetRules.derived</c>) added to an initiative roll, e.g.
    /// an <c>initiative</c> field with the formula <c>mod(dex)</c>. Null: the bare die.</summary>
    public string? InitiativeBonus { get; set; }

    /// <summary>Attribute whose score breaks initiative ties (higher first). Null: ties fall to actor id.</summary>
    public string? InitiativeTieBreak { get; set; }
}

/// <summary>
/// What happens to a creature's vitals at the table (<c>vitals:</c> in system.yaml): dropping to zero,
/// death saves, damage types, exhaustion, hit dice and rests. Every part is optional; a system that
/// leaves one out gets the plain behaviour (damage stops at zero, rests refill only what slot pools say).
/// </summary>
public class VitalsRules
{
    /// <summary>Resource that absorbs damage before the primary one ("TempHP"); granted as "take the higher".</summary>
    public string? TemporaryResource { get; set; }

    public DownRules? Down { get; set; }
    public DeathSaveRules? DeathSaves { get; set; }
    public DamageTypeRules? Damage { get; set; }
    public ExhaustionRules? Exhaustion { get; set; }
    public HitDiceRules? HitDice { get; set; }

    /// <summary>Rests the table offers, by name; a slot pool's <c>recoversOn</c> names one of these.</summary>
    public List<RestRule> Rests { get; set; } = new();

    public RestRule? FindRest(string name) =>
        Rests.Find(r => string.Equals(r.Name, name, System.StringComparison.OrdinalIgnoreCase));
}

/// <summary>What reaching zero on the primary resource does.</summary>
public class DownRules
{
    /// <summary>Condition applied to a player character at zero ("Unconscious").</summary>
    public string Condition { get; set; } = "Unconscious";

    /// <summary>Condition that marks a dead creature; monsters and NPCs get it straight away at zero.</summary>
    public string DeadCondition { get; set; } = "Dead";

    /// <summary>Condition for a creature at zero that is no longer making death saves.</summary>
    public string StableCondition { get; set; } = "Stable";

    /// <summary>When the damage left over after reaching zero is at least the maximum, the creature dies outright.</summary>
    public bool InstantDeathAtMax { get; set; } = true;

    /// <summary>Non-player creatures die at zero instead of falling unconscious.</summary>
    public bool NonPlayersDieAtZero { get; set; } = true;
}

public class DeathSaveRules
{
    public string Die { get; set; } = "1d20";
    public int SuccessOn { get; set; } = 10;
    public int SuccessesToStabilize { get; set; } = 3;
    public int FailuresToDie { get; set; } = 3;

    /// <summary>A natural roll this high regains one hit point. Null: no critical success.</summary>
    public int? CriticalSuccess { get; set; } = 20;

    /// <summary>A natural roll this low counts as two failures. Null: no critical failure.</summary>
    public int? CriticalFailure { get; set; } = 1;

    /// <summary>Pool keys the running tally is kept in.</summary>
    public string SuccessesKey { get; set; } = "DeathSaveSuccesses";
    public string FailuresKey { get; set; } = "DeathSaveFailures";
}

/// <summary>Damage types and the sheet lists that halve, cancel or double them.</summary>
public class DamageTypeRules
{
    public List<string> Types { get; set; } = new();
    public string ResistancesField { get; set; } = "resistances";
    public string ImmunitiesField { get; set; } = "immunities";
    public string VulnerabilitiesField { get; set; } = "vulnerabilities";
}

/// <summary>Exhaustion as a level counter kept in a pool, not a timed condition.</summary>
public class ExhaustionRules
{
    public string Key { get; set; } = "Exhaustion";
    public int MaxLevel { get; set; } = 6;

    /// <summary>The condition name in <see cref="SystemDefinition.Conditions"/> this replaces in the picker.</summary>
    public string? Condition { get; set; } = "Exhaustion";
}

/// <summary>A pool of hit dice: one per level for characters, the stat block's count for creatures.</summary>
public class HitDiceRules
{
    public string Key { get; set; } = "HitDice";
    public string LevelField { get; set; } = "level";
    public string ClassField { get; set; } = "class";

    /// <summary>Class entry property with the die ("d10").</summary>
    public string DieProperty { get; set; } = "hitDie";

    /// <summary>Creature property with the dice expression ("2d6") that gives count and die.</summary>
    public string? CreatureDiceProperty { get; set; } = "hitDice";

    /// <summary>Attribute whose modifier is added to each die spent.</summary>
    public string Attribute { get; set; } = "con";

    public string MaxKey => Key + "_Max";
}

public class RestRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Resources refilled to their maximum ("HP").</summary>
    public List<string> Restores { get; set; } = new();

    /// <summary>Resources set to zero ("TempHP").</summary>
    public List<string> Clears { get; set; } = new();

    /// <summary>Hit dice regained: "all", "half" (rounded down, at least one) or "none".</summary>
    public string HitDiceRegain { get; set; } = "none";

    /// <summary>Whether the table offers spending hit dice during this rest.</summary>
    public bool SpendHitDice { get; set; }

    /// <summary>Change to the exhaustion level (-1 for a long rest).</summary>
    public int ExhaustionDelta { get; set; }

    /// <summary>Conditions removed by the rest.</summary>
    public List<string> ClearsConditions { get; set; } = new();
}

/// <summary>
/// A whole game system described as data (<c>system.yaml</c>). This is what lets a homebrew system
/// exist without compiling a plugin: attributes, resources, the actor sheet, the content types its
/// packs may contain, conditions, and the licence the rules text is under.
/// </summary>
public class SystemDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Version { get; set; } = "1.0.0";

    /// <summary>What the system may call itself in relation to a trademarked product, e.g. "5E compatible".</summary>
    public string? Compatibility { get; set; }

    public LicenseInfo? License { get; set; }

    public List<AttributeDefinition> Attributes { get; set; } = new();
    public List<ResourceDefinition> Resources { get; set; } = new();

    /// <summary>Extra sheet fields beyond attributes and resources (level, class, alignment, speed...).</summary>
    public List<FieldDefinition> ActorFields { get; set; } = new();

    public List<ContentTypeDefinition> EntityTypes { get; set; } = new();

    /// <summary>Condition names the combat console offers in its picker.</summary>
    public List<string> Conditions { get; set; } = new();

    public DiceConventions Dice { get; set; } = new();

    /// <summary>Table rules for hit points, death, damage types, exhaustion, hit dice and rests. Null: none.</summary>
    public VitalsRules? Vitals { get; set; }

    /// <summary>Character-sheet rules (attribute generation, pick constraints, lists, derived
    /// fields). Null means "no rules": any value goes, which is what a loose homebrew wants.</summary>
    public SheetRules? SheetRules { get; set; }

    /// <summary>Translations by language tag ("ru"), filled by the loader from <c>system.&lt;locale&gt;.yaml</c> files.</summary>
    public Dictionary<string, SystemTranslation> Translations { get; set; } = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SystemDefinition> _localized = new();

    private static string LanguageOf(string? culture)
    {
        var language = (culture ?? "").Trim().Split('-', '_')[0].ToLowerInvariant();
        return language switch { "en" => "", "iw" => "he", _ => language };
    }

    private SystemTranslation? TranslationFor(string? culture) =>
        Translations.TryGetValue(LanguageOf(culture), out var t) ? t : null;

    /// <summary>
    /// A copy whose labels, descriptions and categories are shown in <paramref name="culture"/>; the original when the system
    /// has no translation for it. Keys, field types, options and every rule are untouched, so the copy is for display only:
    /// run the rules engine on the original.
    /// </summary>
    public SystemDefinition Localize(string? culture)
    {
        var language = LanguageOf(culture);
        if (language.Length == 0 || !Translations.TryGetValue(language, out var t)) return this;
        return _localized.GetOrAdd(language, _ => Apply(t));
    }

    /// <summary>The text to show for a dropdown/tag value such as "Evocation"; the value itself when untranslated.</summary>
    public string DisplayOption(string? culture, string fieldKey, string value)
    {
        var t = TranslationFor(culture);
        if (t == null) return value;
        foreach (var fields in new[] { t.ActorFields }.Concat(t.EntityTypes.Values.Select(x => x.Fields)))
        {
            if (fields.TryGetValue(fieldKey, out var f) && f.Options.TryGetValue(value, out var shown)) return shown;
        }

        return t.Values.TryGetValue(value, out var common) ? common : value;
    }

    /// <summary>The text to show for a condition name ("Poisoned"); the name itself when untranslated.</summary>
    public string DisplayCondition(string? culture, string condition) =>
        TranslationFor(culture) is { } t && t.Conditions.TryGetValue(condition, out var shown) ? shown : condition;

    private SystemDefinition Apply(SystemTranslation t)
    {
        FieldDefinition Field(FieldDefinition f, LabelTranslation? x) => new()
        {
            Key = f.Key,
            Label = x?.Label ?? f.Label,
            Type = f.Type,
            Description = x?.Description ?? f.Description,
            Options = f.Options,
            DefaultValue = f.DefaultValue,
            IsRequired = f.IsRequired,
            TargetEntityType = f.TargetEntityType,
            Category = x?.Category ?? f.Category,
            Min = f.Min,
            Max = f.Max,
            OptionLabels = OptionLabelsFor(t, x)
        };

        static LabelTranslation? Find(Dictionary<string, LabelTranslation> map, string key) =>
            map.TryGetValue(key, out var hit) ? hit : null;

        var copy = (SystemDefinition)MemberwiseClone();
        copy.Name = t.Name ?? Name;
        copy.Description = t.Description ?? Description;
        copy.Compatibility = t.Compatibility ?? Compatibility;
        copy.Attributes = Attributes.Select(a => new AttributeDefinition
        {
            Key = a.Key, Default = a.Default, Min = a.Min, Max = a.Max, Modifier = a.Modifier,
            Label = Find(t.Attributes, a.Key)?.Label ?? a.Label,
            Abbreviation = Find(t.Attributes, a.Key)?.Abbreviation ?? a.Abbreviation,
            CanonicalLabel = a.CanonicalLabel ?? a.Label,
            CanonicalAbbreviation = a.CanonicalAbbreviation ?? a.Abbreviation
        }).ToList();
        copy.Resources = Resources.Select(r => new ResourceDefinition
        {
            Key = r.Key, MaxKey = r.MaxKey, Default = r.Default, IsPrimary = r.IsPrimary,
            Label = Find(t.Resources, r.Key)?.Label ?? r.Label
        }).ToList();
        copy.ActorFields = ActorFields.Select(f => Field(f, Find(t.ActorFields, f.Key))).ToList();
        copy.EntityTypes = EntityTypes.Select(e =>
        {
            t.EntityTypes.TryGetValue(e.Type, out var et);
            return new ContentTypeDefinition
            {
                Type = e.Type, Folder = e.Folder, Icon = e.Icon,
                Label = et?.Label ?? e.Label,
                PluralLabel = et?.PluralLabel ?? e.PluralLabel,
                Description = et?.Description ?? e.Description,
                Fields = e.Fields.Select(f => Field(f, et == null ? null : Find(et.Fields, f.Key))).ToList(),
                SummaryFields = e.SummaryFields, FilterFields = e.FilterFields, ActorBlueprint = e.ActorBlueprint,
                HitPointsProperty = e.HitPointsProperty, AttributeProperties = e.AttributeProperties, PlayerFacing = e.PlayerFacing
            };
        }).ToList();
        copy.SheetRules = SheetRules == null ? null : ApplyRuleLabels(SheetRules, t.SheetRules);
        copy.Translations = Translations;
        copy.ConditionLabels = new Dictionary<string, string>(t.Conditions);
        return copy;
    }

    private static readonly System.Reflection.MethodInfo ShallowCopy =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static T Copy<T>(T value) where T : class => (T)ShallowCopy.Invoke(value, null)!;

    private static string Pick(Dictionary<string, string> map, string key, string original) =>
        map.TryGetValue(key, out var shown) && !string.IsNullOrWhiteSpace(shown) ? shown : original;

    /// <summary>The rules with their labels translated. Only the label-bearing objects on the way down are copied; every
    /// formula, condition and key is shared with the original.</summary>
    private static SheetRules ApplyRuleLabels(SheetRules rules, SheetRulesTranslation t)
    {
        var copy = Copy(rules);
        copy.Derived = rules.Derived.Select(d => { var c = Copy(d); c.Label = Pick(t.Derived, d.Key, d.Label); return c; }).ToList();
        copy.Lists = rules.Lists.Select(l => { var c = Copy(l); c.Label = Pick(t.Lists, l.Key, l.Label); return c; }).ToList();
        copy.SlotPools = rules.SlotPools.Select(p => { var c = Copy(p); c.Label = Pick(t.SlotPools, p.Key, p.Label); return c; }).ToList();
        if (rules.Inventory != null)
        {
            copy.Inventory = Copy(rules.Inventory);
            copy.Inventory.Label = t.InventoryLabel ?? rules.Inventory.Label;
        }
        if (rules.Multiclass != null)
        {
            copy.Multiclass = Copy(rules.Multiclass);
            copy.Multiclass.Label = t.MulticlassLabel ?? rules.Multiclass.Label;
        }
        if (rules.Skills != null)
        {
            var skills = Copy(rules.Skills);
            skills.Label = t.SkillsLabel ?? rules.Skills.Label;
            skills.List = rules.Skills.List.Select(s => { var c = Copy(s); c.CanonicalLabel = s.CanonicalLabel ?? s.Label; c.Label = Pick(t.Skills, s.Key, s.Label); return c; }).ToList();
            skills.Passives = rules.Skills.Passives.Select(p => { var c = Copy(p); c.Label = Pick(t.Passives, p.Key, p.Label); return c; }).ToList();
            copy.Skills = skills;
        }
        return copy;
    }

    /// <summary>Shared values first, then this field's own options, so a field can word a value differently.</summary>
    private static Dictionary<string, string>? OptionLabelsFor(SystemTranslation t, LabelTranslation? field)
    {
        if (t.Values.Count == 0 && (field == null || field.Options.Count == 0)) return null;
        var labels = new Dictionary<string, string>(t.Values);
        if (field != null) foreach (var (value, shown) in field.Options) labels[value] = shown;
        return labels;
    }

    /// <summary>Set on a localized copy: condition name as the rules know it -> name to show.</summary>
    public Dictionary<string, string>? ConditionLabels { get; set; }

    /// <summary>The name to show for a condition ("Poisoned"); the name itself when this copy has no translation for it.</summary>
    public string DisplayCondition(string condition) =>
        ConditionLabels != null && ConditionLabels.TryGetValue(condition, out var shown) ? shown : condition;

    public ContentTypeDefinition? FindType(string typeOrFolder)
    {
        foreach (var type in EntityTypes)
        {
            if (string.Equals(type.Type, typeOrFolder, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.FolderOrType, typeOrFolder, System.StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return null;
    }

    public ResourceDefinition? PrimaryResource
    {
        get
        {
            foreach (var resource in Resources)
            {
                if (resource.IsPrimary) return resource;
            }

            return Resources.Count > 0 ? Resources[0] : null;
        }
    }
}
