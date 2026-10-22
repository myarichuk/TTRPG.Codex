using Codex.Core.AI;

namespace Codex.Tests;

public class AiImageGeneratorTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };

    [Fact]
    public void ParseImagesResponse_DecodesB64Json_AndTypesItByItsBytes()
    {
        // Declared as JPEG, but the bytes are a PNG: the stored type must match the bytes.
        var json = $$"""{"created":1,"data":[{"b64_json":"{{Convert.ToBase64String(Png)}}","media_type":"image/jpeg"}]}""";

        var image = AiImageGenerator.ParseImagesResponse(json);

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(Png, image.Bytes);
    }

    [Fact]
    public void ParseImagesResponse_NoPicture_IsNull()
    {
        Assert.Null(AiImageGenerator.ParseImagesResponse("""{"created":1,"data":[]}"""));
        Assert.Null(AiImageGenerator.ParseImagesResponse("""{"error":{"message":"nope"}}"""));
    }

    [Fact]
    public void ErrorMessage_ReadsTheReason_NotTheRawBody()
    {
        Assert.Equal("Model not found", AiImageGenerator.ErrorMessage("""{"error":{"message":"Model not found","code":404},"user_id":"user_123"}"""));
        Assert.Equal("Bad gateway", AiImageGenerator.ErrorMessage("Bad gateway"));
    }

    [Theory]
    [InlineData(ArtShape.Tall, "2:3")]
    [InlineData(ArtShape.Wide, "3:2")]
    [InlineData(ArtShape.Square, "1:1")]
    public async Task OpenRouter_PostsToTheImagesEndpoint_WithTheShapesAspectRatio(ArtShape shape, string ratio)
    {
        var handler = new RecordingHandler($$"""{"created":1,"data":[{"b64_json":"{{Convert.ToBase64String(Png)}}"}]}""");
        var config = new AIConfiguration { Provider = "openrouter", ModelId = "qwen/qwen-image-2.1-turbo", ApiKey = "sk-test" };

        var image = await AiImageGenerator.GenerateAsync(config, "a toad", shape, new HttpClient(handler));

        Assert.Equal("https://openrouter.ai/api/v1/images", handler.Uri?.ToString());
        Assert.Equal("Bearer sk-test", handler.Authorization);
        using var body = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal("qwen/qwen-image-2.1-turbo", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("a toad", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(ratio, body.RootElement.GetProperty("aspect_ratio").GetString());
        Assert.Equal(Png, image.Bytes);
    }

    [Fact]
    public async Task OpenRouterAddress_UsesItsImagesEndpoint_EvenUnderAnotherService()
    {
        var handler = new RecordingHandler($$"""{"created":1,"data":[{"b64_json":"{{Convert.ToBase64String(Png)}}"}]}""");
        var config = new AIConfiguration { Provider = "openai", Endpoint = "https://openrouter.ai/api/v1", ModelId = "qwen/qwen-image-2.1-turbo", ApiKey = "sk-test" };

        await AiImageGenerator.GenerateAsync(config, "a toad", ArtShape.Tall, new HttpClient(handler));

        Assert.Equal("https://openrouter.ai/api/v1/images", handler.Uri?.ToString());
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("https://openrouter.ai/api/v1/")]
    [InlineData("https://openrouter.ai/api/v1/images")]
    [InlineData("https://openrouter.ai/api/v1/images/generations")]
    [InlineData("https://openrouter.ai/api/v1/chat/completions")]
    public void OpenRouterImagesUri_CutsAnEndpointBackToTheApiRoot(string baseUrl)
    {
        Assert.Equal("https://openrouter.ai/api/v1/images", AiImageGenerator.OpenRouterImagesUri(new Uri(baseUrl)).ToString());
    }

    [Fact]
    public async Task OpenRouter_ErrorReply_SurfacesItsMessage()
    {
        var handler = new RecordingHandler("""{"error":{"message":"Insufficient credits","code":402}}""", System.Net.HttpStatusCode.PaymentRequired);
        var config = new AIConfiguration { Provider = "openrouter", ModelId = "qwen/qwen-image-2.1-turbo", ApiKey = "sk-test" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AiImageGenerator.GenerateAsync(config, "a toad", ArtShape.Tall, new HttpClient(handler)));

        Assert.Equal("The image service answered 402: Insufficient credits", error.Message);
    }

    private sealed class RecordingHandler(string reply, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(reply) };
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g' }, null)]
    public void SniffContentType_RecognisesStorableImages(byte[] bytes, string? expected)
    {
        Assert.Equal(expected, AiImageGenerator.SniffContentType(bytes));
    }

    [Fact]
    public void Problem_NamesWhatIsMissing()
    {
        Assert.Contains("API key", AiImageGenerator.Problem(new AIConfiguration { Provider = "openai", ModelId = "gpt-image-1" }));
        Assert.Contains("model", AiImageGenerator.Problem(new AIConfiguration { Provider = "openrouter", ApiKey = "k" }));
        Assert.Contains("base URL", AiImageGenerator.Problem(new AIConfiguration { Provider = "custom", ModelId = "sd" }));
        Assert.Null(AiImageGenerator.Problem(new AIConfiguration { Provider = "custom", ModelId = "sd", Endpoint = "http://localhost:8080/v1" }));
    }

    [Fact]
    public void SizeFor_PicksSizesEachFamilyAccepts()
    {
        Assert.Equal(new System.Drawing.Size(1024, 1536), AiImageGenerator.SizeFor("gpt-image-1", ArtShape.Tall));
        Assert.Equal(new System.Drawing.Size(1024, 1024), AiImageGenerator.SizeFor("gpt-image-1", ArtShape.Square));
        Assert.Equal(new System.Drawing.Size(1792, 1024), AiImageGenerator.SizeFor("dall-e-3", ArtShape.Wide));
        Assert.Null(AiImageGenerator.SizeFor("stable-diffusion-xl", ArtShape.Wide));
    }
}
