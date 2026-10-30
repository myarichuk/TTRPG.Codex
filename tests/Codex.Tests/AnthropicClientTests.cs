using System.Net;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Codex.Core.AI;

namespace Codex.Tests;

public class AnthropicClientTests
{
    [Theory]
    [InlineData("anthropic", null, "k", true)]
    [InlineData("custom", "https://api.anthropic.com/v1", "k", true)]
    [InlineData("openai", "https://api.anthropic.com", "k", true)]
    [InlineData("custom", "https://eu.api.anthropic.com/v1", "k", true)]
    [InlineData("custom", null, "sk-ant-api03-abc", true)]
    [InlineData("custom", "", " sk-ant-api03-abc ", true)]
    [InlineData("custom", "https://gateway.example/v1", "sk-ant-api03-abc", false)] // a gateway speaks the OpenAI protocol, whatever key it takes
    [InlineData("custom", "https://notanthropic.com/v1", "k", false)]
    [InlineData("openrouter", "https://openrouter.ai/api/v1", "sk-or-abc", false)]
    [InlineData("openai", null, "sk-abc", false)]
    [InlineData("ollama", "http://localhost:11434/v1", null, false)]
    public void IsAnthropic_GoesByPresetAddressOrKey(string kind, string? endpoint, string? key, bool expected) =>
        Assert.Equal(expected, AIClientFactory.IsAnthropic(kind, endpoint, key));

    [Fact]
    public void DetectKind_PicksAnthropic_ElseKeepsWhatWasChosen()
    {
        Assert.Equal("anthropic", AIClientFactory.DetectKind("custom", "https://api.anthropic.com/v1", "k"));
        Assert.Equal("custom", AIClientFactory.DetectKind("custom", "https://gateway.example/v1", "sk-ant-x"));
    }

    [Theory]
    [InlineData("custom", "https://api.anthropic.com/v1")]
    [InlineData("custom", "https://api.anthropic.com/v1/")]
    [InlineData("anthropic", "https://api.anthropic.com")]
    [InlineData("anthropic", "")]
    public void CreateClient_ForAnthropic_UsesTheAnthropicClient_WhateverTheSpelling(string provider, string endpoint)
    {
        var client = AIClientFactory.CreateClient(new AIConfiguration { Provider = provider, Endpoint = endpoint, ApiKey = "sk-ant-test", ModelId = "claude-haiku-5-5" });

        Assert.NotNull(client);
        Assert.Equal("anthropic", client!.GetService<ChatClientMetadata>()?.ProviderName, ignoreCase: true);
    }

    [Fact]
    public void CreateClient_ForOpenAi_StillUsesTheOpenAiClient()
    {
        var client = AIClientFactory.CreateClient(new AIConfiguration { Provider = "openai", ApiKey = "sk-test", ModelId = "gpt-4o-mini" });

        Assert.Equal("openai", client!.GetService<ChatClientMetadata>()?.ProviderName, ignoreCase: true);
    }

    [Fact]
    public void CreateClient_ForAnthropic_NeedsAKey() =>
        Assert.Null(AIClientFactory.CreateClient(new AIConfiguration { Provider = "anthropic", ModelId = "claude-haiku-5-5" }));

    [Fact]
    public void AnthropicsOwnRefusalOfTemperature_IsRecognised_OtherBadRequestsAreNot()
    {
        // The API's error body rides on the exception apart from its message.
        const string refusal = """{"type":"error","error":{"type":"invalid_request_error","message":"`temperature` is deprecated for this model."}}""";
        const string tooLarge = """{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: too large"}}""";
        var refused = new AnthropicBadRequestException(new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest)) { StatusCode = HttpStatusCode.BadRequest, ResponseBody = refusal };
        var other = new AnthropicBadRequestException(new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest)) { StatusCode = HttpStatusCode.BadRequest, ResponseBody = tooLarge };

        Assert.True(TemperatureFallbackChatClient.RefusesTemperature(refused));
        Assert.False(TemperatureFallbackChatClient.RefusesTemperature(other));
        Assert.False(TemperatureFallbackChatClient.RefusesTemperature(new InvalidOperationException("temperature")));
    }

    [Fact]
    public void AiFailure_ReadsTheWholeChain_NotTheRetryWrapper()
    {
        var attempt = new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout of 0:01:40.");
        var wrapped = new System.ClientModel.ClientResultException("Retry failed after 4 tries.", null, new AggregateException(attempt, attempt, attempt, attempt));

        Assert.Equal(AiFailureKind.Timeout, AiFailure.From(wrapped).Kind);
    }

    [Fact]
    public void AiFailure_SaysWhatTheProviderSaid_NotTheSdksWrappingOfIt()
    {
        const string body = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";
        var auth = new AnthropicUnauthorizedException(new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized)) { StatusCode = HttpStatusCode.Unauthorized, ResponseBody = body };
        var bad = new AnthropicBadRequestException(new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest)) { StatusCode = HttpStatusCode.BadRequest, ResponseBody = """{"error":{"message":"max_tokens: too large"}}""" };

        Assert.Equal(AiFailureKind.Auth, AiFailure.From(auth).Kind);
        Assert.Equal(new AiFailure(AiFailureKind.Other, "max_tokens: too large"), AiFailure.From(bad));
        Assert.Equal(AiFailureKind.Network, AiFailure.From(new HttpRequestException("Name or service not known")).Kind);
        Assert.Equal(AiFailureKind.Server, AiFailure.From(new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable)).Kind);
    }
}
