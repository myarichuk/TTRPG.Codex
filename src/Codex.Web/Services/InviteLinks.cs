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

    /// <summary>The second link, for players who reach the host another way (<c>Codex:RemoteUrl</c>, the
    /// Tailscale address), or <c>null</c> when there is only one way in.</summary>
    public static string? BuildRemote(string? remoteUrl, string inviteCode) =>
        string.IsNullOrWhiteSpace(remoteUrl) ? null : Build("", remoteUrl, inviteCode);
}
