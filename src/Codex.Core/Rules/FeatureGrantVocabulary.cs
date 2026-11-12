using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A name a feature may use, and what to call it to a person.</summary>
public sealed record GrantName(string Key, string Label)
{
    /// <summary>"key" when the label adds nothing, else "key (label)".</summary>
    public string Described => string.IsNullOrWhiteSpace(Label) || Label == Key ? Key : $"{Key} ({Label})";
}

/// <summary>A list a feature may add entries to. <see cref="EntityType"/> is the type of the entries of a pick list; null for a
/// tag list. A ranked list (the skills, the saving throws) also has <see cref="Ranks"/>, in order, lowest first (a grant names one by its key or
/// number), and <see cref="Keys"/>, the keys a grant may name (the skills, the attributes); both null for any other list.</summary>
public sealed record GrantList(string Key, string Label, string? EntityType, IReadOnlyList<GrantName>? Ranks = null, IReadOnlyList<GrantName>? Keys = null)
{
    /// <summary>Whether the list holds its keys at ranks (<see cref="SheetRuleEngine.SkillsTarget"/>, <see cref="SheetRuleEngine.SavesTarget"/>).</summary>
    public bool IsRanked => Ranks is { Count: > 0 };
}

/// <summary>Where on an entry of one type the system reads features: the properties holding feature rows, and whether the whole
/// entry is itself a feature.</summary>
public sealed record FeatureCarrier(IReadOnlyList<string> RowProperties, bool WholeEntry);

/// <summary>
/// The names a feature may use to change a sheet, as one system gives them: modifier targets, the names a formula reads, the
/// lists entries can be added to, the rests that refill a counter, the modifier types. The writer's guide
/// (<see cref="FeatureGrantGuide"/>) and the feature editor both read it, so the editor offers what the model is told. Every
/// name comes from the system.
/// </summary>
public sealed class FeatureGrantVocabulary
{
    private readonly SystemDefinition _system;
    private readonly SheetRules _rules;

    private FeatureGrantVocabulary(SystemDefinition system, SheetRules rules)
    {
        _system = system;
        _rules = rules;
        Targets = ComputeTargets(system, rules);
        FormulaNames = ComputeFormulaNames(system, rules).ToList();

        var lists = rules.Lists.Where(l => l.MaxCount == null).Select(l => new GrantList(l.Key, l.Label, l.TargetEntityType)).ToList();
        if (system.Vitals?.Damage is { } damage)
        {
            DamageTypes = damage.Types;
            foreach (var key in new[] { damage.ResistancesField, damage.ImmunitiesField, damage.VulnerabilitiesField })
            {
                if (string.IsNullOrEmpty(key)) continue;
                DamageListKeys.Add(key);
                lists.Add(new GrantList(key, FieldLabel(system, key), null));
            }
        }

        // The ranked lists: a feature adds skills or saving throws at a rank, raises them, or leaves them to a choice.
        if (rules.Skills is { } skills)
        {
            GrantName Rank(RankDefinition r) => new(r.Key, string.IsNullOrWhiteSpace(r.Label) ? r.Key : r.Label);
            if (skills.List.Count > 0) lists.Add(new GrantList(SheetRuleEngine.SkillsTarget, skills.Label, null, skills.SkillRanks().Select(Rank).ToList(), skills.List.Select(s => new GrantName(s.Key, s.Label)).ToList()));
            if (system.Attributes.Count > 0) lists.Add(new GrantList(SheetRuleEngine.SavesTarget, "Saving throws", null, skills.SaveRanks().Select(Rank).ToList(), system.Attributes.Select(a => new GrantName(a.Key, a.Label)).ToList()));
        }

        Lists = lists;
        Rests = (system.Vitals?.Rests.Select(r => r.Name) ?? Enumerable.Empty<string>())
            .Concat(rules.SlotPools.SelectMany(p => p.RecoversOn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ModifierTypes = system.ModifierTypes.Types.Keys.ToList();
        FloorTypes = system.ModifierTypes.Types.Where(t => ModifierStacking.IsFloor(t.Value)).Select(t => t.Key).ToList();
        CeilingTypes = system.ModifierTypes.Types.Where(t => ModifierStacking.IsCeiling(t.Value)).Select(t => t.Key).ToList();
        StackingRules = system.ModifierTypes.Types.ToDictionary(t => t.Key, t => t.Value.ToLowerInvariant() == ModifierStacking.Best ? ModifierStacking.Max : t.Value.ToLowerInvariant());
    }

    /// <summary>The vocabulary of <paramref name="system"/>, or null when it reads no features from entries at all.</summary>
    public static FeatureGrantVocabulary? For(SystemDefinition? system) =>
        system?.SheetRules is { Features.Count: > 0 } rules ? new FeatureGrantVocabulary(system, rules) : null;

    /// <summary>What a modifier may change: armor class, hit points, derived values and Number actor fields.</summary>
    public IReadOnlyList<GrantName> Targets { get; }

    /// <summary>What an amount's formula may read, besides <c>mod(attribute)</c> and the sheet's other numbers.</summary>
    public IReadOnlyList<string> FormulaNames { get; }

    /// <summary>The lists a feature may add to: the lists nothing limits the count of, and the damage resistance, immunity and
    /// vulnerability fields.</summary>
    public IReadOnlyList<GrantList> Lists { get; }

    /// <summary>The keys of <see cref="Lists"/> that hold damage types.</summary>
    public List<string> DamageListKeys { get; } = new();

    /// <summary>The system's damage types; empty when it has none.</summary>
    public IReadOnlyList<string> DamageTypes { get; } = Array.Empty<string>();

    /// <summary>The rests that may refill a counter.</summary>
    public IReadOnlyList<string> Rests { get; }

    /// <summary>The moments besides rests that may refill a counter: rolling initiative, a GM-called dawn, or by hand only.</summary>
    public IReadOnlyList<string> Moments => FeatureRecovery.Moments;

    /// <summary>The moments a choice among a feature's options may name in <c>changesOn</c>: the rests, then initiative and dawn (not <c>manual</c>,
    /// at which nothing happens).</summary>
    public IReadOnlyList<string> ChangeMoments => Rests.Concat(Moments.Where(m => m != FeatureRecovery.Manual)).ToList();

    /// <summary>The modifier types the system names. A modifier without a type is added.</summary>
    public IReadOnlyList<string> ModifierTypes { get; }

    /// <summary>The modifier types that set a floor (the number becomes at least the amount: <c>max</c>, <c>best</c>) instead of adding.</summary>
    public IReadOnlyList<string> FloorTypes { get; }

    /// <summary>The modifier types that set a ceiling (the number becomes at most the amount: <c>min</c>) instead of adding.</summary>
    public IReadOnlyList<string> CeilingTypes { get; }

    /// <summary>Each modifier type the system names and its stacking rule (<c>sum</c>, <c>highest</c>, <c>lowest</c>, <c>max</c>,
    /// <c>min</c>; <c>best</c> is shown as <c>max</c>). Types it does not name sum.</summary>
    public IReadOnlyDictionary<string, string> StackingRules { get; }

    /// <summary>Where entries of <paramref name="entityType"/> carry features, or null when the system reads none from them.</summary>
    public FeatureCarrier? CarrierFor(string? entityType)
    {
        var properties = new List<string>();
        var whole = false;
        foreach (var source in _rules.Features)
        {
            var type = source.List != null
                ? _rules.Lists.FirstOrDefault(l => l.Key == source.List)?.TargetEntityType
                : _system.ActorFields.FirstOrDefault(f => f.Key == source.Field)?.TargetEntityType;
            if (!string.Equals(type, entityType, StringComparison.OrdinalIgnoreCase)) continue;
            if (source.Property == null) whole = true;
            else if (!properties.Contains(source.Property)) properties.Add(source.Property);
        }

        return whole || properties.Count > 0 ? new FeatureCarrier(properties, whole) : null;
    }

    /// <summary>What is wrong with a formula (its text, from the engine's parser), or null when it can be read. Names are not
    /// checked: a formula may read any number the sheet has.</summary>
    public string? CheckFormula(string? formula) => SheetFormulas.For(_system).Check(formula);

    /// <summary>The label a system gives a field key: an actor field's, else the first content type's field of that key (srd5e
    /// declares its damage lists on the creature type only), else the key with its first letter raised.</summary>
    private static string FieldLabel(SystemDefinition system, string key)
    {
        var label = system.ActorFields.FirstOrDefault(f => f.Key == key)?.Label
            ?? system.EntityTypes.SelectMany(t => t.Fields).FirstOrDefault(f => f.Key == key)?.Label;
        return string.IsNullOrWhiteSpace(label) ? char.ToUpperInvariant(key[0]) + key[1..] : label;
    }

    private static List<GrantName> ComputeTargets(SystemDefinition system, SheetRules rules)
    {
        var targets = new List<GrantName>();
        void Add(string key, string? label)
        {
            if (targets.All(t => t.Key != key)) targets.Add(new GrantName(key, string.IsNullOrWhiteSpace(label) ? key : label));
        }

        if (rules.ArmorClass is { } armor) Add(armor.Key, system.ActorFields.FirstOrDefault(f => f.Key == armor.Key)?.Label);
        if (rules.HitPoints is { } hitPoints) Add(hitPoints.Key, system.Resources.FirstOrDefault(r => r.MaxKey == hitPoints.Key)?.Label);
        foreach (var derived in rules.Derived) Add(derived.Key, derived.Label);
        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.Number && f.Key != system.FieldRoles.Level && f.Key != rules.ArmorClass?.MiscField)) Add(field.Key, field.Label);
        return targets;
    }

    private static IEnumerable<string> ComputeFormulaNames(SystemDefinition system, SheetRules rules)
    {
        if (system.FieldRoles.Level is { } level) yield return level;
        if (rules.Multiclass is { } multiclass) yield return multiclass.ClassLevelKey;
        foreach (var derived in rules.Derived) yield return derived.Key;
        foreach (var attribute in system.Attributes) yield return attribute.Key;
    }
}
