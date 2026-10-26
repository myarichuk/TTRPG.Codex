using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Nothing the app says to an AI model may assume one game. Ashfall (tests/Codex.Tests/Fixtures/ashfall) is a made-up system with
/// no levels or classes, Wounds instead of hit points and "foes" instead of monsters; its prompts must carry none of 5e's words.
/// </summary>
public class SystemNeutralityTests
{
    private static readonly string AshfallRoot = Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", "ashfall");

    // Words of one particular game. "level" and "class" are only banned where the system has no such thing: the level-up prompt is
    // asked about a level on purpose, and only for a system whose sheet has one.
    private static readonly Regex FiveEWords = new(
        @"hit points?|\bHP\b|monster|proficien|fighting style|constitution|d20|armor class|saving throw|challenge rating|5e|D&D|\bSRD\b|" +
        @"\bBandit Captain\b|\bGuard\b|\bPriest\b|\bSpy\b|\blevels?\b|\bclass(es)?\b|high-level",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static SystemDefinition Ashfall() => SystemDefinitionLoader.Load(Path.Combine(AshfallRoot, "system.yaml"));

    private static ComponentRegistry Registry()
    {
        var registry = new ComponentRegistry();
        registry.Register<Codex.Core.Components.ResourcePoolComponent>();
        registry.Register<Codex.Core.Components.AttributesComponent>();
        return registry;
    }

    [Fact]
    public void AshfallSystem_DeclaresItsOwnWords()
    {
        var system = Ashfall();

        Assert.Equal("foe", system.Ai.Creature);
        Assert.Equal("Wounds", system.PrimaryResource!.Key);
        Assert.DoesNotContain(system.ActorFields, f => f.Key is "level" or "class");
    }

    [Fact]
    public async Task FoeBlueprint_FillsTheSystemsPrimaryResource_NotHp()
    {
        var scripts = new Codex.Core.Scripting.ScriptEvaluator(NullLogger<Codex.Core.Scripting.ScriptEvaluator>.Instance);
        var content = new ContentRegistry(scripts);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(content));
        await loader.LoadAndInitializeAsync(AshfallRoot);

        var ferryman = content.GetActor("ashfall:ash-ferryman")!;

        Assert.Equal(5, ferryman.Resources["Wounds"]);
        Assert.Equal(5, ferryman.Resources["Wounds_Max"]);
        Assert.DoesNotContain("HP", ferryman.Resources.Keys);
    }

    [Fact]
    public void CastMemberFromAFoe_ShowsItsWounds_NotZeroOverZero()
    {
        var system = Ashfall();
        var type = system.FindType("foe")!;
        var entry = new ContentEntry { Id = "ash-ferryman", Name = "Ash Ferryman", Type = "foe", SystemId = "ashfall", Properties = { ["wounds"] = 5L, ["grit"] = 4L } };
        var blueprint = YamlContentPackLoader.ToActorBlueprint(entry, type, system.PrimaryResource);
        var actor = new ActorDocument();

        ActorSheetSync.SeedFromBlueprint(actor, blueprint, system, Registry());

        Assert.Equal((5, 5, "Wounds"), ActorSheetSync.PrimaryResource(actor, system));
    }

    [Fact]
    public void Blueprint_WithoutASystem_KeepsTheConventionalHp()
    {
        var type = new ContentTypeDefinition { Type = "monster", ActorBlueprint = true };
        var entry = new ContentEntry { Id = "rat", Name = "Rat", Type = "monster", Properties = { ["hitPoints"] = 3L } };

        var actor = YamlContentPackLoader.ToActorBlueprint(entry, type);

        Assert.Equal(3, actor.Resources["HP"]);
        Assert.Equal(3, actor.Resources["HP_Max"]);
    }

    [Fact]
    public void Prompts_ForALevelessSystem_MentionNoWordsOfAnotherGame()
    {
        var system = Ashfall();
        var primary = system.PrimaryResource!;
        var prompts = new List<(string Name, string Text)>();

        void Add(string name, (string System, string User) p) => prompts.Add((name, p.System + "\n" + p.User));

        Add("create character", AiPrompts.CreateCharacter(new CharacterConceptRequest(
            "a tired ferryman", AiFeatures.KindLabel(ActorKind.Monster, system), "Ashfall",
            system.Attributes.Select(a => new FieldSpec(a.Key, a.Label, "Number", null, a.Min, a.Max)).ToList(),
            new[] { new FieldSpec("trade", "Trade", "Text", null, null, null) },
            primary.Key, primary.MaxKey, HitPointsLabel: primary.Label, HitPointsBasis: system.Ai.PrimaryResourceBasis)));
        Add("rewrite", AiPrompts.Rewrite(new RewriteRequest("Oda", AiFeatures.KindLabel(ActorKind.Monster, system), new[] { "Wounds: 3/5" }, "", "", AiAudience.Dm)));
        Add("describe", AiPrompts.Describe(new DescribeRequest("Oda", "foe", "Ashfall", new[] { "Wounds: 5" }, "", "", AiAudience.Players)));

        foreach (var recipe in WorldRecipes.All)
        {
            var answers = recipe.Questions.Select(q => new WorldAnswer(q.Ask, "surprise me")).ToList();
            var kinds = recipe.Drafts.Select(k => k + ": a draft").ToList();
            var outline = new WorldOutlineRequest(recipe.Prompt, "Ashfall", answers, kinds, new[] { "foe (Foe, a foe)", "relic (Relic)" },
                Array.Empty<string>(), Array.Empty<string>(), CreatureStats: true, Creature: system.Ai.Creature!, CreatureExamples: system.Ai.CreatureExamples);
            Add($"outline {recipe.Key}", AiPrompts.WorldOutline(outline));

            foreach (var kind in recipe.Drafts)
            {
                var expand = new WorldExpandRequest("Oda", "A ferrywoman.", kind == DraftKinds.Homebrew ? "Foe" : null, "Ashfall", Array.Empty<FieldSpec>(), answers,
                    Array.Empty<string>(), Array.Empty<string>(), Kind: kind, Guidance: recipe.Write.GetValueOrDefault(kind));
                Add($"expand {recipe.Key}/{kind}", kind == DraftKinds.Homebrew ? AiPrompts.WorldExpandHomebrew(expand) : AiPrompts.WorldExpandPage(expand));
            }
        }

        foreach (var (name, text) in prompts)
        {
            Assert.True(!FiveEWords.IsMatch(text), $"{name}: \"{FiveEWords.Match(text).Value}\" is a word of another game.\n{text}");
        }
    }

    [Fact]
    public void Prompts_WithNoVocabularyAtAll_StayNeutral()
    {
        var outline = new WorldOutlineRequest("Design a person.", "Anything", Array.Empty<WorldAnswer>(), new[] { "npc: a person" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), CreatureStats: true);
        var (system, _) = AiPrompts.WorldOutline(outline);
        var (levelUp, _) = AiPrompts.LevelUp(new LevelUpRequest("Aria", "Anything", null, null, 2, 3, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null));
        var (_, character) = AiPrompts.CreateCharacter(new CharacterConceptRequest("a thief", "player character", "Anything", Array.Empty<FieldSpec>(), Array.Empty<FieldSpec>(), "Wounds", null));

        Assert.DoesNotContain("Bandit Captain", system);
        Assert.Contains("generic creature", system);
        Assert.DoesNotContain("proficiency", levelUp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fighting style", levelUp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("constitution", character, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hit points", character, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LevelUp_TakesItsChoicesAndNumbersFromTheSystem()
    {
        var (system, _) = AiPrompts.LevelUp(new LevelUpRequest("Aria", "Demo", null, null, 2, 3, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null,
            Choices: new[] { "a path", "a boon" }, SheetChanges: new[] { "stamina" }));

        Assert.Contains("(a path, a boon...)", system);
        Assert.Contains("(stamina...)", system);
    }

    [Fact]
    public void QuestRecipe_AsksHowExperiencedThePartyIs_NotForALevel()
    {
        var party = WorldRecipes.Find("quest")!.Question("party")!;

        Assert.Equal("How experienced is the party, in whatever measure the game uses?", party.Ask);
    }

    [Fact]
    public void Srd5e_StillWordsItsOwnGame()
    {
        var srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

        Assert.Equal("monster", srd.Ai.Creature);
        Assert.Contains("proficiency bonus", srd.Ai.AdvancementChanges);
        Assert.Equal("monster", AiFeatures.KindLabel(ActorKind.Monster, srd));
        Assert.Equal("creature", AiFeatures.KindLabel(ActorKind.Monster));
    }
}
