namespace Codex.Persistence;

/// <summary>
/// Maps of places and regions (MAPS_DESIGN.md 9). Like <see cref="IRegionRepository"/>, every read is filtered for the caller before it
/// is returned, so nothing hidden reaches a player's page:
/// <list type="bullet">
/// <item>a player gets a map only when it has a picture, is <see cref="MapDocument.ShownToPlayers"/> and its place is known to them, or,
/// for a region's own map, the region has at least one place they know;</item>
/// <item>a player gets only the markers of places they know; a hidden place's pin or area is never sent, not even its shape;</item>
/// <item>everyone gets only the markers of places that still exist in the map's region (the rest are dropped on the next save).</item>
/// </list>
/// Every write is the GM's alone, by their role as stored now, and refuses a party preview.
/// </summary>
public interface IMapRepository
{
    /// <summary>Every map of the campaign the caller may see, in one query: each owner's current version first.</summary>
    Task<IReadOnlyList<MapDocument>> ListForCampaignAsync(CampaignAccess access);

    /// <summary>Every map in a region the caller may see: the region's own and its places', each owner's current version first.</summary>
    Task<IReadOnlyList<MapDocument>> ListForRegionAsync(string regionId, CampaignAccess access);

    /// <summary>The versions of one place's maps (<paramref name="locationId"/> null: the region's own), the current one first, then
    /// oldest first. A player gets only the versions shown to players, so the first is the one to open.</summary>
    Task<IReadOnlyList<MapDocument>> ListVersionsAsync(string regionId, string? locationId, CampaignAccess access);

    /// <summary>The version a place's pin opens for the caller: the first of <see cref="ListVersionsAsync"/>, or null.</summary>
    Task<MapDocument?> GetCurrentAsync(string regionId, string? locationId, CampaignAccess access);

    /// <summary>One map, filtered for the caller; null when there is no such map in the campaign or the caller may not see it.</summary>
    Task<MapDocument?> GetAsync(string mapId, CampaignAccess access);

    /// <summary>A new, empty map (no picture yet) of a place, or of the region when <paramref name="locationId"/> is null. Named after its
    /// place when <paramref name="name"/> is blank. It starts hidden from players: showing it is the GM's own step. The owner's first map
    /// becomes current. When the owner already has a map with no picture, that one is returned instead, so pressing Add map twice makes
    /// one map. GM only; null when the region or place is not there.</summary>
    Task<MapDocument?> CreateAsync(string regionId, string? locationId, string? name, string? label, CampaignAccess access);

    /// <summary>Changes a map's name and version label, nothing else. GM only.</summary>
    Task<bool> RenameAsync(string mapId, string name, string? label, CampaignAccess access);

    /// <summary>Changes a map's grid, nothing else. GM only.</summary>
    Task<bool> SetGridAsync(string mapId, MapGrid grid, CampaignAccess access);

    /// <summary>Makes this version the one its place's pin opens; every other version of the same place stops being current. GM only.</summary>
    Task<bool> SetCurrentAsync(string mapId, CampaignAccess access);

    /// <summary>Shows a version to players or hides it from them. GM only.</summary>
    Task<bool> SetShownToPlayersAsync(string mapId, bool shown, CampaignAccess access);

    /// <summary>A copy of a map as a new version of the same place: its picture, grid and markers (with new ids). The copy is not current and
    /// not shown to players, since a new version is usually a state the party hasn't reached yet. Within the owner's storage limit
    /// (otherwise <c>Service.Storage.Full</c> is thrown). GM only; null when there is no such map.</summary>
    Task<MapDocument?> CopyAsNewVersionAsync(string mapId, string? label, CampaignAccess access);

    /// <summary>Puts a place on a map, or moves it: a marker with an existing id, or for a place already on the map, replaces that marker.
    /// Points are kept inside the picture. GM only; null when the place is not in the map's region or the shape is not a pin (one point) or an
    /// area (3 to <see cref="MapMarker.MaxCorners"/> corners).</summary>
    Task<MapMarker?> PlaceMarkerAsync(string mapId, MapMarker marker, CampaignAccess access);

    /// <summary>Takes a marker off a map. GM only.</summary>
    Task<bool> RemoveMarkerAsync(string mapId, string markerId, CampaignAccess access);

    /// <summary>Deletes a map and its picture. When it was current, the oldest remaining version becomes current. GM only.</summary>
    Task<bool> DeleteAsync(string mapId, CampaignAccess access);

    /// <summary>Stores a map's picture: JPEG, PNG or WebP by its bytes (never what it was called), at most <see cref="MapImages.MaxBytes"/>
    /// and <see cref="MapImages.MaxPixels"/>, its metadata left out, and within the owner's storage limit (otherwise
    /// <c>Service.Storage.Full</c> is thrown). GM only.</summary>
    Task<MapImageProblem> PutImageAsync(string mapId, Stream content, CampaignAccess access);

    /// <summary>A map's picture as a stream, only when <see cref="GetAsync"/> would give the caller the map. The caller disposes it.</summary>
    Task<MapImageFile?> OpenImageAsync(string mapId, CampaignAccess access);
}
