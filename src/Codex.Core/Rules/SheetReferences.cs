using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One Library entry a sheet uses: where it sits on the sheet (<paramref name="FieldKey"/>), the content types it may be
/// (an inventory takes several) and its id.</summary>
public sealed record SheetReference(string FieldKey, IReadOnlyList<string> Types, string Id)
{
    public bool Equals(SheetReference? other) =>
        other != null && FieldKey == other.FieldKey && Id == other.Id && Types.SequenceEqual(other.Types, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(FieldKey, Id);

    /// <summary>A stable key for a page to remember a choice about this reference by.</summary>
    public string Key => FieldKey + "|" + Id;
}

/// <summary>
/// What a sheet uses from the Library, read from the system's own rules: entry fields (class, species), pick lists (feats,
/// spells), the inventory and other classes with their subclasses. Bringing a character from the vault into a campaign lists
/// those the campaign does not have, and leaves out the ones the player chooses to (docs/SERVER_ACCESS_AND_VAULT.md 7).
/// </summary>
public static class SheetReferences
{
    /// <summary><paramref name="resolver"/> finds the entries whose features hold the sheet's choices (a feat, a class), so the entries it
    /// picks through them (a spell of Magic Initiate) are read too, as <c>choice__</c> keys; without one, only the sheet's own fields and lists are.</summary>
    public static IReadOnlyList<SheetReference> Of(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, IEntryResolver? resolver = null)
    {
        var found = new List<SheetReference>();
        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.EntityReference && !string.IsNullOrEmpty(f.TargetEntityType)))
        {
            if (YamlValues.GetString(sheet, field.Key) is { Length: > 0 } id) found.Add(new SheetReference(field.Key, new[] { field.TargetEntityType! }, id));
        }

        var rules = system.SheetRules;
        foreach (var list in rules?.Lists ?? Enumerable.Empty<ListRule>())
        {
            foreach (var raw in SheetRuleEngine.RawList(sheet, list.Key))
            {
                found.Add(new SheetReference(list.Key, new[] { list.TargetEntityType }, SheetRuleEngine.ParsePick(raw).Id));
            }
        }

        if (rules?.Inventory is { } inventory)
        {
            foreach (var raw in SheetRuleEngine.RawList(sheet, inventory.Key))
            {
                found.Add(new SheetReference(inventory.Key, inventory.Types, SheetRuleEngine.ParsePick(raw).Id));
            }
        }

        if (rules?.Multiclass is { } multiclass)
        {
            var classType = TypeOf(system, multiclass.ClassField);
            var subclassType = multiclass.SubclassField == null ? null : TypeOf(system, multiclass.SubclassField);
            foreach (var other in SheetRuleEngine.ParseClassLevels(sheet, multiclass.Key))
            {
                if (classType != null) found.Add(new SheetReference(multiclass.Key, new[] { classType }, other.Id));
                if (subclassType != null && !string.IsNullOrEmpty(other.Subclass)) found.Add(new SheetReference(multiclass.Key, new[] { subclassType }, other.Subclass));
            }
        }

        if (resolver != null)
        {
            foreach (var (key, type, ids) in new SheetRuleEngine(system, resolver).EntryPicks(sheet))
            {
                found.AddRange(ids.Select(id => new SheetReference(key, new[] { type }, id)));
            }
        }

        return found.Where(r => r.Id.Length > 0 && r.Types.Count > 0).Distinct().ToList();
    }

    /// <summary>The references <paramref name="resolver"/> (a campaign's Library) cannot find. <paramref name="readWith"/> is the Library the
    /// sheet's choices are read in (the character's own, where its feats are); without one, the campaign's.</summary>
    public static IReadOnlyList<SheetReference> Missing(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, IEntryResolver resolver,
        IEntryResolver? readWith = null) =>
        Of(system, sheet, readWith ?? resolver).Where(r => !r.Types.Any(type => resolver.Resolve(type, r.Id) != null)).ToList();

    /// <summary>A copy of <paramref name="sheet"/> without <paramref name="drop"/>: an entry field is cleared, a list loses the
    /// picks or items, another class goes, and a subclass goes from its class.</summary>
    public static Dictionary<string, object> Without(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, IEnumerable<SheetReference> drop)
    {
        var copy = new Dictionary<string, object>(sheet);
        var dropping = drop.ToList();
        if (dropping.Count == 0) return copy;
        bool Dropped(string key, string id) => dropping.Any(d => d.FieldKey == key && d.Id == id);

        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.EntityReference))
        {
            if (YamlValues.GetString(copy, field.Key) is { Length: > 0 } id && Dropped(field.Key, id)) copy.Remove(field.Key);
        }

        var rules = system.SheetRules;
        var listKeys = (rules?.Lists.Select(l => l.Key) ?? Enumerable.Empty<string>())
            .Concat(rules?.Inventory is { } inventory ? new[] { inventory.Key } : Array.Empty<string>());
        foreach (var key in listKeys)
        {
            if (!copy.ContainsKey(key)) continue;
            copy[key] = SheetRuleEngine.RawList(copy, key).Where(raw => !Dropped(key, SheetRuleEngine.ParsePick(raw).Id)).Cast<object>().ToList();
        }

        // What a choice picks is dropped by the choice's own key.
        foreach (var key in copy.Keys.Where(k => k.StartsWith(SheetRuleEngine.ChoicePrefix, StringComparison.Ordinal) && dropping.Any(d => d.FieldKey == k)).ToList())
        {
            copy[key] = SheetRuleEngine.RawList(copy, key).Where(id => !Dropped(key, id)).Cast<object>().ToList();
        }

        if (rules?.Multiclass is { } multiclass && copy.ContainsKey(multiclass.Key))
        {
            copy[multiclass.Key] = SheetRuleEngine.ParseClassLevels(copy, multiclass.Key)
                .Where(c => !Dropped(multiclass.Key, c.Id))
                .Select(c => !string.IsNullOrEmpty(c.Subclass) && Dropped(multiclass.Key, c.Subclass) ? c with { Subclass = null } : c)
                .Select(SheetRuleEngine.FormatClassLevel)
                .Cast<object>()
                .ToList();
        }

        return copy;
    }

    /// <summary>A copy of <paramref name="sheet"/> where every use of the entry <paramref name="fromId"/> of <paramref name="type"/>
    /// names <paramref name="toId"/> instead: an entry field, a list's picks (their choices kept), an inventory item, another
    /// class or its subclass. A list that already holds <paramref name="toId"/> just loses the old pick.</summary>
    public static Dictionary<string, object> Repointed(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, string type, string fromId, string toId,
        IEntryResolver? resolver = null)
    {
        var copy = new Dictionary<string, object>(sheet);
        bool OfType(string? t) => string.Equals(t, type, StringComparison.OrdinalIgnoreCase);
        bool Same(string id) => string.Equals(id, fromId, StringComparison.OrdinalIgnoreCase);

        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.EntityReference && OfType(f.TargetEntityType)))
        {
            if (YamlValues.GetString(copy, field.Key) is { Length: > 0 } id && Same(id)) copy[field.Key] = toId;
        }

        var rules = system.SheetRules;
        var listKeys = (rules?.Lists.Where(l => OfType(l.TargetEntityType)).Select(l => l.Key) ?? Enumerable.Empty<string>())
            .Concat(rules?.Inventory is { } inventory && inventory.Types.Any(OfType) ? new[] { inventory.Key } : Array.Empty<string>());
        foreach (var key in listKeys)
        {
            if (!copy.ContainsKey(key)) continue;
            var raw = SheetRuleEngine.RawList(copy, key);
            var has = raw.Any(r => string.Equals(SheetRuleEngine.ParsePick(r).Id, toId, StringComparison.OrdinalIgnoreCase));
            copy[key] = raw
                .Where(r => !(has && Same(SheetRuleEngine.ParsePick(r).Id)))
                .Select(r => SheetRuleEngine.ParsePick(r) is var (id, choices) && Same(id) ? SheetRuleEngine.FormatPick(toId, choices) : r)
                .Cast<object>()
                .ToList();
        }

        // Picks of a choice over entries of this type name the other entry; a choice already holding it just loses the old pick.
        if (resolver != null)
        {
            foreach (var (key, _, _) in new SheetRuleEngine(system, resolver).EntryPicks(sheet).Where(p => OfType(p.Type)))
            {
                var raw = SheetRuleEngine.RawList(copy, key);
                var has = raw.Any(id => string.Equals(id, toId, StringComparison.OrdinalIgnoreCase));
                copy[key] = raw.Where(id => !(has && Same(id))).Select(id => Same(id) ? toId : id).Cast<object>().ToList();
            }
        }

        if (rules?.Multiclass is { } multiclass && copy.ContainsKey(multiclass.Key))
        {
            var classType = OfType(TypeOf(system, multiclass.ClassField));
            var subclassType = multiclass.SubclassField != null && OfType(TypeOf(system, multiclass.SubclassField));
            copy[multiclass.Key] = SheetRuleEngine.ParseClassLevels(copy, multiclass.Key)
                .Select(c => classType && Same(c.Id) ? c with { Id = toId } : c)
                .Select(c => subclassType && !string.IsNullOrEmpty(c.Subclass) && Same(c.Subclass) ? c with { Subclass = toId } : c)
                .Select(SheetRuleEngine.FormatClassLevel)
                .Cast<object>()
                .ToList();
        }

        return copy;
    }

    /// <summary>The entries of <paramref name="campaign"/> (a campaign's Library) that the sheet's rules would take in the place of the
    /// entry <paramref name="id"/> of <paramref name="type"/>, in every slot of the sheet that uses it: the options the rules
    /// offer for a constrained field (a subclass of the class), a pick list or the other classes, and any entry of the type where the
    /// rules offer no list (an inventory). <paramref name="evaluation"/> is the sheet read with the entry added.</summary>
    public static IReadOnlyList<ContentEntry> Replacements(SystemDefinition system, IReadOnlyDictionary<string, object> sheet,
        SheetEvaluation evaluation, IEntryResolver campaign, string type, string id)
    {
        var uses = Of(system, sheet)
            .Where(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase) && r.Types.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (uses.Count == 0) return Array.Empty<ContentEntry>();
        var multiclass = system.SheetRules?.Multiclass;
        HashSet<string>? allowed = null;
        foreach (var use in uses)
        {
            IReadOnlyList<ContentEntry>? options = null;
            if (evaluation.AllowedOptions.TryGetValue(use.FieldKey, out var constrained)) options = constrained;
            else if (evaluation.ListOptions.TryGetValue(use.FieldKey, out var listed)) options = listed;
            else if (multiclass != null && use.FieldKey == multiclass.Key && string.Equals(TypeOf(system, multiclass.ClassField), type, StringComparison.OrdinalIgnoreCase)) options = evaluation.MulticlassOptions;
            if (options == null) continue;

            var ids = options.Where(o => string.Equals(o.Type, type, StringComparison.OrdinalIgnoreCase)).Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowed == null) allowed = ids;
            else allowed.IntersectWith(ids);
        }

        return campaign.Candidates(type)
            .Where(e => !string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase) && (allowed == null || allowed.Contains(e.Id)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TypeOf(SystemDefinition system, string fieldKey) =>
        system.ActorFields.FirstOrDefault(f => f.Key == fieldKey)?.TargetEntityType;
}
