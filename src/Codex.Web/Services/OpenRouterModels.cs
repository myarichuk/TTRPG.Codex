using System.Globalization;
using System.Text.Json;
using Codex.Core.AI;

namespace Codex.Web.Services;

/// <summary>A model OpenRouter lists, trimmed to what the settings page shows. Prices are what OpenRouter charges, in US dollars, in the
/// units the price fields use; a price OpenRouter does not give is null, never zero.</summary>
public sealed record OpenRouterModel(string Id, string Name, int? ContextLength, decimal? InputPerMTok, decimal? OutputPerMTok,
    decimal? CachedInputPerMTok, decimal? PerImage, bool Tools);

/// <summary>
/// OpenRouter's public model list (<c>/api/v1/models</c>, which needs no key), kept for an hour so the settings page can offer
/// the real, current ids instead of a list that goes stale. It always asks OpenRouter's own address, never one a user typed, so
/// a provider's base URL cannot point the server somewhere else. (The server's own setting <c>Codex:OpenRouterModelsUrl</c> can,
/// for a mirror, or for tests that must not reach the internet; it is configuration, not something a user types.) When OpenRouter
/// cannot be reached the page still works: the last list is kept, or there is none and the model id is typed by hand.
/// <para>
/// Left out: OpenRouter's own routers (<c>openrouter/auto</c>, <c>openrouter/free</c>, …) and anything with a negative price, which
/// is OpenRouter's way of saying "priced per request by whichever model it picks": there is no model to name and no price to show.
/// </para>
/// </summary>
public sealed class OpenRouterModels(IHttpClientFactory http, TimeProvider? clock = null, ILogger<OpenRouterModels>? log = null, string? baseUrl = null)
{
    private readonly string endpoint = string.IsNullOrWhiteSpace(baseUrl) ? AIClientFactory.OpenRouterEndpoint : baseUrl.Trim().TrimEnd('/');

    public static readonly TimeSpan Fresh = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const long MaxBytes = 16 * 1024 * 1024;

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Slot text = new();
    private readonly Slot images = new();

    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyList<OpenRouterModel>? Items;
        public DateTimeOffset At;
        public DateTimeOffset? FailedAt;
    }

    /// <summary>The chat models (those that answer in text only), or the image models. Null when there is no list and none is kept.</summary>
    public async Task<IReadOnlyList<OpenRouterModel>?> ListAsync(bool imageModels, CancellationToken ct = default)
    {
        var slot = imageModels ? images : text;
        if (Current(slot) is { } cached) return cached;

        await slot.Gate.WaitAsync(ct);
        try
        {
            if (Current(slot) is { } again) return again;

            var now = time.GetUtcNow();
            if (slot.FailedAt is { } failed && now - failed < RetryAfterFailure) return slot.Items;

            try
            {
                slot.Items = await FetchAsync(imageModels, ct);
                slot.At = time.GetUtcNow();
                slot.FailedAt = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                // Not fatal: the page offers what it has, and the id can always be typed.
                log?.LogInformation("OpenRouter's model list could not be read: {Reason}", ex.Message);
                slot.FailedAt = time.GetUtcNow();
            }

            return slot.Items;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private IReadOnlyList<OpenRouterModel>? Current(Slot slot) =>
        slot.Items is { } items && time.GetUtcNow() - slot.At < Fresh ? items : null;

    private async Task<IReadOnlyList<OpenRouterModel>> FetchAsync(bool imageModels, CancellationToken ct)
    {
        using var client = http.CreateClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);

        var url = endpoint + "/models" + (imageModels ? "?output_modalities=image" : string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TTRPG.Codex");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("the list is larger than expected");

        await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: limit.Token);
        return Parse(doc.RootElement, imageModels);
    }

    /// <summary>Reads OpenRouter's reply. A model that cannot be read is skipped, never the whole list.</summary>
    internal static IReadOnlyList<OpenRouterModel> Parse(JsonElement root, bool imageModels)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("no model list in the reply");
        }

        var result = new List<OpenRouterModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Str(item, "id") is not { Length: > 0 } id) continue;
            if (id.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase) || !seen.Add(id)) continue;
            if (!Outputs(item, imageModels)) continue;

            var prices = item.TryGetProperty("pricing", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            if (prices.ValueKind == JsonValueKind.Object && prices.EnumerateObject().Any(x => Number(x.Value) < 0)) continue;

            var tools = item.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array
                && sp.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "tools");
            var context = item.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number && cl.TryGetInt32(out var c) && c > 0 ? c : (int?)null;

            result.Add(new OpenRouterModel(id, Str(item, "name") is { Length: > 0 } name ? name : id, context,
                imageModels ? null : PerMTok(prices, "prompt"), imageModels ? null : PerMTok(prices, "completion"),
                imageModels ? null : PerMTok(prices, "input_cache_read"),
                imageModels ? Number(Get(prices, "image")) : null, tools));
        }

        return result;
    }

    /// <summary>A chat model answers in text and nothing else (so not the ones that also speak or make video); an image model
    /// is anything that can draw.</summary>
    private static bool Outputs(JsonElement model, bool imageModels)
    {
        if (!model.TryGetProperty("architecture", out var arch) || arch.ValueKind != JsonValueKind.Object
            || !arch.TryGetProperty("output_modalities", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var kinds = outputs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
        return imageModels ? kinds.Contains("image") : kinds.Count == 1 && kinds[0] == "text";
    }

    private static JsonElement Get(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement obj, string name) => Get(obj, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>OpenRouter gives prices as strings of dollars; null when absent or not a number.</summary>
    private static decimal? Number(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
        : v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n
        : null;

    /// <summary>Dollars per token as OpenRouter lists it, as dollars per million tokens as the price fields take it.</summary>
    private static decimal? PerMTok(JsonElement prices, string name) =>
        Number(Get(prices, name)) is { } perToken ? Math.Round(perToken * 1_000_000m, 6).Normalize() : null;
}

internal static class DecimalExtensions
{
    /// <summary>The same number without trailing zeros (0.150000 → 0.15).</summary>
    public static decimal Normalize(this decimal value) => value / 1.0000000000000000000000000000m;
}
