using Codex.Core.AI;

namespace Codex.Tests;

/// <summary>The model ids the AI settings page suggests: the bundled list, and the file an admin can merge over it.</summary>
public sealed class AiModelCatalogTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ai-models-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(string json)
    {
        var path = Path.Combine(dir, AiModelCatalog.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static string[] Ids(AiModelCatalog catalog, AiModelPurpose purpose, string kind) =>
        [.. catalog.For(purpose, kind).Select(o => o.Id)];

    [Fact]
    public void Bundled_OffersAnthropicsModels_WithTheirNames()
    {
        var anthropic = AiModelCatalog.Bundled().For(AiModelPurpose.Text, "anthropic");

        Assert.Contains(anthropic, o => o.Id == "claude-sonnet-5-5" && o.Label == "Claude Sonnet 5.5");
        Assert.Contains(anthropic, o => o.Id == AIClientFactory.Preset("anthropic")!.DefaultModel);
    }

    [Fact]
    public void Bundled_OnlyNamesServicesThePageOffers_AndEachDefaultIsSuggested()
    {
        // A catalog entry for a kind no preset has would never be shown, and a preset whose own default is not in its list
        // would suggest everything but the model it fills in.
        var catalog = AiModelCatalog.Bundled();
        foreach (var (purpose, kinds, defaults) in new[]
        {
            (AiModelPurpose.Text, AIClientFactory.Presets.Select(p => p.Kind), AIClientFactory.Presets.ToDictionary(p => p.Kind, p => p.DefaultModel)),
            (AiModelPurpose.Image, AiImageGenerator.Presets.Select(p => p.Kind), AiImageGenerator.Presets.ToDictionary(p => p.Kind, p => p.DefaultModel))
        })
        {
            foreach (var kind in kinds)
            {
                var ids = Ids(catalog, purpose, kind);
                if (ids.Length == 0) continue;
                Assert.True(ids.Contains(defaults[kind]), $"{purpose}/{kind}: the default model '{defaults[kind]}' is not among its suggestions");
            }
        }
    }

    [Fact]
    public void For_IsCaseInsensitive_AndEmptyForAServiceWithNoList()
    {
        var catalog = AiModelCatalog.Bundled();

        Assert.NotEmpty(catalog.For(AiModelPurpose.Text, " Anthropic "));
        Assert.Empty(catalog.For(AiModelPurpose.Text, "custom"));
        Assert.Empty(catalog.For(AiModelPurpose.Text, null));
    }

    [Fact]
    public void Load_WithNoFile_IsTheBundledList()
    {
        var catalog = AiModelCatalog.Load(Path.Combine(dir, AiModelCatalog.FileName));

        Assert.Null(catalog.Problem);
        Assert.Equal(Ids(AiModelCatalog.Bundled(), AiModelPurpose.Text, "anthropic"), Ids(catalog, AiModelPurpose.Text, "anthropic"));
    }

    [Fact]
    public void Load_AFileReplacesTheServicesItNames_AndKeepsTheRest()
    {
        var path = Write("""{ "text": { "anthropic": [ "my-claude", { "id": "other", "label": "Other" } ], "ollama": [ "llama3.1" ] } }""");

        var catalog = AiModelCatalog.Load(path);

        Assert.Null(catalog.Problem);
        Assert.Equal(["my-claude", "other"], Ids(catalog, AiModelPurpose.Text, "anthropic"));
        Assert.Equal("Other", catalog.For(AiModelPurpose.Text, "anthropic")[1].Label);
        Assert.Equal(["llama3.1"], Ids(catalog, AiModelPurpose.Text, "ollama"));
    }

    [Fact]
    public void Load_AnEmptyList_SwitchesThatServicesSuggestionsOff()
    {
        var catalog = AiModelCatalog.Load(Write("""{ "text": { "anthropic": [] } }"""));

        Assert.Null(catalog.Problem);
        Assert.Empty(catalog.For(AiModelPurpose.Text, "anthropic"));
    }

    [Fact]
    public void Load_TextAndImageListsAreSeparate()
    {
        var catalog = AiModelCatalog.Load(Write("""{ "image": { "openai": [ "img-1" ] } }"""));

        Assert.Equal(["img-1"], Ids(catalog, AiModelPurpose.Image, "openai"));
        Assert.DoesNotContain("img-1", Ids(catalog, AiModelPurpose.Text, "openai"));
    }

    [Fact]
    public void Load_ToleratesCommentsTrailingCommasBlanksAndRepeats()
    {
        // It is edited by hand: a comment, a trailing comma, an empty id and a repeated one must not cost anyone their list.
        var catalog = AiModelCatalog.Load(Write("""
            {
              // my models
              "text": { "groq": [ "a", "", "  ", "b", "a", ], },
            }
            """));

        Assert.Null(catalog.Problem);
        Assert.Equal(["a", "b"], Ids(catalog, AiModelPurpose.Text, "groq"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "speech": { "openai": [ "x" ] } }""")]
    [InlineData("""{ "text": { "openai": "gpt" } }""")]
    [InlineData("""{ "text": { "openai": [ 5 ] } }""")]
    public void Load_AFileThatCannotBeUsed_FallsBackToTheBundledListAndSaysWhy(string json)
    {
        var path = Write(json);

        var catalog = AiModelCatalog.Load(path);

        Assert.NotNull(catalog.Problem);
        Assert.Contains(path, catalog.Problem);
        Assert.Equal(Ids(AiModelCatalog.Bundled(), AiModelPurpose.Text, "anthropic"), Ids(catalog, AiModelPurpose.Text, "anthropic"));
    }

    [Fact]
    public void Load_AFileWithOneBadService_UsesNoneOfIt()
    {
        // All or nothing: half of an edited file would leave a list nobody wrote.
        var catalog = AiModelCatalog.Load(Write("""{ "text": { "ollama": [ "llama3.1" ], "openai": "oops" } }"""));

        Assert.NotNull(catalog.Problem);
        Assert.Empty(catalog.For(AiModelPurpose.Text, "ollama"));
    }
}
