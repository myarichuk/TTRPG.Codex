using Avalonia.Media.Imaging;
using QRCoder;

namespace Codex.Launcher;

internal static class QrImage
{
    /// <summary>A QR code for <paramref name="text"/>: dark brown on parchment, with a quiet border, so a phone
    /// camera reads it from the screen.</summary>
    public static Bitmap Make(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12, new byte[] { 0x1f, 0x16, 0x0c }, new byte[] { 0xf3, 0xe9, 0xd2 }, drawQuietZones: true);
        return new Bitmap(new MemoryStream(png));
    }
}
