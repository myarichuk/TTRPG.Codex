using Microsoft.Extensions.FileProviders;

namespace Codex.Web;

/// <summary>
/// An in-memory <see cref="IFileInfo"/> wrapping PEM key bytes, so an Apple Sign-In private key
/// supplied as inline configuration never has to be written to a temp file on disk (B11: the
/// previous approach wrote to Path.GetTempPath() and never deleted it).
/// </summary>
public sealed class InMemoryPemFileInfo(string pemContent, string name) : IFileInfo
{
    private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(pemContent);

    public bool Exists => true;
    public long Length => _bytes.Length;
    public string? PhysicalPath => null;
    public string Name { get; } = name;
    public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
    public bool IsDirectory => false;

    public Stream CreateReadStream() => new MemoryStream(_bytes, writable: false);
}
