using System.ClientModel;
using Microsoft.Extensions.AI;
using Codex.Core.AI;

namespace Codex.Tests;

public class TemperatureFallbackChatClientTests
{
    private static readonly ChatMessage[] Hello = { new(ChatRole.User, "hi") };

    [Fact]
    public async Task ModelThatRefusesTemperature_IsAskedAgainWithoutIt()
    {
        var inner = new FakeChat(refuseTemperature: true);
        var client = new TemperatureFallbackChatClient(inner);

        var response = await client.GetResponseAsync(Hello, new ChatOptions { Temperature = 0.7f });

        Assert.Equal("ok", response.Text);
        Assert.Equal(new float?[] { 0.7f, null }, inner.Temperatures);
    }

    [Fact]
    public async Task RefusalIsRemembered_SoLaterRequestsSkipTheFailedAttempt()
    {
        var inner = new FakeChat(refuseTemperature: true);
        var client = new TemperatureFallbackChatClient(inner);

        await client.GetResponseAsync(Hello, new ChatOptions { Temperature = 0.7f });
        await client.GetResponseAsync(Hello, new ChatOptions { Temperature = 0.8f });

        Assert.Equal(new float?[] { 0.7f, null, null }, inner.Temperatures);
    }

    [Fact]
    public async Task ModelThatAcceptsTemperature_GetsItEveryTime()
    {
        var inner = new FakeChat(refuseTemperature: false);
        var client = new TemperatureFallbackChatClient(inner);

        await client.GetResponseAsync(Hello, new ChatOptions { Temperature = 0.7f });
        await client.GetResponseAsync(Hello, new ChatOptions { Temperature = 0.8f });

        Assert.Equal(new float?[] { 0.7f, 0.8f }, inner.Temperatures);
    }

    [Fact]
    public async Task OtherFailures_AreNotSwallowed()
    {
        var inner = new FakeChat(refuseTemperature: false) { Failure = new ClientResultException("HTTP 401 (invalid_api_key) bad key") };
        var client = new TemperatureFallbackChatClient(inner);

        await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync(Hello, new ChatOptions { Temperature = 0.7f }));
        Assert.Single(inner.Temperatures);
    }

    [Fact]
    public async Task CallersOptions_AreNotMutated()
    {
        var inner = new FakeChat(refuseTemperature: true);
        var client = new TemperatureFallbackChatClient(inner);
        var options = new ChatOptions { Temperature = 0.7f, MaxOutputTokens = 50 };

        await client.GetResponseAsync(Hello, options);

        Assert.Equal(0.7f, options.Temperature);
        Assert.Equal(50, inner.LastOptions!.MaxOutputTokens);
    }

    [Fact]
    public async Task Streaming_RefusedBeforeItsFirstUpdate_IsOpenedAgainWithoutTemperature()
    {
        var inner = new FakeChat(refuseTemperature: true);
        var client = new TemperatureFallbackChatClient(inner);

        var updates = await client.GetStreamingResponseAsync(Hello, new ChatOptions { Temperature = 0.8f }).ToListAsync();

        Assert.Equal("ok", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal(new float?[] { 0.8f, null }, inner.Temperatures);
    }

    [Fact]
    public async Task Streaming_OtherFailures_AreNotRetried_AndTheClientStaysUsable()
    {
        var inner = new FakeChat(refuseTemperature: false) { Failure = new ClientResultException("HTTP 401 (invalid_api_key) bad key") };
        var client = new TemperatureFallbackChatClient(inner);

        await Assert.ThrowsAsync<ClientResultException>(async () => await client.GetStreamingResponseAsync(Hello, new ChatOptions { Temperature = 0.8f }).ToListAsync());

        Assert.Single(inner.Temperatures); // no second attempt for something that is not about the temperature
    }

    private sealed class FakeChat(bool refuseTemperature) : IChatClient
    {
        public List<float?> Temperatures { get; } = new();
        public ChatOptions? LastOptions { get; private set; }
        public Exception? Failure { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Temperatures.Add(options?.Temperature);
            LastOptions = options;
            if (Failure is not null)
            {
                throw Failure;
            }

            if (refuseTemperature && options?.Temperature is not null)
            {
                throw new ClientResultException("HTTP 400 (invalid_request_error: invalid_request_error) `temperature` is deprecated for this model.");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
