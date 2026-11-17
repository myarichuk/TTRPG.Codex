using System;
using System.Collections.Generic;
using System.Linq;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// What a campaign stores about its packs. <paramref name="Legacy"/> is the older full list (non-empty means exactly those packs);
/// <paramref name="On"/> and <paramref name="Off"/> are the choice relative to the defaults: packs that are off by default and were
/// switched on, and packs that are on by default and were switched off. <paramref name="All"/> is the vault's stand-in campaign, which
/// uses every pack.
/// </summary>
public sealed record PackChoice(
    IReadOnlyCollection<string> Legacy,
    IReadOnlyCollection<string> On,
    IReadOnlyCollection<string> Off,
    bool All = false)
{
    /// <summary>A campaign that never chose: the defaults.</summary>
    public static readonly PackChoice Defaults = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// The one rule for which packs a campaign uses. A pack is on when the campaign says so, otherwise when its manifest does
/// (<see cref="PackManifest.ActiveByDefault"/>, true unless it opts out). Because the choice is stored relative to the defaults, a pack
/// added later is on for every campaign unless that campaign switched it off, and a campaign that turned an optional pack on is not
/// frozen out of packs that ship afterwards. Pickers, search and the Library list are narrowed to this set; a reference a campaign
/// already holds resolves whatever the set says. Translation packs are not toggles and are left out throughout.
/// </summary>
public static class PackSelection
{
    private static IEnumerable<PackManifest> Toggles(IEnumerable<PackManifest> systemPacks) => systemPacks.Where(p => !p.IsTranslation);

    /// <summary>The ids to narrow content to, or null when nothing is narrowed (every pack of the system is on). An empty set means no
    /// pack is on and nothing is offered.</summary>
    public static IReadOnlySet<string>? Resolve(PackChoice choice, IEnumerable<PackManifest> systemPacks)
    {
        var packs = Toggles(systemPacks).ToList();
        var on = Selected(choice, packs);
        return packs.All(p => on.Contains(p.Id)) ? null : on;
    }

    /// <summary>The packs that are on, as Settings shows its toggles.</summary>
    public static HashSet<string> Selected(PackChoice choice, IEnumerable<PackManifest> systemPacks)
    {
        var packs = Toggles(systemPacks).ToList();
        if (choice.All)
        {
            return packs.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (choice.Legacy.Count > 0)
        {
            return choice.Legacy.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return packs
            .Where(p => p.ActiveByDefault ? !choice.Off.Contains(p.Id, StringComparer.OrdinalIgnoreCase) : choice.On.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the pack is on for a campaign that stores <paramref name="choice"/>.</summary>
    public static bool IsActive(PackChoice choice, IEnumerable<PackManifest> systemPacks, string packId) =>
        Selected(choice, systemPacks).Contains(packId);

    /// <summary>What to store for the packs the user left switched on: only the difference from the defaults, and no legacy list.</summary>
    public static PackChoice FromSelection(IReadOnlyCollection<string> selected, IEnumerable<PackManifest> systemPacks)
    {
        var chosen = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packs = Toggles(systemPacks).ToList();
        return new PackChoice(
            Array.Empty<string>(),
            packs.Where(p => !p.ActiveByDefault && chosen.Contains(p.Id)).Select(p => p.Id).ToList(),
            packs.Where(p => p.ActiveByDefault && !chosen.Contains(p.Id)).Select(p => p.Id).ToList());
    }
}
