using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// The sections a sheet is laid out in (overhaul section 3, "Layout"). A sheet kind's declared layout; for a kind with none, the
/// generic one, which shows every value, track, list and keyed list in turn; for a system without sheet kinds, its own
/// <see cref="SystemDefinition.Layout"/> or else the built-in widgets in the order the sheet has always had them.
/// </summary>
public static class SheetLayout
{
    /// <summary>Widgets any system may use, bound to its own keys.</summary>
    public static readonly IReadOnlySet<string> GenericWidgets =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "values", "track", "list", "table", "roll", "text", "modifiers", "moments" };

    /// <summary>The widgets the host builds in, and what each reads: 5e's panels, which read <see cref="SheetRules"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> BuiltinWidgets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["attributes"] = "attributes",
        ["skills"] = "sheetRules.skills",
        ["fields"] = "actorFields",
        ["classes"] = "sheetRules.multiclass",
        ["library"] = "actorFields",
        ["picks"] = "sheetRules.lists",
        ["inventory"] = "sheetRules.inventory",
        ["hit-points"] = "primaryResource",
        ["armor-class"] = "sheetRules.armorClass",
        ["slots"] = "sheetRules.slotPools",
        ["actions"] = "sheetRules.actions",
        ["features"] = "features",
        ["level-up"] = "fieldRoles.level"
    };

    /// <summary>The section a generic layout is in.</summary>
    public const string GenericSection = "sheet";

    /// <summary>The sheet kind a sheet is: the one its <c>kind</c> names, else the system's first (the same rule as <see cref="SheetModel"/>).</summary>
    public static SheetKindDefinition? KindOf(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var kind = YamlValues.GetString(sheet, SheetModel.KindKey);
        return system.SheetKinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase)) ?? system.SheetKinds.FirstOrDefault();
    }

    /// <summary>The kinds a new actor of a host role may be: those whose <c>role</c> is it, then those with none.</summary>
    public static IReadOnlyList<SheetKindDefinition> KindsFor(SystemDefinition system, string role) =>
        system.SheetKinds.Where(k => string.Equals(k.Role, role, StringComparison.OrdinalIgnoreCase))
            .Concat(system.SheetKinds.Where(k => string.IsNullOrEmpty(k.Role)))
            .ToList();

    /// <summary>The layout the system declares for this sheet; empty when it declares none.</summary>
    public static IReadOnlyList<LayoutSection> Declared(SystemDefinition system, IReadOnlyDictionary<string, object> sheet) =>
        system.SheetKinds.Count > 0 ? KindOf(system, sheet)?.Layout ?? new List<LayoutSection>() : system.Layout;

    /// <summary>The layout the sheet is shown in: declared, or the generic or built-in one.</summary>
    public static IReadOnlyList<LayoutSection> For(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var declared = Declared(system, sheet);
        if (declared.Count > 0) return declared;
        return system.SheetKinds.Count > 0 ? Generic(KindOf(system, sheet)!) : Builtin;
    }

    /// <summary>Today's sheet as a layout of built-in widgets: what srd5e's <c>layout:</c> says, and what a system without one gets.</summary>
    public static readonly IReadOnlyList<LayoutSection> Builtin = new List<LayoutSection>
    {
        Section("attributes", "zap", false, "attributes", "skills", "fields", "classes", "library"),
        Section("picks", "sparkles", false, "picks"),
        Section("inventory", "package", false, "inventory"),
        Section("vitals", "heart", true, "hit-points", "armor-class", "slots", "actions", "features", "level-up")
    };

    private static LayoutSection Section(string name, string icon, bool aside, params string[] widgets) => new()
    {
        Section = name,
        Icon = icon,
        Aside = aside,
        Widgets = widgets.Select(w => new LayoutWidget { Widget = w }).ToList()
    };

    /// <summary>A kind's every value, then its tracks, lists and keyed lists, in one section.</summary>
    public static IReadOnlyList<LayoutSection> Generic(SheetKindDefinition kind)
    {
        var widgets = new List<LayoutWidget>();
        if (kind.Values.Count > 0) widgets.Add(new LayoutWidget { Widget = "values", Keys = kind.Values.Select(v => v.Key).ToList() });
        foreach (var track in kind.Tracks)
        {
            widgets.Add(new LayoutWidget { Widget = "track", Key = track.Each != null ? Wildcard(track.Key) : track.Key });
        }

        foreach (var list in kind.Lists)
        {
            widgets.Add(new LayoutWidget { Widget = "list", Key = list.Key, Columns = list.ItemFields.Select(f => f.Key).ToList() });
        }

        foreach (var list in kind.KeyedLists)
        {
            widgets.Add(new LayoutWidget
            {
                Widget = "table",
                Key = list.Key,
                Columns = list.ItemFields.Select(f => f.Key).Concat(list.Derive.Keys.Where(k => k == "value")).ToList()
            });
        }

        return widgets.Count == 0 ? new List<LayoutSection>() : new List<LayoutSection> { new() { Section = GenericSection, Icon = "file-text", Widgets = widgets } };
    }

    // "slots_{item.id}_{level}" → "slots_*": the part before the first template brace.
    private static string Wildcard(string template) => (template.IndexOf('{') is var brace and >= 0 ? template[..brace] : template) + "*";

    /// <summary>Whether a track widget's key (<c>stress</c>, or <c>slots_*</c>) names this track.</summary>
    public static bool Matches(string widgetKey, string trackKey) =>
        widgetKey.EndsWith('*') ? trackKey.StartsWith(widgetKey[..^1], StringComparison.Ordinal) : string.Equals(widgetKey, trackKey, StringComparison.Ordinal);

    /// <summary>What is wrong with the layouts a system declares, each naming where it is.</summary>
    public static IEnumerable<string> Problems(SystemDefinition system)
    {
        foreach (var problem in Problems(system, null, system.Layout, "layout")) yield return problem;
        foreach (var kind in system.SheetKinds)
        {
            foreach (var problem in Problems(system, kind, kind.Layout, $"sheetKinds.{kind.Kind}.layout")) yield return problem;
        }
    }

    private static IEnumerable<string> Problems(SystemDefinition system, SheetKindDefinition? kind, List<LayoutSection> layout, string at)
    {
        if (layout.Count > 0 && kind == null && system.SheetKinds.Count > 0)
        {
            yield return $"{at}: the system has sheet kinds, so each kind's own layout is read and this one is not.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in layout)
        {
            var where = $"{at}.{(string.IsNullOrWhiteSpace(section.Section) ? "?" : section.Section)}";
            if (string.IsNullOrWhiteSpace(section.Section)) yield return $"{where}: a section needs a name.";
            else if (!seen.Add(section.Section)) yield return $"{where}: there are two sections of that name.";
            if (section.Widgets.Count == 0) yield return $"{where}: the section has no widgets.";
            foreach (var widget in section.Widgets)
            {
                foreach (var problem in WidgetProblems(system, kind, widget)) yield return $"{where}: {problem}";
            }
        }
    }

    private static IEnumerable<string> WidgetProblems(SystemDefinition system, SheetKindDefinition? kind, LayoutWidget widget)
    {
        var name = widget.Widget;
        if (BuiltinWidgets.TryGetValue(name, out var reads))
        {
            // "features" reads sheetRules.features, or a sheet kind's own features: it needs one of them.
            if (kind != null && string.Equals(name, "features", StringComparison.OrdinalIgnoreCase))
            {
                if (kind.Features.Count == 0) yield return "features shows a sheet kind's feature sources, and this kind has none; it shows nothing.";
            }
            else if (kind != null) yield return $"{name} is a built-in widget over {reads}, which a sheet kind does not have; it shows nothing.";
            yield break;
        }

        if (!GenericWidgets.Contains(name))
        {
            // One the system's DLL supplies: whether its type loads is CompiledSuppliers.Problems' to say. It is handed a sheet
            // kind's view, as the generic widgets read one.
            if (system.Widgets.ContainsKey(name))
            {
                if (kind == null) yield return $"{name} is the system's own widget, which reads a sheet kind's view, and the system has no sheet kinds; it shows nothing.";
                yield break;
            }
            yield return string.IsNullOrWhiteSpace(name) ? "a widget needs a name." : $"there is no widget called {name}.";
            yield break;
        }

        if (kind == null)
        {
            yield return $"{name} reads a sheet kind's keys, and the system has no sheet kinds; it shows nothing.";
            yield break;
        }

        var values = kind.Values.Select(v => v.Key)
            .Concat(system.Attributes.Select(a => a.Key))
            .Concat(kind.Tracks.Select(t => t.Key + SheetModel.MaxSuffix))
            .ToHashSet(StringComparer.Ordinal);
        switch (name.ToLowerInvariant())
        {
            case "values":
                if (widget.Keys.Count == 0) yield return "values needs keys.";
                foreach (var key in widget.Keys)
                {
                    var dot = key.IndexOf('.');
                    var known = values.Contains(key) || (dot > 0 && kind.KeyedLists.Any(l => l.Key == key[..dot]));
                    if (!known) yield return $"values: {key} is not a value of {kind.Kind}.";
                }

                break;
            case "track":
                if (widget.Key is not { Length: > 0 } trackKey) yield return "track needs a key.";
                else if (!kind.Tracks.Any(t => t.Each == null ? Matches(trackKey, t.Key) : trackKey.EndsWith('*') && Wildcard(t.Key).StartsWith(trackKey[..^1], StringComparison.Ordinal)))
                    yield return $"track: {trackKey} is not a track of {kind.Kind}.";
                break;
            case "list":
            case "table":
                var list = (object?)kind.Lists.FirstOrDefault(l => l.Key == widget.Key) ?? kind.KeyedLists.FirstOrDefault(l => l.Key == widget.Key);
                if (widget.Key is not { Length: > 0 }) yield return $"{name} needs a key.";
                else if (list == null) yield return $"{name}: {widget.Key} is not a list of {kind.Kind}.";
                else
                {
                    var columns = list is PickListDefinition pick
                        ? pick.ItemFields.Select(f => f.Key)
                        : ((KeyedListDefinition)list).ItemFields.Select(f => f.Key).Concat(((KeyedListDefinition)list).Derive.Keys);
                    var known = columns.Append("name").ToHashSet(StringComparer.Ordinal);
                    foreach (var column in widget.Columns.Where(c => !known.Contains(c))) yield return $"{name}: {widget.Key} has no field {column}.";
                }

                break;
            case "roll":
                if (widget.Roll is not { Length: > 0 } roll) yield return "roll needs a roll.";
                else if (!system.Rolls.Any(r => r.Key == roll)) yield return $"roll: there is no roll called {roll}.";
                break;
            case "text":
                if (widget.Key is not { Length: > 0 }) yield return "text needs a key.";
                break;
            case "modifiers":
                if (widget.Target is not { Length: > 0 }) yield return "modifiers needs a target.";
                break;
            case "moments":
                // The buttons of the moments that touch the sheet; with none declared there is nothing to press.
                if (system.Moments.Count == 0 && system.Vitals?.Rests.Count is null or 0) yield return "moments: the system declares no moments, so it shows nothing.";
                break;
        }
    }
}
