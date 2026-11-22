namespace Codex.Persistence;

/// <summary>How a map's grid is laid out (MAPS_DESIGN.md 2, 9.2). The browser draws it over the picture, so turning it off needs no new image.</summary>
public enum MapGridKind
{
    None,
    Square,

    /// <summary>Hexes in rows (pointy side up).</summary>
    HexRows,

    /// <summary>Hexes in columns (flat side up).</summary>
    HexColumns
}

/// <summary>A map's grid, in the picture's own pixels.</summary>
public class MapGrid
{
    public const double MinCellSize = 4;
    public const double MaxCellSize = 2000;
    public const int MaxUnitLength = 40;

    public MapGridKind Kind { get; set; } = MapGridKind.None;

    /// <summary>One cell's width in image pixels.</summary>
    public double CellSize { get; set; } = 50;

    /// <summary>Where the grid starts on the picture, in image pixels.</summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    /// <summary>One cell in the game's own units, as the GM writes it: "5 ft", "1 mile". Free text, since systems differ.</summary>
    public string? Unit { get; set; }

    /// <summary>Whether the grid is drawn over the picture.</summary>
    public bool Shown { get; set; } = true;

    /// <summary>The grid with a kind that exists, a cell size in range, an offset inside one cell and a short unit.</summary>
    public static MapGrid Tidy(MapGrid? grid)
    {
        if (grid == null) return new MapGrid();
        var cell = double.IsFinite(grid.CellSize) ? Math.Clamp(grid.CellSize, MinCellSize, MaxCellSize) : 50;
        return new MapGrid
        {
            Kind = Enum.IsDefined(grid.Kind) ? grid.Kind : MapGridKind.None,
            CellSize = cell,
            OffsetX = Offset(grid.OffsetX, cell),
            OffsetY = Offset(grid.OffsetY, cell),
            Unit = string.IsNullOrWhiteSpace(grid.Unit) ? null : grid.Unit.Trim() is var u && u.Length > MaxUnitLength ? u[..MaxUnitLength] : grid.Unit.Trim(),
            Shown = grid.Shown
        };

        static double Offset(double value, double cell) => double.IsFinite(value) ? ((value % cell) + cell) % cell : 0;
    }
}

/// <summary>A picture stored as an attachment on its <see cref="MapDocument"/>. Its type is what its bytes are, never what the upload said.</summary>
public class MapImage
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Goes up with every new picture, for the URL's cache-busting query.</summary>
    public int Version { get; set; }
}

/// <summary>A point on a map, in the picture's own pixels.</summary>
public record struct MapPoint(double X, double Y);

public enum MapMarkerKind
{
    /// <summary>A single point.</summary>
    Pin,

    /// <summary>A polygon of three or more corners.</summary>
    Area
}

/// <summary>Where a place is on a map. It holds only the place's id, never its name, text or visibility (MAPS_DESIGN.md 9.2): those are read
/// from the place, so a player gets a marker only when they may see its place.</summary>
public class MapMarker
{
    public const int MaxCorners = 200;

    public string Id { get; set; } = string.Empty;

    /// <summary>The place this marks: a <see cref="LocationDocument.Id"/> in the map's region.</summary>
    public string LocationId { get; set; } = string.Empty;

    public MapMarkerKind Kind { get; set; } = MapMarkerKind.Pin;

    /// <summary>One point for a pin, the corners in order for an area.</summary>
    public List<MapPoint> Points { get; set; } = new();

    /// <summary>Where the label is drawn; null: beside the pin, or at the area's middle.</summary>
    public MapPoint? LabelAt { get; set; }
}

/// <summary>
/// A map: a picture of a place (or of a region) and where its places are on it (MAPS_DESIGN.md 9.2). Its own document, so loading the
/// places list never loads maps. A place can have several maps, its versions (summer and winter, before and after the fire); one is
/// <see cref="Current"/>. Coordinates are in the picture's own pixels. Players get only what <see cref="IMapRepository"/> lets through.
/// </summary>
public class MapDocument
{
    public const string ImageAttachment = "map";
    public const int MaxName = 200;
    public const int MaxLabel = 100;
    public const int MaxMarkers = 2000;

    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string RegionId { get; set; } = string.Empty;

    /// <summary>The place it shows; null for the region's own map.</summary>
    public string? LocationId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Which version this is ("Summer", "After the fire"); empty for a place's only map.</summary>
    public string? Label { get; set; }

    /// <summary>The version the place's pin opens. Exactly one per place (or region) once it has maps.</summary>
    public bool Current { get; set; }

    /// <summary>Whether players may see this version at all: off for the town after the fire, before the fire happens. Off for a new map
    /// and a copy, so a picture is never shown before the GM means to: once shown, players see the whole picture.</summary>
    public bool ShownToPlayers { get; set; }

    /// <summary>The picture (attachment <see cref="ImageAttachment"/>); null until one is uploaded.</summary>
    public MapImage? Image { get; set; }

    public MapGrid Grid { get; set; } = new();

    public List<MapMarker> Markers { get; set; } = new();

    /// <summary>Goes up with every change, so a page can tell its copy is old.</summary>
    public int Version { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A map's picture, read straight from its attachment: the caller streams <see cref="Content"/> and then disposes this, which closes
/// the database session the stream belongs to.</summary>
public sealed class MapImageFile : IDisposable
{
    private readonly IDisposable[] owners;
    private bool disposed;

    internal MapImageFile(Stream content, string contentType, long size, params IDisposable[] owners)
    {
        Content = content;
        ContentType = contentType;
        Size = size;
        this.owners = owners;
    }

    public Stream Content { get; }
    public string ContentType { get; }
    public long Size { get; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var owner in owners) owner.Dispose();
    }
}

/// <summary>One per map owner (a place, or a region for its own map): saved with every new map under optimistic concurrency, so two
/// Add map presses at once can't both make a map, or both make it current.</summary>
public class MapOwnerGuard
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public int Creates { get; set; }

    public static string Prefix(string campaignId) => $"MapOwnerGuards/{campaignId}/";

    public static string IdFor(string campaignId, string regionId, string? locationId) => Prefix(campaignId) + (locationId ?? regionId);
}

/// <summary>Why a map picture was refused.</summary>
public enum MapImageProblem
{
    None,

    /// <summary>Not JPEG, PNG or WebP by its bytes, whatever its name or declared type (an SVG renamed .png lands here).</summary>
    Unsupported,

    /// <summary>It says it is a JPEG, PNG or WebP but is not a well-formed one.</summary>
    Malformed,

    /// <summary>Over <see cref="MapImages.MaxBytes"/>.</summary>
    TooManyBytes,

    /// <summary>Over <see cref="MapImages.MaxPixels"/>, read from its header before anything decodes it.</summary>
    TooManyPixels,

    /// <summary>No such map in the campaign, or the caller is not its GM.</summary>
    NotAllowed
}

/// <summary>The limits on a map's picture (MAPS_DESIGN.md 9.3): Owlbear's free plan's, so anything Codex accepts can go on to Owlbear unchanged.
/// Portraits and art keep <see cref="RavenMediaStore.MaxBytes"/>.</summary>
public static class MapImages
{
    public const long MaxBytes = 25L * 1024 * 1024;
    public const long MaxPixels = 67_000_000;

    /// <summary>The picture with its metadata (a camera's GPS position, the author's name) left out, its type taken from its bytes and
    /// its size read from its header, never decoded: so a crafted file can't use up memory, and SVG, GIF or anything else is refused.</summary>
    public static MapImageProblem Check(byte[] bytes, out Codex.Core.Book.BookImage? image)
    {
        image = null;
        if (bytes.LongLength > MaxBytes) return MapImageProblem.TooManyBytes;
        return Codex.Core.Book.BookImageSanitizer.TrySanitize(bytes, int.MaxValue, MaxPixels, out image) switch
        {
            Codex.Core.Book.BookImageProblem.None => MapImageProblem.None,
            Codex.Core.Book.BookImageProblem.TooLarge => MapImageProblem.TooManyPixels,
            Codex.Core.Book.BookImageProblem.Unsupported => MapImageProblem.Unsupported,
            // Too short to be any picture, or not one of the three by its first bytes: an SVG, a text file, a GIF.
            _ when !LooksLikeJpegPngOrWebP(bytes) => MapImageProblem.Unsupported,
            _ => MapImageProblem.Malformed
        };
    }

    private static bool LooksLikeJpegPngOrWebP(byte[] b) => Codex.Core.AI.AiImageGenerator.SniffContentType(b) is "image/jpeg" or "image/png" or "image/webp";
}
