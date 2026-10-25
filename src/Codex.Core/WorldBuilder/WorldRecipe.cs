using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.Core.WorldBuilder;

/// <summary>The kinds of draft a recipe may ask the model for, and what each becomes when saved.</summary>
public static class DraftKinds
{
    /// <summary>A Library entry of one of the system's content types, saved as an unpublished homebrew entry.</summary>
    public const string Homebrew = "homebrew";

    /// <summary>A world wiki page, saved DM-only.</summary>
    public const string Wiki = "wiki";

    /// <summary>A person (a leader, a member, a contact), saved as a DM-only wiki page and, when the DM ticks it, also as a
    /// hidden cast member.</summary>
    public const string Npc = "npc";

    /// <summary>A secret or rumour the party can uncover, saved DM-only in Lore; its name is the fact itself.</summary>
    public const string Fact = "fact";

    /// <summary>A place (a district, a landmark, an inn), saved as a DM-only wiki page and, when the DM ticks it, also as a
    /// hidden place on the World page.</summary>
    public const string Place = "place";

    public static readonly IReadOnlyList<string> All = new[] { Homebrew, Wiki, Npc, Fact, Place };

    /// <summary>Kinds saved as wiki pages, which other drafts' text can link to.</summary>
    public static bool IsPage(string kind) => kind is Wiki or Npc or Place;
}

/// <summary>The kinds of question a recipe can ask.</summary>
public static class QuestionTypes
{
    public const string Text = "text";
    public const string LongText = "longText";
    public const string Choice = "choice";

    /// <summary>A content type of the campaign's system (spell, creature...); the options come from the system, not the recipe.</summary>
    public const string ContentType = "contentType";

    /// <summary>A yes/no question; "true" or "false".</summary>
    public const string Toggle = "toggle";

    public static readonly IReadOnlyList<string> All = new[] { Text, LongText, Choice, ContentType, Toggle };
}

/// <summary>
/// One guided world-building wizard (see docs/WORLD_BUILDER.md): the questions it asks, the drafts it may propose and the
/// brief the model is given. Recipes are data; their labels live in the <c>WorldBuilder</c> resx area, keyed by
/// <see cref="Key"/> and the question and option keys, so the wizard reads in every language while the prompt stays English.
/// </summary>
public sealed class WorldRecipe
{
    public string Key { get; set; } = string.Empty;
    public string Icon { get; set; } = "sparkles";

    /// <summary>Kinds of draft this recipe may return (<see cref="DraftKinds"/>); anything else in a reply is dropped.</summary>
    public List<string> Drafts { get; set; } = new();

    public List<RecipeQuestion> Questions { get; set; } = new();

    /// <summary>The English brief for the outline call: what to design and how many drafts of each kind.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Extra English instructions for writing a draft of a kind in full, keyed by kind (a quest page's fixed
    /// sections, say); a kind without one is written the usual way.</summary>
    public Dictionary<string, string> Write { get; set; } = new();

    public RecipeQuestion? Question(string key) =>
        Questions.FirstOrDefault(q => string.Equals(q.Key, key, StringComparison.OrdinalIgnoreCase));
}

public sealed class RecipeQuestion
{
    public string Key { get; set; } = string.Empty;

    /// <summary>One of <see cref="QuestionTypes"/>.</summary>
    public string Type { get; set; } = QuestionTypes.Text;

    /// <summary>The question as the model reads it, in English ("What is it for?").</summary>
    public string Ask { get; set; } = string.Empty;

    /// <summary>Option keys of a <see cref="QuestionTypes.Choice"/>, in English; the model reads the key, the DM its label.</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>Offers "Surprise me": the question may be left for the model to decide.</summary>
    public bool Surprise { get; set; }

    /// <summary>Must be answered (or left to the model, when <see cref="Surprise"/>) before the outline.</summary>
    public bool Required { get; set; }

    /// <summary>The answer the wizard starts with: an option key, "true"/"false", or text.</summary>
    public string? Default { get; set; }
}

/// <summary>Reads recipes from YAML and checks them; the shipped ones are embedded in this assembly.</summary>
public static class WorldRecipes
{
    private static readonly Regex KeyPattern = new("^[a-z][a-zA-Z0-9]*$", RegexOptions.Compiled);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly Lazy<IReadOnlyList<WorldRecipe>> Shipped = new(LoadShipped);

    /// <summary>The recipes that ship with the app, in a stable order.</summary>
    public static IReadOnlyList<WorldRecipe> All => Shipped.Value;

    public static WorldRecipe? Find(string key) =>
        All.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses and validates one recipe. Throws <see cref="InvalidDataException"/> naming what is wrong.</summary>
    public static WorldRecipe Parse(string yaml, string source = "recipe")
    {
        WorldRecipe recipe;
        try
        {
            recipe = Deserializer.Deserialize<WorldRecipe>(yaml) ?? throw new InvalidDataException($"{source}: empty recipe.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException($"{source}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        Validate(recipe, source);
        return recipe;
    }

    private static void Validate(WorldRecipe recipe, string source)
    {
        var problems = new List<string>();
        if (!KeyPattern.IsMatch(recipe.Key)) problems.Add($"key \"{recipe.Key}\" must be camelCase letters and digits");
        if (string.IsNullOrWhiteSpace(recipe.Prompt)) problems.Add("prompt is empty");
        if (recipe.Drafts.Count == 0) problems.Add("drafts lists no kinds");
        problems.AddRange(recipe.Drafts.Where(k => !DraftKinds.All.Contains(k)).Select(k => $"unknown draft kind \"{k}\""));
        if (recipe.Questions.Count == 0) problems.Add("no questions");
        problems.AddRange(recipe.Write.Keys.Where(k => !recipe.Drafts.Contains(k)).Select(k => $"write: \"{k}\" is not one of the recipe's drafts"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in recipe.Questions)
        {
            var at = $"question \"{q.Key}\"";
            if (!KeyPattern.IsMatch(q.Key)) problems.Add($"{at}: key must be camelCase letters and digits");
            if (!seen.Add(q.Key)) problems.Add($"{at}: duplicate key");
            if (!QuestionTypes.All.Contains(q.Type)) problems.Add($"{at}: unknown type \"{q.Type}\"");
            if (string.IsNullOrWhiteSpace(q.Ask)) problems.Add($"{at}: ask is empty");
            if (q.Type == QuestionTypes.Choice)
            {
                if (q.Options.Count < 2) problems.Add($"{at}: a choice needs at least two options");
                problems.AddRange(q.Options.Where(o => !KeyPattern.IsMatch(o)).Select(o => $"{at}: option \"{o}\" must be camelCase letters and digits"));
                if (q.Default != null && !q.Options.Contains(q.Default)) problems.Add($"{at}: default \"{q.Default}\" is not an option");
            }
            else if (q.Options.Count > 0)
            {
                problems.Add($"{at}: only a choice has options");
            }

            if (q.Type == QuestionTypes.Toggle && q.Default is not (null or "true" or "false")) problems.Add($"{at}: a toggle's default is true or false");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException($"{source}: " + string.Join("; ", problems) + ".");
        }
    }

    private static IReadOnlyList<WorldRecipe> LoadShipped()
    {
        var assembly = typeof(WorldRecipes).Assembly;
        const string prefix = "Codex.Core.WorldBuilder.Recipes.";
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".yaml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd(), name[prefix.Length..]);
            })
            .ToList();
    }
}
