namespace Codex.Web.Services;

public static class InviteLinks
{
    /// <summary>The link a DM gives players. It uses <c>Codex:PublicUrl</c> when the host (the tray launcher)
    /// knows an address players can reach, because the DM's own browser is usually on <c>localhost</c>,
    /// which means nothing on a player's phone.</summary>
    public static string Build(string browserBaseUri, string? publicUrl, string inviteCode)
    {
        var root = string.IsNullOrWhiteSpace(publicUrl) ? browserBaseUri : publicUrl.Trim();
        return $"{root.TrimEnd('/')}/campaigns/join/{inviteCode}";
    }
}
