using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Codex.Core.AI;

/// <summary>One generated picture, ready to store as an attachment.</summary>
public sealed record GeneratedImage(byte[] Bytes, string ContentType);

/// <summary>The frame a picture is drawn for: a creature or entry (tall), a place (wide), or a character's avatar (square).</summary>
public enum ArtShape
{
    Tall,
    Wide,
    Square
}

/// <summary>An image model the settings page can set up with one click. <see cref="OpenRouterImages"/>
/// marks OpenRouter, whose image API is <c>POST /images</c> with an aspect ratio, rather than the
/// OpenAI-style <c>/images/generations</c>.</summary>
public sealed record AiImagePreset(string Kind, string Label, string DefaultEndpoint, string DefaultModel, bool RequiresKey, bool OpenRouterImages, string Hint);

/// <summary>
/// Text-to-image for art slots. Kept apart from the chat client on purpose: the best writing model
/// rarely draws, and the two speak different protocols. OpenAI and compatible servers use
/// <c>/images/generations</c>; OpenRouter has its own <c>/images</c> endpoint that serves every image model it lists.
/// </summary>
public static class AiImageGenerator
{
    public static readonly IReadOnlyList<AiImagePreset> Presets = new[]
    {
        new AiImagePreset("openai", "OpenAI Images", AIClientFactory.OpenAIEndpoint, "gpt-image-1", true, false, "OpenAI's image models (gpt-image-1, dall-e-3) with your own key."),
        new AiImagePreset("openrouter", "OpenRouter", AIClientFactory.OpenRouterEndpoint, "google/gemini-2.5-flash-image", true, true, "Any image model OpenRouter lists, e.g. google/gemini-2.5-flash-image or qwen/qwen-image-2.1-turbo. Uses your OpenRouter key."),
        new AiImagePreset("custom", "Custom (OpenAI images API)", string.Empty, string.Empty, false, false, "Any server that implements /v1/images/generations, such as LocalAI or a ComfyUI bridge.")
    };

    public static AiImagePreset? Preset(string? kind) =>
        Presets.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when the configuration is missing an endpoint, a model or a required key.</summary>
    public static string? Problem(AIConfiguration config)
    {
        var preset = Preset(config.Provider);
        var endpoint = Endpoint(config);
        if (string.IsNullOrWhiteSpace(config.ModelId)) return "The image provider needs a model.";
        if (endpoint == null) return "The image provider needs a full base URL.";
        if (preset is { RequiresKey: true } && string.IsNullOrWhiteSpace(config.ApiKey)) return "The image provider needs an API key.";
        return null;
    }

    public static async Task<GeneratedImage> GenerateAsync(AIConfiguration config, string prompt, ArtShape shape, HttpClient http, CancellationToken ct = default)
    {
        if (Problem(config) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var endpoint = Endpoint(config)!;
        var key = string.IsNullOrWhiteSpace(config.ApiKey) ? "none" : config.ApiKey.Trim();
        return IsOpenRouter(config.Provider, endpoint)
            ? await ViaOpenRouterAsync(endpoint, key, config.ModelId!.Trim(), prompt, shape, http, ct)
            : await ViaImagesApiAsync(endpoint, key, config.ModelId!.Trim(), prompt, shape, http, ct);
    }

    /// <summary>OpenRouter by preset, or by address: a model saved under another service but pointed at openrouter.ai
    /// would otherwise call <c>/images/generations</c>, which OpenRouter answers with a bare 404.</summary>
    public static bool IsOpenRouter(string? kind, Uri endpoint) =>
        Preset(kind)?.OpenRouterImages == true
        || endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
        || endpoint.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase);

    private static Uri? Endpoint(AIConfiguration config)
    {
        var raw = string.IsNullOrWhiteSpace(config.Endpoint) ? Preset(config.Provider)?.DefaultEndpoint : config.Endpoint.Trim();
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
    }

#pragma warning disable MEAI001 // IImageGenerator is marked experimental; it is the adapter the OpenAI package ships.
    private static async Task<GeneratedImage> ViaImagesApiAsync(Uri endpoint, string key, string model, string prompt, ArtShape shape, HttpClient http, CancellationToken ct)
    {
        var client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = endpoint });
        var generator = client.GetImageClient(model).AsIImageGenerator();
        // No response format: gpt-image models reject it and always return base64; dall-e models
        // return a URL, which is downloaded below. Sizes differ per family; others get their default.
        var response = await generator.GenerateImagesAsync(prompt, new ImageGenerationOptions
        {
            Count = 1,
            ImageSize = SizeFor(model, shape)
        }, ct);

        foreach (var content in response.Contents)
        {
            switch (content)
            {
                case DataContent data when data.Data.Length > 0:
                    return new GeneratedImage(data.Data.ToArray(), SniffContentType(data.Data.Span) ?? data.MediaType);
                case UriContent link:
                    return await DownloadAsync(link.Uri, http, ct);
            }
        }

        throw new InvalidOperationException("The image model returned no picture.");
    }
#pragma warning restore MEAI001

    /// <summary>A size of the right shape that the model family accepts, or null to let the server choose.</summary>
    public static System.Drawing.Size? SizeFor(string model, ArtShape shape)
    {
        if (model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase))
        {
            return shape switch
            {
                ArtShape.Wide => new System.Drawing.Size(1536, 1024),
                ArtShape.Square => new System.Drawing.Size(1024, 1024),
                _ => new System.Drawing.Size(1024, 1536)
            };
        }

        if (model.StartsWith("dall-e-3", StringComparison.OrdinalIgnoreCase))
        {
            return shape switch
            {
                ArtShape.Wide => new System.Drawing.Size(1792, 1024),
                ArtShape.Square => new System.Drawing.Size(1024, 1024),
                _ => new System.Drawing.Size(1024, 1792)
            };
        }

        return null;
    }

    /// <summary>OpenRouter's aspect ratio for a shape; every image model it lists takes these three.</summary>
    public static string AspectRatio(ArtShape shape) => shape switch
    {
        ArtShape.Wide => "3:2",
        ArtShape.Square => "1:1",
        _ => "2:3"
    };

    private static async Task<GeneratedImage> ViaOpenRouterAsync(Uri endpoint, string key, string model, string prompt, ArtShape shape, HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenRouterImagesUri(endpoint));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/myarichuk/TTRPG.Codex");
        request.Headers.TryAddWithoutValidation("X-Title", "TTRPG.Codex");
        request.Content = JsonContent.Create(new { model, prompt, aspect_ratio = AspectRatio(shape) });

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The image service answered {(int)response.StatusCode}: {ErrorMessage(body)}");
        }

        return ParseImagesResponse(body) ?? throw new InvalidOperationException("The image model returned no picture.");
    }

    /// <summary><c>{base}/images</c>. A base URL that already names an endpoint (OpenRouter's own error message
    /// suggests <c>/api/v1/images</c>) is cut back to the API root first, so it doesn't become <c>/images/images</c>.</summary>
    public static Uri OpenRouterImagesUri(Uri endpoint)
    {
        var root = endpoint.ToString().TrimEnd('/');
        foreach (var suffix in new[] { "/images/generations", "/chat/completions", "/images" })
        {
            if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^suffix.Length];
                break;
            }
        }

        return new Uri(root + "/images");
    }

    /// <summary>The first picture in an OpenRouter <c>/images</c> reply: <c>data[0].b64_json</c>, typed by its bytes
    /// (or its <c>media_type</c> when they aren't recognised). Null when there is none.</summary>
    public static GeneratedImage? ParseImagesResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64) || b64.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(b64.GetString())) continue;
            var bytes = Convert.FromBase64String(b64.GetString()!);
            var declared = item.TryGetProperty("media_type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            return new GeneratedImage(bytes, SniffContentType(bytes) ?? declared ?? "image/png");
        }

        return null;
    }

    /// <summary>The <c>error.message</c> of a JSON error reply, so the user reads the reason rather than the raw body.</summary>
    public static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return Trim(message.GetString()!);
            }
        }
        catch (JsonException)
        {
        }

        return Trim(body);
    }

    /// <summary>The real image type from the first bytes, so a mislabelled reply still lands as an allowed type.</summary>
    public static string? SniffContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual("GIF8"u8)) return "image/gif";
        return null;
    }

    private static async Task<GeneratedImage> DownloadAsync(Uri uri, HttpClient http, CancellationToken ct)
    {
        var bytes = await http.GetByteArrayAsync(uri, ct);
        return new GeneratedImage(bytes, SniffContentType(bytes) ?? "image/png");
    }

    private static string Trim(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
