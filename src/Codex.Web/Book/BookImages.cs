using Codex.Core.AI;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Web.Book;

/// <summary>The picture model a user's book pictures would be drawn with, as the tray shows it before anything is drawn.</summary>
/// <param name="Label">"name · model" as the AI settings name it.</param>
/// <param name="Price">The price per picture the user set, or null (then no money is shown, never a guessed figure).</param>
/// <param name="ServerUsedToday">On the server's key: pictures this account drew today, and the daily allowance (0 for none); null on the user's own provider.</param>
public sealed record BookImageInfo(string Label, AiImagePrice? Price, (int Used, int Limit)? ServerUsedToday);

/// <summary>
/// A picture model opened for one drawing run of <see cref="Pictures"/> pictures. It draws at most that many: the GM agreed to that number,
/// and on the server's key exactly that many were set aside. Dispose it when the run ends: the pictures set aside and not drawn go back.
/// </summary>
public sealed class BookImageHandle(Func<string, ArtShape, CancellationToken, Task<GeneratedImage>> draw, int pictures, string model, AiImagePrice? price, IDisposable? reservation) : IDisposable
{
    private int _drawn;

    public int Pictures { get; } = pictures;
    public string Model { get; } = model;
    public AiImagePrice? Price { get; } = price;

    public int Drawn => Volatile.Read(ref _drawn);

    /// <summary>Draws one picture. Throws <see cref="InvalidOperationException"/> past <see cref="Pictures"/>, before anything is sent.</summary>
    public Task<GeneratedImage> DrawAsync(string prompt, ArtShape shape, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _drawn) > Pictures)
        {
            Interlocked.Decrement(ref _drawn);
            throw new InvalidOperationException("This run has drawn every picture it was opened for.");
        }

        return draw(prompt, shape, ct);
    }

    public void Dispose() => reservation?.Dispose();
}

/// <summary>The seam between book pictures and the AI settings, so drawing can be tested with a fake picture model.</summary>
public interface IBookImages
{
    /// <summary>The model the user's pictures are drawn with, or null when they have none.</summary>
    Task<BookImageInfo?> InfoAsync(string userId);

    /// <summary>Sets aside <paramref name="pictures"/> on the server's image key when that is whose it is (all or nothing, refused up front with
    /// the allowance's own message) and returns the model. Throws <see cref="AiImageNotConfiguredException"/> when there is none.</summary>
    Task<BookImageHandle> OpenAsync(string userId, int pictures);
}

public sealed class AiBookImages(AiProviderService providers, IUserRepository users, ServerAiUsage usage) : IBookImages
{
    public async Task<BookImageInfo?> InfoAsync(string userId)
    {
        var user = await users.GetUserByIdAsync(userId);
        if (providers.DescribeImage(user) is not { } label) return null;
        (int, int)? server = providers.UsesServerImageKey(user)
            ? (usage.UsedToday(userId, ServerAiUsage.Kind.Image), user?.Roles.Contains(ExposurePolicy.ServerAdminRole) == true ? 0 : usage.LimitOf(ServerAiUsage.Kind.Image))
            : null;
        return new BookImageInfo(label, providers.ImagePriceFor(user), server);
    }

    public async Task<BookImageHandle> OpenAsync(string userId, int pictures)
    {
        var reservation = await providers.ReserveImagesAsync(userId, pictures);
        try
        {
            var user = await users.GetUserByIdAsync(userId);
            return new BookImageHandle((prompt, shape, ct) => providers.GenerateImageAsync(userId, prompt, shape, reservation, ct), pictures,
                providers.DescribeImage(user) ?? string.Empty, providers.ImagePriceFor(user), reservation);
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }
}
