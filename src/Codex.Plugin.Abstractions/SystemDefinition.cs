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

    /// <summary>Character-sheet rules (attribute generation, pick constraints, lists, derived
    /// fields). Null means "no rules": any value goes, which is what a loose homebrew wants.</summary>
    public SheetRules? SheetRules { get; set; }

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
