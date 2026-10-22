using Codex.Core.AI;

namespace Codex.Tests;

public class AiImageGeneratorTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };

    [Fact]
    public void ParseChatImageUrl_ReadsOpenRouterImages()
    {
        const string json = """
            {"choices":[{"message":{"role":"assistant","content":"Here you go","images":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]}}]}
            """;

        Assert.Equal("data:image/png;base64,AAAA", AiImageGenerator.ParseChatImageUrl(json));
    }

    [Fact]
    public void ParseChatImageUrl_TextOnlyReply_IsNull()
    {
        Assert.Null(AiImageGenerator.ParseChatImageUrl("""{"choices":[{"message":{"content":"I can't draw."}}]}"""));
    }

    [Fact]
    public void DecodeDataUrl_SniffsTheRealType()
    {
        // Labelled JPEG, but the bytes are a PNG: the stored type must match the bytes.
        var image = AiImageGenerator.DecodeDataUrl("data:image/jpeg;base64," + Convert.ToBase64String(Png));

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(Png, image.Bytes);
    }

    [Fact]
    public void DecodeDataUrl_IgnoresPlainUrls()
    {
        Assert.Null(AiImageGenerator.DecodeDataUrl("https://example.test/a.png"));
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
        Assert.Equal(new System.Drawing.Size(1024, 1536), AiImageGenerator.SizeFor("gpt-image-1", landscape: false));
        Assert.Equal(new System.Drawing.Size(1792, 1024), AiImageGenerator.SizeFor("dall-e-3", landscape: true));
        Assert.Null(AiImageGenerator.SizeFor("stable-diffusion-xl", landscape: true));
    }
}
