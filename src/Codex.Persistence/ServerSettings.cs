namespace Codex.Persistence;

/// <summary>Who may make an account (docs/SERVER_ACCESS_AND_VAULT.md 3).</summary>
public enum SignUpRule
{
    /// <summary>Anyone who reaches the server gets an account at once.</summary>
    Open,

    /// <summary>Anyone may sign up, and waits for an admin to approve them; arriving with a campaign invite approves them as a player.</summary>
    Approval,

    /// <summary>Only someone arriving with a campaign invite.</summary>
    InviteOnly
}

/// <summary>Who may create campaigns.</summary>
public enum CampaignCreationRule
{
    /// <summary>Game Masters and admins.</summary>
    GameMasters,

    /// <summary>Every approved account.</summary>
    Anyone
}

/// <summary>Where a book's stylesheet may have the server fetch fonts, pictures and imported sheets from (docs/BOOK_EXPORT.md 41.14).</summary>
public enum WebFetchRule
{
    /// <summary>Only the sites on <see cref="ServerSettingsDocument.FetchSites"/>.</summary>
    Listed,

    /// <summary>Any public https address.</summary>
    AnyPublic
}

/// <summary>Who may fall back to the AI key in the server's own configuration when they have no model of their own
/// (docs/SERVER_ACCESS_AND_VAULT.md 6). The daily allowance applies to all but the admin.</summary>
public enum ServerAiRule
{
    /// <summary>The server's admin only.</summary>
    Admins,

    /// <summary>Game Masters (the server role) and admins.</summary>
    GameMasters,

    /// <summary>Every approved account.</summary>
    Everyone
}

/// <summary>The server's own settings, chosen in the first-time setup and changed on the admin page. One document.</summary>
public class ServerSettingsDocument
{
    public const string DocumentId = "server/settings";

    /// <summary>The fetch list a new server starts with: font services, the picture and file hosts brews use.</summary>
    public static readonly IReadOnlyList<string> DefaultFetchSites =
    [
        "fonts.googleapis.com", "fonts.gstatic.com", "use.typekit.net", "fonts.cdnfonts.com",
        "i.imgur.com", "cdn.jsdelivr.net", "cdnjs.cloudflare.com",
        "raw.githubusercontent.com", "rawcdn.githack.com", "raw.githack.com",
        "homebrewery.naturalcrit.com"
    ];

    public string Id { get; set; } = DocumentId;

    /// <summary>False until the admin has finished (or skipped) the setup steps after making their account.</summary>
    public bool SetupDone { get; set; }

    public SignUpRule SignUp { get; set; } = SignUpRule.Approval;
    public CampaignCreationRule CampaignCreation { get; set; } = CampaignCreationRule.GameMasters;
    public WebFetchRule WebFetch { get; set; } = WebFetchRule.Listed;
    public List<string> FetchSites { get; set; } = DefaultFetchSites.ToList();

    /// <summary>Who may use the server's AI key. Null on a server set up before this setting, which keeps the rule it had: everyone
    /// at home, only the admin while the server is open to the internet. A new server's setup stores Game Masters.</summary>
    public ServerAiRule? ServerAi { get; set; }

    /// <summary>How many megabytes of pictures and files each account may keep (docs/SERVER_ACCESS_AND_VAULT.md 15). Null, as on every
    /// server until the admin sets one, is no limit.</summary>
    public int? StorageLimitMb { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ServerSettingsDocument Copy() => new()
    {
        Id = Id,
        SetupDone = SetupDone,
        SignUp = SignUp,
        CampaignCreation = CampaignCreation,
        WebFetch = WebFetch,
        FetchSites = FetchSites.ToList(),
        ServerAi = ServerAi,
        StorageLimitMb = StorageLimitMb,
        UpdatedAt = UpdatedAt
    };

    /// <summary>A site as the list keeps it: a host name, lower case, no scheme, path, port or leading dot. Null when it is not one.</summary>
    public static string? Site(string? text)
    {
        var s = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Contains("://")) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        s = s.Split('/', '?', '#')[0];
        if (s.Contains('@')) return null;
        if (s.Contains(':')) s = s[..s.IndexOf(':')];
        s = s.Trim('.');
        if (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        if (s.Length == 0 || s.Length > 253 || !s.Contains('.')) return null;
        return s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.') && !s.Contains("..") ? s : null;
    }

    /// <summary>Whether <paramref name="host"/> is a listed site or a name under one (<c>cdn.example.com</c> under <c>example.com</c>),
    /// matched on whole labels: <c>evilexample.com</c> and <c>example.com.evil.net</c> are not under <c>example.com</c>.</summary>
    public static bool Lists(IEnumerable<string> sites, string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return false;
        foreach (var site in sites)
        {
            if (Site(site) is not { } s) continue;
            if (h == s || h.EndsWith("." + s, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}

/// <summary>What an admin did, kept so "who let this person in" has an answer.</summary>
public enum AdminAction
{
    SetUp,
    Approved,
    Rejected,
    Disabled,
    Enabled,
    RoleGiven,
    RoleTaken,
    Deleted,
    SettingsChanged
}

public class AdminLogDocument
{
    public string Id { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string ByUserId { get; set; } = string.Empty;
    public string ByName { get; set; } = string.Empty;
    public AdminAction Action { get; set; }

    /// <summary>The account the action was about, by name as it was then; empty for settings.</summary>
    public string? TargetName { get; set; }

    /// <summary>The role, or the settings that changed, in words the log shows as they are (role keys, setting names).</summary>
    public string? Detail { get; set; }
}

/// <summary>The server's settings, the admin log, and the one-time move of an existing server onto approval and roles.</summary>
public interface IServerSettingsStore
{
    /// <summary>The settings; a server that has none yet gets the defaults a new server starts with (not stored).</summary>
    Task<ServerSettingsDocument> GetAsync();

    /// <summary>The settings for a caller that cannot wait (choosing an AI client): the kept copy, or one read now.</summary>
    ServerSettingsDocument Get();

    /// <summary>Applies <paramref name="change"/> to the stored settings (the defaults when there are none) and stores them.</summary>
    Task<ServerSettingsDocument> UpdateAsync(Action<ServerSettingsDocument> change);

    Task LogAsync(AdminLogDocument entry);
    Task<IReadOnlyList<AdminLogDocument>> RecentLogAsync(int count);

    /// <summary>
    /// A server that has accounts and no settings was made before approval and roles: its accounts stay approved, everyone who
    /// owns a campaign becomes a Game Master so nobody loses what they run, fetching stays open to any public site as it was, and
    /// setup counts as done. Sign-up becomes "needs approval". Does nothing on a new server or one already moved. Returns whether it moved it.
    /// </summary>
    Task<bool> MoveExistingServerAsync();
}
