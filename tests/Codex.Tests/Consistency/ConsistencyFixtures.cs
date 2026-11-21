using Codex.Core;
using Codex.Core.Consistency;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests.Consistency;

/// <summary>Small builders so a test says only what it is about.</summary>
internal static class Fx
{
    public static PageView Page(string id, string title, string body = "Text.", bool party = false, string? actorId = null, string? placeId = null,
        string[]? tags = null, string kind = "general") =>
        new(id, WikiText.Slugify(title), title, body, party, actorId, placeId, tags ?? Array.Empty<string>(), kind);

    public static ActorView Actor(string id, string name = "Aria", ActorRole role = ActorRole.PlayerCharacter, bool retired = false, string? bio = "History.",
        string? notes = null, string? wikiPageId = null, Dictionary<string, object>? sheet = null, string? owner = null, string? blueprint = null, HazardLinkView? hazard = null) =>
        new(id, name, role, retired, bio, notes, wikiPageId, sheet ?? new Dictionary<string, object>(), owner, blueprint, hazard);

    public static WorldSnapshot Snap(IEnumerable<PageView>? pages = null, IEnumerable<ActorView>? actors = null, IEnumerable<PlaceView>? places = null,
        IEnumerable<FactView>? facts = null, SystemDefinition? system = null, IEntryResolver? library = null, string enforcement = "strict",
        CampaignView? campaign = null, IEnumerable<HomebrewView>? homebrew = null) =>
        WorldSnapshot.Create(pages, actors, places, facts, system, library, enforcement, campaign, homebrew);

    /// <summary>A campaign as the checks read it, with what a test does not name left plain.</summary>
    public static CampaignView Campaign(string systemId = "demo", bool installed = true, string[]? members = null, string[]? namedPacks = null, string[]? knownPacks = null,
        string[]? changes = null, string[]? problems = null, BestiaryView[]? bestiary = null) =>
        new("c1", "users/dm", systemId, installed, members ?? new[] { "users/dm", "users/p1" }, namedPacks ?? Array.Empty<string>(), knownPacks ?? Array.Empty<string>(),
            changes ?? Array.Empty<string>(), problems ?? Array.Empty<string>(), bestiary ?? Array.Empty<BestiaryView>());

    public static HomebrewView Brew(string id, string type, string entryId, string name = "Entry", string[]? tags = null, params (string Key, object Value)[] properties) =>
        new(id, type, entryId, name, tags ?? Array.Empty<string>(), properties.ToDictionary(p => p.Key, p => p.Value));

    public static List<Issue> Run(IConsistencyCheck check, WorldSnapshot snapshot) => check.Run(snapshot).OrderBy(i => i, Issue.Order).ToList();

    /// <summary>An issue as plain values, so two runs compare (an Issue holds a list, which a record compares by reference).</summary>
    public static string Flat(Issue i) => string.Join("|", i.Id, i.Check, i.Severity, i.EntityKind, i.EntityId, i.Field, i.Code, string.Join(",", i.Args), i.Message, i.FixHint, i.Bundle, i.FixVia);

    public sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    public static ContentEntry Entry(string type, string id, string name, params (string Key, object Value)[] properties)
    {
        var entry = new ContentEntry { Type = type, Id = id, Name = name };
        foreach (var (key, value) in properties) entry.Properties[key] = value;
        return entry;
    }

    /// <summary>A system with a class and a background, an inventory, and one constraint (the class must be one of two).</summary>
    public static SystemDefinition System() => SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        actorFields:
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: background, label: Background, type: EntityReference, targetEntityType: background }
        entityTypes:
          - { type: class, label: Class }
          - { type: background, label: Background }
          - { type: equipment, label: Equipment }
          - { type: monster, label: Monster, actorBlueprint: true }
          - type: spell
            label: Spell
            fields:
              - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - type: hazard
            label: Hazard
            fields:
              - { key: higherLevels, label: Higher levels, type: Collection }
            hazard:
              higherLevels: higherLevels
        sheetRules:
          enforcement: strict
          inventory: { key: inventory, types: [equipment] }
          constraints:
            - field: class
              where: [{ property: id, in: [fighter, wizard], message: "no such class." }]
        """);
}
