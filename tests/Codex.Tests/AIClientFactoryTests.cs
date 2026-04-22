using System;
using Codex.Core.AI;
using Xunit;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Codex.Tests;

public class AIClientFactoryTests
{
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

    [Theory]
    [InlineData(null, "model-id")]
    [InlineData("", "model-id")]
    [InlineData("http://localhost", null)]
    [InlineData("http://localhost", "")]
    public void CreateClient_Ollama_WithMissingEndpointOrModelId_ReturnsNull(string? endpoint, string? modelId)
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
        var config = new AIConfiguration
        {
            Provider = "ollama",
            Endpoint = "http://localhost:11434",
            ModelId = "test-model-id"
        };

        // Act
        var result = AIClientFactory.CreateClient(config);

        // Assert
        Assert.NotNull(result);
    }
}
