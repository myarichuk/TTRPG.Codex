using Codex.Persistence;
using Codex.Web;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The Phase 4 exit criterion: after and between sessions.
///   1. The DM authors lore and reveals one fact to a single actor.
///   2. That actor's player sees it in the Grimoire; the other player doesn't; nobody but the
///      DM ever receives the DM-only fact - asserted on the DOM and the circuit payload.
///   3. A player adds a private and a public session note; the other player sees only the
///      public one (DOM and payload); the DM sees both.
///   4. The DM writes a recap; players can read it.
///
/// One scenario, one Fact, like <see cref="Phase3ExitCriteriaTests"/>: one app boot, three
/// logged-in circuits, sequential stages of the same campaign.
/// </summary>
[Collection("RavenDb")]
public class Phase4AfterSessionTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public Phase4AfterSessionTests(AppFixture app) => _app = app;

    [Fact]
    public async Task Phase4_AfterAndBetweenSessions_Async()
    {
        const string dmPassword = "Correct-Horse-1";
        const string playerPassword = "Correct-Horse-2";
        var dmId = "p4-dm-" + Guid.NewGuid();
        var player1Id = "p4-p1-" + Guid.NewGuid();
        var player2Id = "p4-p2-" + Guid.NewGuid();

        await _app.SeedUserAsync(dmId, "dm_" + dmId, dmPassword, "Player");
        await _app.SeedUserAsync(player1Id, "aria_" + player1Id, playerPassword, "Player");
        await _app.SeedUserAsync(player2Id, "bram_" + player2Id, playerPassword, "Player");

        var browser = await _app.GetBrowserAsync();
        var contextOptions = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await browser.NewContextAsync(contextOptions);
        await using var player1Context = await browser.NewContextAsync(contextOptions);
        await using var player2Context = await browser.NewContextAsync(contextOptions);

        var dm = await dmContext.NewPageAsync();
        var player1 = await player1Context.NewPageAsync();
        var player2 = await player2Context.NewPageAsync();

        var player1Frames = new List<string>();
        var player2Frames = new List<string>();
        player1.WebSocket += (_, ws) => ws.FrameReceived += (_, frame) =>
        {
            if (frame.Text != null)
            {
                lock (player1Frames) player1Frames.Add(frame.Text);
            }
        };
        player2.WebSocket += (_, ws) => ws.FrameReceived += (_, frame) =>
        {
            if (frame.Text != null)
            {
                lock (player2Frames) player2Frames.Add(frame.Text);
            }
        };

        await LoginAsync(dm, "dm_" + dmId, dmPassword);
        await LoginAsync(player1, "aria_" + player1Id, playerPassword);
        await LoginAsync(player2, "bram_" + player2Id, playerPassword);

        await dm.GotoAsync("/campaigns");
        await dm.GetByRole(AriaRole.Button, new() { Name = "Create Your First Campaign" }).ClickAsync();
        await dm.GetByRole(AriaRole.Button, new() { Name = "Combat" }).WaitForAsync();
        var campaignId = new Uri(dm.Url).Segments[^1].TrimEnd('/');

        var inviteUrl = await dm.Locator("div.card:has(h5:text('Invite')) input").InputValueAsync();
        await player1.GotoAsync(inviteUrl);
        await player1.GetByText("You're in!").WaitForAsync();
        await player2.GotoAsync(inviteUrl);
        await player2.GetByText("You're in!").WaitForAsync();

        var ariaActorId = "p4-aria-" + Guid.NewGuid();
        var bramActorId = "p4-bram-" + Guid.NewGuid();
        await _app.SeedActorsAsync(new[]
        {
            new TestSeedActor { Id = ariaActorId, CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = player1Id, Visibility = ActorVisibility.Known, Hp = 12, HpMax = 12 },
            new TestSeedActor { Id = bramActorId, CampaignId = campaignId, Name = "Bram", Kind = ActorKind.PlayerCharacter, OwnerUserId = player2Id, Visibility = ActorVisibility.Known, Hp = 10, HpMax = 10 }
        });

        // ---- DM authors two facts, one Knowers-only, one DM-only. ----

        const string revealedSummary = "P4 The Bridge Is Trapped";
        const string secretSummary = "P4 Secret Villain Plot";
        await dm.GetByRole(AriaRole.Button, new() { Name = "Lore" }).ClickAsync();
        var newFactCard = dm.Locator("div.card:has(h5:text('New Fact'))");
        await newFactCard.Locator("input[placeholder='Summary']").FillAsync(revealedSummary);
        await newFactCard.Locator("select").SelectOptionAsync("KnowersOnly");
        await newFactCard.GetByRole(AriaRole.Button, new() { Name = "Add Fact" }).ClickAsync();
        await dm.GetByText(revealedSummary).WaitForAsync();

        await newFactCard.Locator("input[placeholder='Summary']").FillAsync(secretSummary);
        await newFactCard.Locator("select").SelectOptionAsync("DmOnly");
        await newFactCard.GetByRole(AriaRole.Button, new() { Name = "Add Fact" }).ClickAsync();
        await dm.GetByText(secretSummary).WaitForAsync();

        // ---- DM reveals the bridge fact to Aria alone, from the Grimoire. ----

        await dm.GotoAsync($"/campaigns/{campaignId}/grimoire");
        var bridgeCard = dm.Locator(".lore-card", new() { HasText = revealedSummary });
        await bridgeCard.WaitForAsync();
        await bridgeCard.GetByLabel("Actor to reveal to").SelectOptionAsync(new SelectOptionValue { Label = "Aria" });
        await bridgeCard.GetByRole(AriaRole.Button, new() { Name = "Reveal", Exact = true }).ClickAsync();
        await bridgeCard.GetByText("Known by: Aria").WaitForAsync();

        await Assertions.Expect(dm.GetByText(revealedSummary)).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByText(secretSummary)).ToBeVisibleAsync();

        // ---- Players' Grimoires: Aria's player knows, Bram's doesn't, nobody sees the secret. ----

        await player1.GotoAsync($"/campaigns/{campaignId}/grimoire");
        await Assertions.Expect(player1.GetByText(revealedSummary)).ToBeVisibleAsync();
        await Assertions.Expect(player1.GetByText(secretSummary)).ToHaveCountAsync(0);

        await player2.GotoAsync($"/campaigns/{campaignId}/grimoire");
        await Assertions.Expect(player2.GetByText(revealedSummary)).ToHaveCountAsync(0);
        await Assertions.Expect(player2.GetByText(secretSummary)).ToHaveCountAsync(0);

        // ---- Session notes: private stays between the author and the DM. ----

        await dm.GetByRole(AriaRole.Link, new() { Name = "Campaign", Exact = true }).ClickAsync();
        await dm.GetByRole(AriaRole.Button, new() { Name = "Sessions" }).ClickAsync();
        await dm.GetByRole(AriaRole.Button, new() { Name = "Plan New Session" }).ClickAsync();
        var sessionLink = dm.Locator("a[href*='/sessions/']").First;
        await sessionLink.WaitForAsync();
        var sessionUrl = await sessionLink.GetAttributeAsync("href");

        const string privateNote = "P4 aria secret theory";
        const string publicNote = "P4 party marching order";
        await player1.GotoAsync(sessionUrl!);
        await player1.GetByLabel("Note visibility").SelectOptionAsync("Private");
        var noteBox = player1.Locator("textarea[placeholder='Add a note...']");
        await noteBox.ClickAsync();
        // Keyboard typing (not Fill): every keystroke carries the full current value, so one
        // lost input event can't empty the server-side field the way a single Fill can.
        await player1.Keyboard.TypeAsync(privateNote);
        await player1.GetByRole(AriaRole.Button, new() { Name = "Add Note" }).ClickAsync();
        await player1.GetByText(privateNote).WaitForAsync();

        await player1.GetByLabel("Note visibility").SelectOptionAsync("Public");
        await player1.Locator("textarea[placeholder='Add a note...']").ClickAsync();
        await player1.Keyboard.TypeAsync(publicNote);
        await player1.GetByRole(AriaRole.Button, new() { Name = "Add Note" }).ClickAsync();
        await player1.GetByText(publicNote).WaitForAsync();

        await player2.GotoAsync(sessionUrl!);
        await Assertions.Expect(player2.GetByText(publicNote)).ToBeVisibleAsync();
        await Assertions.Expect(player2.GetByText(privateNote)).ToHaveCountAsync(0);

        await dm.GotoAsync(sessionUrl!);
        await Assertions.Expect(dm.GetByText(privateNote)).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByText(publicNote)).ToBeVisibleAsync();

        // ---- Recap: the DM writes, the players read. ----

        const string recap = "P4 Goblins attacked the bridge.";
        await dm.Locator("textarea[placeholder='What happened this session...']").ClickAsync();
        await dm.Keyboard.TypeAsync(recap);
        await dm.GetByRole(AriaRole.Button, new() { Name = "Save Recap" }).ClickAsync();
        await dm.GetByText("Recap saved.").WaitForAsync();

        await player1.GotoAsync(sessionUrl!);
        await player1.GetByText(recap).WaitForAsync();

        // ---- Payload half: secrets never crossed the wire to players. ----

        string player1Payload, player2Payload;
        lock (player1Frames) player1Payload = string.Join("\n", player1Frames);
        lock (player2Frames) player2Payload = string.Join("\n", player2Frames);
        Assert.DoesNotContain(secretSummary, player1Payload);
        Assert.DoesNotContain(secretSummary, player2Payload);
        Assert.DoesNotContain(privateNote, player2Payload);
    }

    private static Task LoginAsync(IPage page, string username, string password) =>
        LoginHelper.LoginAsync(page, username, password);
}
