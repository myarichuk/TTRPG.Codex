using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Codex.Core.AI;

/// <summary>One generated picture, ready to store as an attachment.</summary>
public sealed record GeneratedImage(byte[] Bytes, string ContentType);

/// <summary>An image model the settings page can set up with one click. <see cref="ChatModalities"/>
/// marks services that return pictures from chat completions (OpenRouter) rather than from the
/// OpenAI images endpoint.</summary>
public sealed record AiImagePreset(string Kind, string Label, string DefaultEndpoint, string DefaultModel, bool RequiresKey, bool ChatModalities, string Hint);

/// <summary>
/// Text-to-image for art slots. Kept apart from the chat client on purpose: the best writing model
/// rarely draws, and the two speak different protocols. OpenAI and compatible servers use
/// <c>/images/generations</c>; OpenRouter returns images inside a chat completion.
/// </summary>
public static class AiImageGenerator
{
    public static readonly IReadOnlyList<AiImagePreset> Presets = new[]
    {
        new AiImagePreset("openai", "OpenAI Images", AIClientFactory.OpenAIEndpoint, "gpt-image-1", true, false, "OpenAI's image models (gpt-image-1, dall-e-3) with your own key."),
        new AiImagePreset("openrouter", "OpenRouter", AIClientFactory.OpenRouterEndpoint, "google/gemini-2.5-flash-image", true, true, "Image-capable models on OpenRouter, e.g. google/gemini-2.5-flash-image. Uses your OpenRouter key."),
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

    public static async Task<GeneratedImage> GenerateAsync(AIConfiguration config, string prompt, bool landscape, HttpClient http, CancellationToken ct = default)
    {
        if (Problem(config) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var endpoint = Endpoint(config)!;
        var key = string.IsNullOrWhiteSpace(config.ApiKey) ? "none" : config.ApiKey.Trim();
        return Preset(config.Provider)?.ChatModalities == true
            ? await ViaChatAsync(endpoint, key, config.ModelId!.Trim(), prompt, landscape, http, ct)
            : await ViaImagesApiAsync(endpoint, key, config.ModelId!.Trim(), prompt, landscape, http, ct);
    }

    private static Uri? Endpoint(AIConfiguration config)
    {
        var raw = string.IsNullOrWhiteSpace(config.Endpoint) ? Preset(config.Provider)?.DefaultEndpoint : config.Endpoint.Trim();
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
    }

#pragma warning disable MEAI001 // IImageGenerator is marked experimental; it is the adapter the OpenAI package ships.
    private static async Task<GeneratedImage> ViaImagesApiAsync(Uri endpoint, string key, string model, string prompt, bool landscape, HttpClient http, CancellationToken ct)
    {
        var client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = endpoint });
        var generator = client.GetImageClient(model).AsIImageGenerator();
        // No response format: gpt-image models reject it and always return base64; dall-e models
        // return a URL, which is downloaded below. Sizes differ per family; others get their default.
        var response = await generator.GenerateImagesAsync(prompt, new ImageGenerationOptions
        {
            Count = 1,
            ImageSize = SizeFor(model, landscape)
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

    /// <summary>A tall or wide size the model family accepts, or null to let the server choose.</summary>
    public static System.Drawing.Size? SizeFor(string model, bool landscape)
    {
        if (model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase))
        {
            return landscape ? new System.Drawing.Size(1536, 1024) : new System.Drawing.Size(1024, 1536);
        }

        if (model.StartsWith("dall-e-3", StringComparison.OrdinalIgnoreCase))
        {
            return landscape ? new System.Drawing.Size(1792, 1024) : new System.Drawing.Size(1024, 1792);
        }

        return null;
    }

    private static async Task<GeneratedImage> ViaChatAsync(Uri endpoint, string key, string model, string prompt, bool landscape, HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.ToString().TrimEnd('/') + "/chat/completions"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/myarichuk/TTRPG.Codex");
        request.Headers.TryAddWithoutValidation("X-Title", "TTRPG.Codex");
        request.Content = JsonContent.Create(new
        {
            model,
            modalities = new[] { "image", "text" },
            image_config = new { aspect_ratio = landscape ? "3:2" : "2:3" },
            messages = new[] { new { role = "user", content = prompt } }
        });

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The image service answered {(int)response.StatusCode}: {Trim(body)}");
        }

        var url = ParseChatImageUrl(body) ?? throw new InvalidOperationException("The model answered without a picture. Pick a model that can output images.");
        return DecodeDataUrl(url) ?? await DownloadAsync(new Uri(url), http, ct);
    }

    /// <summary>The first image URL (usually a data: URL) in an OpenRouter-style chat completion:
    /// <c>choices[0].message.images[0].image_url.url</c>.</summary>
    public static string? ParseChatImageUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return null;
        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || !message.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) continue;
            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("image_url", out var imageUrl) && imageUrl.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    return url.GetString();
                }
            }
        }

        return null;
    }

    /// <summary>Decodes <c>data:image/png;base64,...</c>; null for anything that is not a base64 data URL.</summary>
    public static GeneratedImage? DecodeDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = url.IndexOf(',');
        if (comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        var bytes = Convert.FromBase64String(url[(comma + 1)..]);
        var declared = url[5..url.IndexOf(';')];
        return new GeneratedImage(bytes, SniffContentType(bytes) ?? declared);
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
