using System.Net;
using System.Text;

namespace Codex.Tests.E2E;

/// <summary>
/// A stand-in for OpenRouter's public <c>/api/v1/models</c>, so the settings page's model picker is tested without the internet. The app
/// under test is pointed at it by <c>Codex:OpenRouterModelsUrl</c> (see <see cref="AppFixture"/>). It serves a few models, one of them a
/// router that must never be offered, and counts what was asked.
/// </summary>
public sealed class FakeOpenRouter : IAsyncDisposable
{
    public const string ChatPro = "acme/chat-pro";
    public const string ChatMini = "acme/chat-mini";
    public const string ChatPlain = "acme/chat-plain";
    public const string Router = "openrouter/auto";
    public const string DrawFlat = "acme/draw-flat";
    public const string DrawTokens = "acme/draw-tokens";

    private const string Text = $$"""
    { "data": [
      { "id": "{{ChatPro}}", "name": "Acme: Chat Pro", "context_length": 200000,
        "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "0.000003", "completion": "0.000015", "input_cache_read": "0.0000003" },
        "supported_parameters": ["tools", "temperature"] },
      { "id": "{{ChatMini}}", "name": "Acme: Chat Mini", "context_length": 128000,
        "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "0.00000015", "completion": "0.0000006", "input_cache_read": "0.000000075" },
        "supported_parameters": ["tools", "temperature"] },
      { "id": "{{ChatPlain}}", "name": "Acme: Plain", "context_length": 32000,
        "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "0.000001", "completion": "0.000002" },
        "supported_parameters": ["temperature"] },
      { "id": "{{Router}}", "name": "Auto Router", "context_length": 2000000,
        "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "-1", "completion": "-1" },
        "supported_parameters": ["tools"] }
    ] }
    """;

    private const string Image = $$"""
    { "data": [
      { "id": "{{DrawFlat}}", "name": "Acme: Draw", "architecture": { "output_modalities": ["image"] },
        "pricing": { "image": "0.04", "image_output": "0.00003" } },
      { "id": "{{DrawTokens}}", "name": "Acme: Draw By Token", "architecture": { "output_modalities": ["image"] },
        "pricing": { "image_output": "0.00003" } }
    ] }
    """;

    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private int _requests;

    public FakeOpenRouter()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>What the app should be told OpenRouter's API address is.</summary>
    public string Url => $"http://127.0.0.1:{Port}/api/v1";

    /// <summary>How many times the list was asked for.</summary>
    public int Requests => Volatile.Read(ref _requests);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Interlocked.Increment(ref _requests);
            var body = context.Request.Url!.AbsolutePath == "/api/v1/models"
                ? Encoding.UTF8.GetBytes(context.Request.Url.Query.Contains("output_modalities=image") ? Image : Text)
                : null;
            context.Response.StatusCode = body == null ? 404 : 200;
            context.Response.ContentType = "application/json";
            if (body != null) await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Close();
        try { await _loop; } catch { /* stopping */ }
    }
}
