using System.Security.Claims;
using Codex.Persistence;
using Codex.Web;
using Codex.Web.Book;
using Codex.Core.Book;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Codex.Tests;

/// <summary>Who may make an account, use the server and create campaigns, and where books may fetch from (docs/SERVER_ACCESS_AND_VAULT.md).</summary>
public class ServerAccessTests
{
    private static ServerSettingsDocument Rule(SignUpRule rule) => new() { SignUp = rule };

    [Fact]
    public void ANewAccount_FollowsTheSignUpRule_AndAnInviteApprovesIt()
    {
        // At home.
        Assert.Equal(AccountStatus.Approved, ServerAccess.NewAccount(Rule(SignUpRule.Open), exposed: false, hasInvite: false));
        Assert.Equal(AccountStatus.Pending, ServerAccess.NewAccount(Rule(SignUpRule.Approval), false, false));
        Assert.Null(ServerAccess.NewAccount(Rule(SignUpRule.InviteOnly), false, false));
        foreach (var rule in Enum.GetValues<SignUpRule>())
        {
            Assert.Equal(AccountStatus.Approved, ServerAccess.NewAccount(Rule(rule), false, hasInvite: true));
        }

        // Open to the internet: only with an invite, whatever the rule says.
        foreach (var rule in Enum.GetValues<SignUpRule>())
        {
            Assert.Null(ServerAccess.NewAccount(Rule(rule), exposed: true, hasInvite: false));
            Assert.Equal(AccountStatus.Approved, ServerAccess.NewAccount(Rule(rule), true, true));
        }
    }

    [Fact]
    public async Task AnInvite_CountsOnlyWhenItIsReal()
    {
        var campaigns = Substitute.For<ICampaignRepository>();
        campaigns.InviteCodeExistsAsync("real").Returns(true);
        var settings = new InMemoryServerSettings(Rule(SignUpRule.InviteOnly));

        Assert.Equal(AccountStatus.Approved, await ServerAccess.NewAccountAsync(settings, false, "/campaigns/join/real", campaigns));
        Assert.Null(await ServerAccess.NewAccountAsync(settings, false, "/campaigns/join/made-up", campaigns));
        Assert.Null(await ServerAccess.NewAccountAsync(settings, true, "/", campaigns));
    }

    [Fact]
    public void Campaigns_AreCreatedByGameMastersAndAdmins_OrAnyoneWhenTheServerSaysSo()
    {
        var gms = new ServerSettingsDocument { CampaignCreation = CampaignCreationRule.GameMasters };
        var anyone = new ServerSettingsDocument { CampaignCreation = CampaignCreationRule.Anyone };
        var player = new UserDocument { Roles = [UserRoles.Player] };
        var gm = new UserDocument { Roles = [UserRoles.Player, UserRoles.GameMaster] };
        var admin = new UserDocument { Roles = [UserRoles.Player, UserRoles.ServerAdmin] };
        var waitingGm = new UserDocument { Roles = [UserRoles.GameMaster], Status = AccountStatus.Pending };
        var offGm = new UserDocument { Roles = [UserRoles.GameMaster], Status = AccountStatus.Disabled };

        Assert.False(ServerAccess.MayCreateCampaigns(player, gms));
        Assert.True(ServerAccess.MayCreateCampaigns(gm, gms));
        Assert.True(ServerAccess.MayCreateCampaigns(admin, gms));
        Assert.True(ServerAccess.MayCreateCampaigns(player, anyone));
        Assert.False(ServerAccess.MayCreateCampaigns(waitingGm, anyone));
        Assert.False(ServerAccess.MayCreateCampaigns(offGm, gms));
        Assert.False(ServerAccess.MayCreateCampaigns(null, anyone));
    }

    [Fact]
    public void ThePrincipal_SaysWhetherItWaits_AndNoticesAChangedAccount()
    {
        var user = new UserDocument { Id = "u1", Username = "ann", Roles = [UserRoles.Player], Status = AccountStatus.Pending };
        var principal = UserPrincipal.For(user);
        Assert.True(ServerAccess.IsWaiting(principal));
        Assert.True(ServerAccess.Matches(principal, user));

        user.Status = AccountStatus.Approved;
        Assert.False(ServerAccess.Matches(principal, user));
        Assert.False(ServerAccess.IsWaiting(UserPrincipal.For(user)));

        var approved = UserPrincipal.For(user);
        user.Roles.Add(UserRoles.GameMaster);
        Assert.False(ServerAccess.Matches(approved, user));
    }

    [Theory]
    [InlineData("imgur.com", "imgur.com", true)]
    [InlineData("imgur.com", "i.imgur.com", true)]
    [InlineData("imgur.com", "I.IMGUR.COM.", true)]
    [InlineData("imgur.com", "evilimgur.com", false)]
    [InlineData("imgur.com", "imgur.com.evil.net", false)]
    [InlineData("i.imgur.com", "imgur.com", false)]
    [InlineData("https://Fonts.Gstatic.com/s/x.woff2", "fonts.gstatic.com", true)]
    [InlineData("*.githack.com", "rawcdn.githack.com", true)]
    public void TheFetchList_MatchesWholeNames(string site, string host, bool listed) =>
        Assert.Equal(listed, ServerSettingsDocument.Lists([site], host));

    [Theory]
    [InlineData("fonts.gstatic.com", "fonts.gstatic.com")]
    [InlineData("  HTTPS://cdn.jsdelivr.net/npm/x  ", "cdn.jsdelivr.net")]
    [InlineData("example.com:8443", "example.com")]
    [InlineData("localhost", null)]
    [InlineData("user@example.com", null)]
    [InlineData("exa mple.com", null)]
    [InlineData("a..b", null)]
    [InlineData("", null)]
    public void ASite_IsAHostName(string text, string? site) => Assert.Equal(site, ServerSettingsDocument.Site(text));

    [Fact]
    public void TheDefaultList_HasTheHostsTashasStyleUses()
    {
        foreach (var host in new[] { "i.imgur.com", "rawcdn.githack.com", "fonts.googleapis.com", "fonts.gstatic.com" })
        {
            Assert.True(ServerSettingsDocument.Lists(ServerSettingsDocument.DefaultFetchSites, host), host);
        }
    }

    [Fact]
    public async Task TheFetcher_KeepsToTheList_AtEveryRedirect()
    {
        using var site = new BookCssWebTests.Site();
        site.Serve("/a.png", "image/png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]);
        site.RedirectAway("/away.png", $"http://localhost:{site.Port}/a.png");
        var settings = new InMemoryServerSettings(new() { WebFetch = WebFetchRule.Listed, FetchSites = ["127.0.0.1.example"] });
        var fetcher = BookWebFetcher.Over(new HttpClient(BookWebFetcher.Handler(_ => true)), httpsOnly: false,
            async host => host == "127.0.0.1" || await BookWebFetcher.Listed(settings)(host));

        var (file, _) = await fetcher.FetchAsync(site.Url("/a.png"), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);
        Assert.NotNull(file);

        var before = site.Requests;
        var (moved, failure) = await fetcher.FetchAsync(site.Url("/away.png"), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);
        Assert.Null(moved);
        Assert.Equal(BookWebProblem.NotListed, failure!.Problem);
        Assert.Equal(before + 1, site.Requests);

        // "Any public site" lets it through to the address check, which is the same as before.
        await settings.UpdateAsync(s => s.WebFetch = WebFetchRule.AnyPublic);
        Assert.True(await BookWebFetcher.Listed(settings)("anything.example"));
        await settings.UpdateAsync(s => s.WebFetch = WebFetchRule.Listed);
        Assert.False(await BookWebFetcher.Listed(settings)("anything.example"));
    }

    [Fact]
    public void TheSettingsLog_SaysWhatChanged()
    {
        var before = new ServerSettingsDocument();
        var after = before.Copy();
        Assert.Null(SettingsChange.Describe(before, after));
        after.SignUp = SignUpRule.Open;
        after.FetchSites.Add("example.com");
        after.FetchSites.Remove("i.imgur.com");
        var text = SettingsChange.Describe(before, after)!;
        Assert.Contains("Approval → Open", text);
        Assert.Contains("added: example.com", text);
        Assert.Contains("removed: i.imgur.com", text);

        // The server's AI key, and a copy that keeps it (the store hands out copies).
        after.ServerAi = ServerAiRule.GameMasters;
        Assert.Equal(ServerAiRule.GameMasters, after.Copy().ServerAi);
        Assert.Contains("server AI key: as before → GameMasters", SettingsChange.Describe(before, after)!);
    }

    // The cookie handler's check on every request.
    private static async Task<(CookieValidatePrincipalContext Context, bool SignedOut)> ValidateAsync(UserDocument? stored, ClaimsPrincipal principal)
    {
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("u1").Returns(stored);
        var auth = Substitute.For<IAuthenticationService>();
        var services = new ServiceCollection().AddSingleton(new AccountCheck(users)).AddSingleton(auth).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var context = new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), new AuthenticationTicket(principal, scheme.Name));
        await AccountCheck.ValidateAsync(context);
        var signedOut = auth.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAuthenticationService.SignOutAsync));
        return (context, signedOut);
    }

    [Fact]
    public async Task ASignedInAccount_FollowsWhatTheAdminChanged()
    {
        var user = new UserDocument { Id = "u1", Username = "ann", Roles = [UserRoles.Player] };
        var principal = UserPrincipal.For(user);

        var (same, _) = await ValidateAsync(user, principal);
        Assert.Same(principal, same.Principal);

        var promoted = new UserDocument { Id = "u1", Username = "ann", Roles = [UserRoles.Player, UserRoles.GameMaster] };
        var (changed, _) = await ValidateAsync(promoted, principal);
        Assert.True(changed.Principal!.IsInRole(UserRoles.GameMaster));
        Assert.True(changed.ShouldRenew);

        var off = new UserDocument { Id = "u1", Username = "ann", Roles = [UserRoles.Player], Status = AccountStatus.Disabled };
        var (rejected, signedOut) = await ValidateAsync(off, principal);
        Assert.Null(rejected.Principal);
        Assert.True(signedOut);

        var (gone, goneOut) = await ValidateAsync(null, principal);
        Assert.Null(gone.Principal);
        Assert.True(goneOut);
    }
}

/// <summary>The settings, the log and the move of an existing server, against the database.</summary>
public class ServerSettingsStoreTests(RavenDbFixture fixture) : IClassFixture<RavenDbFixture>
{
    [Fact]
    public async Task AnExistingServer_KeepsItsAccounts_AndItsCampaignOwnersBecomeGameMasters()
    {
        var db = fixture.Open(fixture.DbName);
        var users = new RavenUserRepository(db);
        var settings = new RavenServerSettingsStore(db);

        // No accounts: a new server, nothing to move; it reads the safe defaults.
        Assert.False(await settings.MoveExistingServerAsync());
        var fresh = await settings.GetAsync();
        Assert.Equal((SignUpRule.Approval, CampaignCreationRule.GameMasters, WebFetchRule.Listed, false),
            (fresh.SignUp, fresh.CampaignCreation, fresh.WebFetch, fresh.SetupDone));

        // Accounts stored before approval existed have no status.
        await users.CreateUserAsync(new UserDocument { Id = "users/owner", Username = "owner", Roles = [UserRoles.Player] });
        await db.Store.Operations.SendAsync(new Raven.Client.Documents.Operations.PatchOperation("users/owner", null,
            new Raven.Client.Documents.Operations.PatchRequest { Script = "delete this.Status; delete this.CreatedAt;" }));

        await users.CreateUserAsync(new UserDocument { Id = "users/player", Username = "player", Roles = [UserRoles.Player] });
        await users.CreateUserAsync(new UserDocument { Id = "users/admin", Username = "admin", Roles = [UserRoles.Player, UserRoles.ServerAdmin] });
        using (var session = db.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new CampaignDocument { Id = "campaigns/1", OwnerId = "users/owner", Name = "Ashes", SystemId = "x" });
            await session.SaveChangesAsync();
        }

        Assert.True(await settings.MoveExistingServerAsync());
        Assert.False(await settings.MoveExistingServerAsync());

        var owner = await users.GetUserByIdAsync("users/owner");
        Assert.Equal(AccountStatus.Approved, owner!.Status);
        Assert.Contains(UserRoles.GameMaster, owner.Roles);
        Assert.DoesNotContain(UserRoles.GameMaster, (await users.GetUserByIdAsync("users/player"))!.Roles);
        Assert.Contains(UserRoles.GameMaster, (await users.GetUserByIdAsync("users/admin"))!.Roles);

        var moved = await settings.GetAsync();
        Assert.True(moved.SetupDone);
        Assert.Equal(SignUpRule.Approval, moved.SignUp);
        Assert.Equal(WebFetchRule.AnyPublic, moved.WebFetch);
        // The server's AI key keeps the rule it had (everyone at home, the admin alone on the internet) until the admin chooses.
        Assert.Null(moved.ServerAi);
        await settings.UpdateAsync(s => s.ServerAi = ServerAiRule.GameMasters);
        Assert.Equal(ServerAiRule.GameMasters, settings.Get().ServerAi);
        Assert.Equal(ServerAiRule.GameMasters, new RavenServerSettingsStore(db).Get().ServerAi);

        // Changes keep a clean list, and the log reads back newest first.
        var saved = await settings.UpdateAsync(s => s.FetchSites = ["HTTPS://Example.com/x", "not a site", "example.com"]);
        Assert.Equal(["example.com"], saved.FetchSites);
        await settings.LogAsync(new AdminLogDocument { At = DateTime.UtcNow.AddMinutes(-1), Action = AdminAction.Approved, ByName = "admin", TargetName = "a" });
        await settings.LogAsync(new AdminLogDocument { At = DateTime.UtcNow, Action = AdminAction.Rejected, ByName = "admin", TargetName = "b" });
        Assert.Equal([AdminAction.Rejected, AdminAction.Approved], (await settings.RecentLogAsync(10)).Select(e => e.Action));

        // A deleted account frees its name.
        await users.TryReserveUsernameAsync("Taken", "users/taken");
        await users.CreateUserAsync(new UserDocument { Id = "users/taken", Username = "Taken", Status = AccountStatus.Pending });
        Assert.Equal(1, await users.CountPendingAsync());
        Assert.Equal("Taken", (await users.ListAsync())[0].Username);
        Assert.False(await users.TryReserveUsernameAsync("taken", "users/other"));
        Assert.True(await users.DeleteAsync("users/taken"));
        Assert.True(await users.TryReserveUsernameAsync("taken", "users/other"));
        Assert.False(await users.DeleteAsync("users/taken"));
    }
}
