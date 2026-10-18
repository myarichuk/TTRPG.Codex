using System;
using System.Threading.Tasks;
using Codex.Core.AI;
using Xunit;
using Microsoft.Extensions.AI;
using OpenAI;
using Testcontainers.Ollama;

namespace Codex.Tests;

public class AIClientFactoryTests : IAsyncLifetime
{
    private OllamaContainer? _ollamaContainer;

    public async Task InitializeAsync()
    {
        try
        {
            _ollamaContainer = new OllamaBuilder()
                .WithImage("ollama/ollama:latest")
                .Build();

            await _ollamaContainer.StartAsync();
        }
        catch (Exception ex)
        {
            // Ignore for tests in sandbox if docker isn't working
            Console.WriteLine("Warning: Failed to start Testcontainer. Tests using real endpoint will fallback to dummy. Error: " + ex.Message);
            _ollamaContainer = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_ollamaContainer != null)
        {
            await _ollamaContainer.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateClient_WithNullOrWhitespaceProvider_ReturnsNull(string? provider)
    {
        // Arrange
        var config = new AIConfiguration { Provider = provider };

        // Act
        var result = AIClientFactory.CreateClient(config);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CreateClient_WithUnknownProvider_ReturnsNull()
    {
        // Arrange
        var config = new AIConfiguration { Provider = "unknown_provider" };

        // Act
        var result = AIClientFactory.CreateClient(config);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null, "model-id")]
    [InlineData("", "model-id")]
    [InlineData("api-key", null)]
    [InlineData("api-key", "")]
    public void CreateClient_OpenAI_WithMissingApiKeyOrModelId_ReturnsNull(string? apiKey, string? modelId)
    {
        // Arrange
        var config = new AIConfiguration
        {
            Provider = "openai",
            ApiKey = apiKey,
            ModelId = modelId
        };

        // Act
        var result = AIClientFactory.CreateClient(config);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CreateClient_OpenAI_WithValidConfig_ReturnsClient()
    {
        // Arrange
        var config = new AIConfiguration
        {
            Provider = "openai",
            ApiKey = "test-api-key",
            ModelId = "test-model-id"
        };

        // Act
        var result = AIClientFactory.CreateClient(config);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void CreateClient_Ollama_WithoutEndpoint_FallsBackToTheLocalDefault()
    {
        Assert.NotNull(AIClientFactory.CreateClient(new AIConfiguration { Provider = "ollama", ModelId = "llama3.1" }));
    }

    [Theory]
    [InlineData("http://localhost", null)]
    [InlineData("http://localhost", "")]
    public void CreateClient_Ollama_WithMissingModelId_ReturnsNull(string? endpoint, string? modelId)
    {
        // Arrange
        var config = new AIConfiguration
        {
            Provider = "ollama",
            Endpoint = endpoint,
            ModelId = modelId
        };

        // Act
        var result = AIClientFactory.CreateClient(config);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CreateClient_Ollama_WithValidConfig_ReturnsClient()
    {
        // Arrange
        var endpoint = _ollamaContainer != null ? _ollamaContainer.GetConnectionString() : "http://localhost:11434";
        var config = new AIConfiguration
        {
            Provider = "ollama",
            Endpoint = endpoint,
            ModelId = "test-model-id"
        };

        // Act
        var result = AIClientFactory.CreateClient(config);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void CreateClient_OpenRouter_UsesPresetEndpointWhenNoneGiven()
    {
        var result = AIClientFactory.CreateClient(new AIConfiguration { Provider = "openrouter", ApiKey = "sk-or-test", ModelId = "openai/gpt-4o-mini" });

        Assert.NotNull(result);
    }

    [Fact]
    public void CreateClient_OpenRouter_WithoutKey_ReturnsNull()
    {
        Assert.Null(AIClientFactory.CreateClient(new AIConfiguration { Provider = "openrouter", ModelId = "openai/gpt-4o-mini" }));
    }

    [Fact]
    public void CreateClient_Custom_NeedsEndpointButNotKey()
    {
        Assert.NotNull(AIClientFactory.CreateClient(new AIConfiguration { Provider = "custom", Endpoint = "http://localhost:1234/v1", ModelId = "local" }));
        Assert.Null(AIClientFactory.CreateClient(new AIConfiguration { Provider = "custom", ModelId = "local" }));
        Assert.Null(AIClientFactory.CreateClient(new AIConfiguration { Provider = "custom", Endpoint = "not a url", ModelId = "local" }));
    }

    [Fact]
    public void Presets_StartWithOpenRouter()
    {
        Assert.Equal("openrouter", AIClientFactory.Presets[0].Kind);
        Assert.Equal(AIClientFactory.OpenRouterEndpoint, AIClientFactory.Presets[0].DefaultEndpoint);
    }
}
