using Codex.Core;
using Codex.Core.Abilities;
using Codex.Core.Components;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The generated full-rules packs (packs/srd51-full, packs/pf2e-remastered) are living
/// fixtures like the sample pack: if the YAML rots or an importer regresses, these fail.
/// Covers 6.1 - counts, known-good values, executable-spell end-to-end runs, and the
/// mechanics-only license guard (no prose may leak into generated entries).
/// </summary>
public class RulesPackContentTests : IDisposable
{
    private readonly CodexWorld _world = new();
    private readonly ScriptEvaluator _evaluator = new(NullLogger<ScriptEvaluator>.Instance);

    public void Dispose() => _world.Dispose();

    private static string FindPackDir(string packId)
    {
        var start = Path.GetDirectoryName(typeof(RulesPackContentTests).Assembly.Location) ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packs", packId);
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"packs/{packId} not found above {start}. " +
            "Generate it with scripts/import_srd51.py / scripts/import_pf2e.py.");
    }

    private async Task<ContentRegistry> LoadPackAsync(string packId)
    {
        var registry = new ContentRegistry(_evaluator);
        await new YamlContentPackLoader(registry).LoadPackAsync(FindPackDir(packId));
        return registry;
    }

    private static DefaultEcs.Entity EntityWithPool(CodexWorld world, string pool, int amount)
    {
        var entity = world.CreateEntity();
        var pools = new ResourcePoolComponent();
        pools.Set(pool, amount);
        entity.Set(pools);
        return entity;
    }

    // YamlDotNet resolves nested mappings under Dictionary<string, object> as
    // Dictionary<object, object> and sequences as List<object>; normalize both.
    private static IReadOnlyDictionary<string, object>? AsDict(object? node) => node switch
    {
        Dictionary<string, object> d => d,
        Dictionary<object, object> d => d.ToDictionary(kv => kv.Key.ToString()!, kv => kv.Value),
        IReadOnlyDictionary<string, object> d => d,
        _ => null,
    };

    private static List<object>? AsList(object? node) => node switch
    {
        List<object> l => l,
        System.Collections.IEnumerable e => e.Cast<object>().ToList(),
        _ => null,
    };

    private static string? Str(object? node) => node?.ToString();

    // ------------------------------------------------------------------ SRD 5.1

    [Fact]
    public async Task Srd51Full_LoadsExpectedCounts()
    {
        var registry = await LoadPackAsync("srd51-full");

        var spells = registry.GetAbilitiesBySystem("DnD5e").ToList();
        Assert.Equal(319, spells.Count);
        Assert.All(spells, s => Assert.Equal("spell", Str(s.Metadata?["kind"])));

        var monsters = registry.GetActorsBySystem("DnD5e").ToList();
        Assert.Equal(334, monsters.Count);
    }

    [Fact]
    public async Task Srd51Full_Fireball_KnownGoodValues()
    {
        var registry = await LoadPackAsync("srd51-full");
        var fireball = registry.GetAbility("DnD5e:fireball");
        Assert.NotNull(fireball);

        Assert.Contains(fireball.Requires!, r => r.Type == "ResourceRequirement" && Str(r.Params!["pool"]) == "Slots3");
        Assert.Contains(fireball.Costs!, c => c.Type == "ResourceCost" && Str(c.Params!["pool"]) == "Slots3");
        var damage = Assert.Single(fireball.Effects!, e => e.Type == "Damage");
        Assert.Equal("8d6", Str(damage.Params!["dice"]));
        Assert.Equal("fire", Str(damage.Params!["damageType"]));
        Assert.Equal("dex", Str(damage.Params!["saveAbility"]));
        Assert.Equal("half", Str(damage.Params!["saveSuccess"]));
        Assert.False(damage.Params!.ContainsKey("saveDc")); // DCs are caster-derived, never imported

        var meta = fireball.Metadata!;
        Assert.Equal(3, Convert.ToInt32(meta["level"]));
        Assert.Equal("evocation", Str(meta["school"]));
        Assert.Contains("wizard", AsList(meta["classes"])!.Select(Str));

        var save = AsDict(meta["save"])!;
        Assert.Equal("dex", Str(save["ability"]));
        Assert.Equal("half", Str(save["success"]));

        var area = AsDict(meta["area"])!;
        Assert.Equal("sphere", Str(area["type"]));
        Assert.Equal(20, Convert.ToInt32(area["size"]));

        var byType = AsDict(meta["damageAtSlotLevel"])!;
        var fire = AsDict(byType["fire"])!;
        Assert.Equal("8d6", Str(fire["3"]));
        Assert.Equal("14d6", Str(fire["9"]));
    }

    [Fact]
    public async Task Srd51Full_CureWounds_HealsBaseDice()
    {
        var registry = await LoadPackAsync("srd51-full");
        var cure = registry.GetAbility("DnD5e:cure-wounds");
        Assert.NotNull(cure);

        var heal = Assert.Single(cure.Effects!, e => e.Type == "Heal");
        Assert.Equal("1d8", Str(heal.Params!["dice"]));

        // The full formula (with spellcasting modifier) stays in metadata.
        var table = AsDict(cure.Metadata!["healAtSlotLevel"])!;
        Assert.Equal("1d8 + MOD", Str(table["1"]));
    }

    [Fact]
    public async Task Srd51Full_Aid_UsesFlatAmount()
    {
        // Aid heals a flat 5 with no dice - those ride the amount path, not dice.
        var registry = await LoadPackAsync("srd51-full");
        var aid = registry.GetAbility("DnD5e:aid");
        Assert.NotNull(aid);

        var heal = Assert.Single(aid.Effects!, e => e.Type == "Heal");
        Assert.Equal(5, Convert.ToInt32(heal.Params!["amount"]));
        Assert.False(heal.Params!.ContainsKey("dice"));
    }

    [Fact]
    public async Task Srd51Full_IceStorm_ExecutesBothDamageTypes()
    {
        var registry = await LoadPackAsync("srd51-full");
        var storm = registry.GetAbility("DnD5e:ice-storm");
        Assert.NotNull(storm);

        var damages = storm.Effects!.Where(e => e.Type == "Damage").ToList();
        Assert.Equal(2, damages.Count);
        Assert.Contains(damages, d => Str(d.Params!["damageType"]) == "bludgeoning");
        Assert.Contains(damages, d => Str(d.Params!["damageType"]) == "cold");

        var caster = EntityWithPool(_world, "Slots4", 1);
        var target = EntityWithPool(_world, "HP", 100);
        var context = new AbilityExecutionContext(_world, caster, target,
            new Dictionary<string, object> { ["SaveDc"] = 25 });
        var result = new AbilityExecutor(_evaluator).Execute(storm, context);

        Assert.True(result.Success);
        var remaining = target.Get<ResourcePoolComponent>().Get("HP");
        Assert.InRange(remaining, 100 - (16 + 24), 100 - 2); // 2d8 + 4d6, no save at DC 25
    }

    [Fact]
    public async Task Srd51Full_Goblin_KnownGoodValues()
    {
        var registry = await LoadPackAsync("srd51-full");
        var goblin = registry.GetActor("DnD5e:goblin");
        Assert.NotNull(goblin);

        Assert.Equal(7, goblin.Resources["HP"]);
        Assert.Equal(15, Convert.ToInt32(goblin.Properties["armorClass"]));
        Assert.Equal(14, Convert.ToInt32(goblin.Properties["dexterity"]));
        Assert.Equal(0.25, Convert.ToDouble(goblin.Properties["challengeRating"]));
        Assert.Equal("2d6", Str(goblin.Properties["hitDice"]));

        var skills = AsDict(goblin.Properties["skills"])!;
        Assert.Equal(6, Convert.ToInt32(skills["stealth"]));
        var senses = AsDict(goblin.Properties["senses"])!;
        Assert.Equal(60, Convert.ToInt32(senses["darkvision"]));

        var actions = AsList(goblin.Properties["actions"])!;
        var scimitar = AsDict(actions.First(a => Str(AsDict(a)!["name"]) == "Scimitar"))!;
        Assert.Equal(4, Convert.ToInt32(scimitar["attackBonus"]));
        var damage = AsDict(AsList(scimitar["damage"])![0])!;
        Assert.Equal("slashing", Str(damage["type"]));
        Assert.Equal("1d6+2", Str(damage["dice"]));
    }

    [Fact]
    public async Task Srd51Full_Fireball_ExecutesEndToEnd()
    {
        var registry = await LoadPackAsync("srd51-full");
        var fireball = registry.GetAbility("DnD5e:fireball");
        Assert.NotNull(fireball);

        var caster = EntityWithPool(_world, "Slots3", 1);
        var target = EntityWithPool(_world, "HP", 50);
        // DC 25 with +0: the d20 can never reach it, so this pins the full-damage path.
        var context = new AbilityExecutionContext(_world, caster, target,
            new Dictionary<string, object> { ["SaveDc"] = 25 });
        var result = new AbilityExecutor(_evaluator).Execute(fireball, context);

        Assert.True(result.Success);
        Assert.Equal(0, caster.Get<ResourcePoolComponent>().Get("Slots3"));
        var remaining = target.Get<ResourcePoolComponent>().Get("HP");
        Assert.InRange(remaining, 50 - 48, 50 - 8);
    }

    [Fact]
    public async Task Srd51Full_Fireball_HalvedOnSuccessfulSave()
    {
        var registry = await LoadPackAsync("srd51-full");
        var fireball = registry.GetAbility("DnD5e:fireball");
        Assert.NotNull(fireball);

        var caster = EntityWithPool(_world, "Slots3", 1);
        var target = EntityWithPool(_world, "HP", 50);
        // DC 1 with +0: the d20 always reaches it, so this pins the half-damage path.
        var context = new AbilityExecutionContext(_world, caster, target,
            new Dictionary<string, object> { ["SaveDc"] = 1 });
        var result = new AbilityExecutor(_evaluator).Execute(fireball, context);

        Assert.True(result.Success);
        var remaining = target.Get<ResourcePoolComponent>().Get("HP");
        Assert.InRange(remaining, 50 - 24, 50 - 4);
    }

    [Fact]
    public async Task Srd51Full_ClassEquipment_HasNoUnmappedOptions()
    {
        // Every equipment option must be renderable by the creation wizard: bundles stay
        // atomic, category grants resolve, and no "?" placeholder may leak through (the
        // cleric's crossbow bundle + holy symbol once broke the wizard's Equipment step).
        var registry = await LoadPackAsync("srd51-full");
        foreach (var cls in registry.GetRulesEntries("DnD5e", "class")
                     .Where(c => !c.Metadata.ContainsKey("subclassOf")))
        {
            foreach (var raw in AsList(cls.Properties.GetValueOrDefault("startingEquipmentOptions")))
            {
                var group = AsDict(raw);
                Assert.NotNull(group);
                if (group!.TryGetValue("options", out var nested))
                {
                    foreach (var option in AsList(nested))
                    {
                        AssertOptionRenderable(AsDict(option), cls.Id);
                    }
                }
                else
                {
                    AssertOptionRenderable(group, cls.Id);
                }
            }
        }

        var cleric = registry.GetRulesEntry("DnD5e:cleric")!;
        var groups = AsList(cleric.Properties["startingEquipmentOptions"]).Select(AsDict).ToList();
        Assert.Contains(groups, g => g!.TryGetValue("options", out var nested)
            && AsList(nested).Any(o => AsDict(o)!.ContainsKey("bundle")));
        Assert.Contains(groups, g => g!.ContainsKey("category"));
    }

    private static void AssertOptionRenderable(IReadOnlyDictionary<string, object>? option, string classId)
    {
        Assert.NotNull(option);
        Assert.True(option!.ContainsKey("bundle") || option.ContainsKey("name")
            || option.ContainsKey("category") || option.ContainsKey("options"),
            $"Class {classId} has an unrenderable equipment option.");
        Assert.NotEqual("?", Str(option.GetValueOrDefault("name")));
        if (option.TryGetValue("options", out var nested))
        {
            foreach (var child in AsList(nested))
            {
                AssertOptionRenderable(AsDict(child), classId);
            }
        }
    }

    [Fact]
    public async Task Srd51Full_AcidArrow_RequiresAttackRoll()
    {
        var registry = await LoadPackAsync("srd51-full");
        var arrow = registry.GetAbility("DnD5e:acid-arrow");
        Assert.NotNull(arrow);
        Assert.Contains(arrow.Requires!, r => r.Type == "Attack");
    }

    [Fact]
    public async Task Srd51Full_RulesEntries_LoadExpectedCounts()
    {
        var registry = await LoadPackAsync("srd51-full");

        var classes = registry.GetRulesEntries("DnD5e", "class").ToList();
        Assert.Equal(24, classes.Count); // 12 classes + 12 subclasses
        Assert.Equal(12, classes.Count(c => c.Metadata.TryGetValue("subclassOf", out _)));
        Assert.Equal(13, registry.GetRulesEntries("DnD5e", "ancestry").Count()); // 9 + 4 subraces
        Assert.Equal(237, registry.GetRulesEntries("DnD5e", "equipment").Count());
    }

    [Fact]
    public async Task Srd51Full_Warlock_KnownGoodValues()
    {
        var registry = await LoadPackAsync("srd51-full");
        var warlock = registry.GetRulesEntry("DnD5e:warlock");
        Assert.NotNull(warlock);
        Assert.Equal("class", warlock.Kind);

        Assert.Equal(8, Convert.ToInt32(warlock.Properties["hitDie"]));
        Assert.Contains("cha", AsList(warlock.Properties["savingThrows"])!.Select(Str));
        Assert.Contains("wis", AsList(warlock.Properties["savingThrows"])!.Select(Str));
        var casting = AsDict(warlock.Properties["spellcasting"])!;
        Assert.Equal("cha", Str(casting["ability"]));
        Assert.Contains("fiend", AsList(warlock.Properties["subclasses"])!.Select(Str));
        Assert.Contains("chill-touch", AsList(warlock.Properties["spellList"])!.Select(Str));
        Assert.DoesNotContain("fireball", AsList(warlock.Properties["spellList"])!.Select(Str));
    }

    [Fact]
    public async Task Srd51Full_Fiend_PatronGrantsExpandedSpells()
    {
        var registry = await LoadPackAsync("srd51-full");
        var fiend = registry.GetRulesEntry("DnD5e:fiend");
        Assert.NotNull(fiend);
        Assert.Equal("warlock", Str(fiend.Metadata["subclassOf"]));

        var spells = AsList(fiend.Properties["spells"])!
            .Select(s => (Spell: Str(AsDict(s)!["spell"]), Level: Convert.ToInt32(AsDict(s)!["level"])))
            .ToList();
        Assert.Contains(spells, s => s.Spell == "burning-hands" && s.Level == 1);
        Assert.Contains(spells, s => s.Spell == "fireball" && s.Level == 5);
    }

    [Fact]
    public async Task Srd51Full_ElfAndLongsword_KnownGoodValues()
    {
        var registry = await LoadPackAsync("srd51-full");

        var elf = registry.GetRulesEntry("DnD5e:elf");
        Assert.NotNull(elf);
        Assert.Equal("ancestry", elf.Kind);
        Assert.Equal(30, Convert.ToInt32(elf.Properties["speed"]));
        Assert.Equal(2, Convert.ToInt32(AsDict(elf.Properties["abilityBonuses"])!["dex"]));
        Assert.Contains("Common", AsList(elf.Properties["languages"])!.Select(Str));
        Assert.Contains("Darkvision", AsList(elf.Properties["traits"])!.Select(Str));

        var sword = registry.GetRulesEntry("DnD5e:longsword");
        Assert.NotNull(sword);
        Assert.Equal("equipment", sword.Kind);
        var damage = AsDict(sword.Properties["damage"])!;
        Assert.Equal("1d8", Str(damage["dice"]));
        Assert.Equal("slashing", Str(damage["type"]));
        Assert.Contains("versatile", AsList(sword.Properties["properties"])!.Select(Str));
    }

    [Fact]
    public async Task Srd51Full_AllEntries_MechanicsOnly()
    {
        var registry = await LoadPackAsync("srd51-full");
        var banned = new HashSet<string> { "desc", "higher_level", "summary", "text" };

        foreach (var spell in registry.GetAbilitiesBySystem("DnD5e"))
        {
            Assert.Null(spell.Description);
            Assert.DoesNotContain(spell.Metadata?.Keys ?? Enumerable.Empty<string>(), k => banned.Contains(k));
            var level = Convert.ToInt32(spell.Metadata!["level"]);
            Assert.InRange(level, 0, 9);
        }

        foreach (var monster in registry.GetActorsBySystem("DnD5e"))
        {
            Assert.Null(monster.Description);
            Assert.DoesNotContain(monster.Properties.Keys, k => banned.Contains(k));
            Assert.Equal("monster", Str(monster.Metadata["kind"]));
        }

        foreach (var entry in registry.GetRulesEntries("DnD5e"))
        {
            Assert.Null(entry.Description);
            Assert.DoesNotContain(entry.Properties.Keys, k => banned.Contains(k));
        }
    }

    // -------------------------------------------------------------- PF2e ORC

    [Fact]
    public async Task Pf2eRemastered_LoadsExpectedCounts()
    {
        var registry = await LoadPackAsync("pf2e-remastered");

        var abilities = registry.GetAbilitiesBySystem("Pf2e").ToList();
        Assert.Equal(373, abilities.Count(a => Str(a.Metadata?["kind"]) == "spell"));
        Assert.Equal(1807, abilities.Count(a => Str(a.Metadata?["kind"]) == "feat"));
    }

    [Fact]
    public async Task Pf2eRemastered_Fireball_KnownGoodValues()
    {
        var registry = await LoadPackAsync("pf2e-remastered");
        var fireball = registry.GetAbility("Pf2e:fireball");
        Assert.NotNull(fireball);

        var meta = fireball.Metadata!;
        Assert.Equal(3, Convert.ToInt32(meta["rank"]));
        Assert.Contains("arcane", AsList(meta["traditions"])!.Select(Str));
        Assert.Contains("primal", AsList(meta["traditions"])!.Select(Str));
        Assert.Contains("wizard", AsList(meta["classes"])!.Select(Str));
        Assert.Equal("Two Actions", Str(meta["actions"]));
        Assert.Equal(500, Convert.ToInt32(meta["range"]));

        var save = AsDict(meta["savingThrow"])!;
        Assert.Equal("reflex", Str(save["type"]));
        Assert.True(Convert.ToBoolean(save["basic"]));

        var area = AsDict(meta["area"])!;
        Assert.Contains("burst", AsList(area["types"])!.Select(Str));
        Assert.Contains(20, AsList(area["values"])!.Select(Convert.ToInt32));

        // Reference-only for now: no damage formulas in AoN's structured fields.
        Assert.True(fireball.Effects == null || fireball.Effects.Count == 0);
    }

    [Fact]
    public async Task Pf2eRemastered_RulesEntries_LoadExpectedCounts()
    {
        var registry = await LoadPackAsync("pf2e-remastered");

        Assert.Equal(16, registry.GetRulesEntries("Pf2e", "class").Count());
        Assert.Equal(10, registry.GetRulesEntries("Pf2e", "ancestry").Count());
        Assert.Equal(100, registry.GetRulesEntries("Pf2e", "heritage").Count());
        Assert.Equal(56, registry.GetRulesEntries("Pf2e", "background").Count());
        Assert.Equal(7, registry.GetRulesEntries("Pf2e", "patron").Count());
        Assert.Equal(551, registry.GetRulesEntries("Pf2e", "equipment").Count());
    }

    [Fact]
    public async Task Pf2eRemastered_FighterDwarfPatron_KnownGoodValues()
    {
        var registry = await LoadPackAsync("pf2e-remastered");

        var fighter = registry.GetRulesEntry("Pf2e:fighter");
        Assert.NotNull(fighter);
        Assert.Equal(10, Convert.ToInt32(fighter.Properties["hitPoints"]));
        Assert.Equal("Trained", Str(fighter.Properties["willProficiency"]));
        Assert.Contains("Strength", AsList(fighter.Properties["keyAttributes"])!.Select(Str));

        var dwarf = registry.GetRulesEntry("Pf2e:dwarf");
        Assert.NotNull(dwarf);
        Assert.Equal(10, Convert.ToInt32(dwarf.Properties["hitPoints"]));
        Assert.Equal("Medium", Str(dwarf.Properties["size"]));
        Assert.Contains("Constitution", AsList(dwarf.Properties["abilityBoosts"])!.Select(Str));
        Assert.Contains("Charisma", AsList(dwarf.Properties["abilityFlaw"])!.Select(Str));

        var patron = registry.GetRulesEntry("Pf2e:faith_s_flamekeeper");
        Assert.NotNull(patron);
        Assert.Equal("patron", patron.Kind);
        Assert.Contains("Divine", AsList(patron.Properties["spellList"])!.Select(Str));
        Assert.Contains("Religion", AsList(patron.Properties["patronSkill"])!.Select(Str));
        Assert.Contains("Stoke the Heart", AsList(patron.Properties["hexCantrip"])!.Select(Str));
    }

    [Fact]
    public async Task Pf2eRemastered_ClinchStrike_HasPrereqAndTrigger()
    {
        var registry = await LoadPackAsync("pf2e-remastered");
        var feat = registry.GetAbility("Pf2e:clinch_strike");
        Assert.NotNull(feat);

        var meta = feat.Metadata!;
        Assert.Equal(6, Convert.ToInt32(meta["level"]));
        Assert.Equal("Wrestler Dedication", Str(meta["prerequisites"]));
        Assert.Contains("Escapes", Str(meta["trigger"]));
        Assert.Equal("Reaction", Str(meta["actions"]));
    }

    [Fact]
    public async Task Pf2eRemastered_AllEntries_MechanicsOnly()
    {
        var registry = await LoadPackAsync("pf2e-remastered");
        var banned = new HashSet<string> { "desc", "text", "summary", "markdown" };

        foreach (var ability in registry.GetAbilitiesBySystem("Pf2e"))
        {
            Assert.Null(ability.Description);
            Assert.DoesNotContain(ability.Metadata?.Keys ?? Enumerable.Empty<string>(), k => banned.Contains(k));
            var kind = Str(ability.Metadata!["kind"]);
            Assert.True(kind == "spell" || kind == "feat");

            if (kind == "spell")
            {
                Assert.InRange(Convert.ToInt32(ability.Metadata!["rank"]), 0, 10);
                Assert.True(ability.Effects == null || ability.Effects.Count == 0);
            }
            else if (ability.Metadata!.TryGetValue("level", out var level) && level != null)
            {
                Assert.InRange(Convert.ToInt32(level), 1, 20);
            }
        }

        foreach (var entry in registry.GetRulesEntries("Pf2e"))
        {
            Assert.Null(entry.Description);
            Assert.DoesNotContain(entry.Properties.Keys, k => banned.Contains(k));
        }
    }
}
