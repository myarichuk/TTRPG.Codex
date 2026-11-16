using System.Net;
using System.Text;
using Codex.Core.AI;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

/// <summary>Google Gemini through its OpenAI-compatible API: the service the settings page offers, and where its requests really go.</summary>
public sealed class GeminiPresetTests
{
    private const string Reply = """{"id":"c1","object":"chat.completion","created":1,"model":"gemini-4-hydrogen","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(Uri Uri, string? Auth)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void Preset_IsGoogles_AndNeedsAKey()
    {
        var preset = AIClientFactory.Preset("gemini")!;

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/", preset.DefaultEndpoint);
        Assert.True(preset.RequiresKey);
        Assert.Contains(preset.DefaultModel, AiModelCatalog.Bundled().For(AiModelPurpose.Text, "gemini").Select(o => o.Id));
        // Not an Anthropic address, and not mistaken for one by the kind detector.
        Assert.Equal("gemini", AIClientFactory.DetectKind("gemini", preset.DefaultEndpoint, "AIza-test"));
    }

    [Fact]
    public void CreateClient_WithoutAKey_IsNull()
    {
        Assert.Null(AIClientFactory.CreateClient(new AIConfiguration { Provider = "gemini", ModelId = "gemini-4-hydrogen" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai")]
    public async Task Chat_GoesToTheCompatibleEndpointsChatCompletions_WhateverTheSlash(string? typedEndpoint)
    {
        var recorder = new Recorder();
        var client = AIClientFactory.CreateClient(new AIConfiguration
        {
            Provider = "gemini",
            Endpoint = typedEndpoint,
            ApiKey = "test-key",
            ModelId = "gemini-4-hydrogen",
            Http = new HttpClient(recorder)
        })!;

        await client.GetResponseAsync("hello");

        var call = Assert.Single(recorder.Calls);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", call.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("Bearer test-key", call.Auth);
    }
}
