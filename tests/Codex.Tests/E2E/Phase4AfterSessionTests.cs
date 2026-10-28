using Codex.Persistence;
using Codex.Web;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// After and between sessions:
///   1. The DM authors lore and reveals one fact to a single character.
///   2. That character's player sees it; the other player doesn't; nobody but the DM ever receives
///      the DM-only fact - asserted on the DOM and the circuit payload.
///   3. A player adds a private and a party note; the other player sees only the party one; the DM both.
///   4. The DM writes a recap; players can read it.
/// </summary>
[Collection("RavenDb")]
public class Phase4AfterSessionTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public Phase4AfterSessionTests(AppFixture app) => _app = app;

    [Fact]
    public async Task AfterAndBetweenSessions_Async()
    {
        const string dmPassword = "Correct-Horse-1";
        const string playerPassword = "Correct-Horse-2";
        var dmId = "p4-dm-" + Guid.NewGuid();
        var player1Id = "p4-p1-" + Guid.NewGuid();
        var player2Id = "p4-p2-" + Guid.NewGuid();

        await _app.SeedUserAsync(dmId, "dm_" + dmId, dmPassword, "Player");
        await _app.SeedUserAsync(player1Id, "aria_" + player1Id, playerPassword, "Player");
        await _app.SeedUserAsync(player2Id, "bram_" + player2Id, playerPassword, "Player");

        var contextOptions = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await _app.Browser.NewContextAsync(contextOptions);
        await using var player1Context = await _app.Browser.NewContextAsync(contextOptions);
        await using var player2Context = await _app.Browser.NewContextAsync(contextOptions);

        var dm = await dmContext.NewPageAsync();
        var player1 = await player1Context.NewPageAsync();
        var player2 = await player2Context.NewPageAsync();

        var player1Frames = new List<string>();
        var player2Frames = new List<string>();
        player1.WebSocket += (_, ws) => ws.FrameReceived += (_, frame) => { if (frame.Text != null) lock (player1Frames) player1Frames.Add(frame.Text); };
        player2.WebSocket += (_, ws) => ws.FrameReceived += (_, frame) => { if (frame.Text != null) lock (player2Frames) player2Frames.Add(frame.Text); };

        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, dmPassword);
        await E2EHelpers.LoginAsync(player1, "aria_" + player1Id, playerPassword);
        await E2EHelpers.LoginAsync(player2, "bram_" + player2Id, playerPassword);

        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Between Sessions");
        var inviteUrl = await E2EHelpers.ReadInviteLinkAsync(dm, campaignId);
        await E2EHelpers.JoinAsync(player1, inviteUrl);
        await E2EHelpers.JoinAsync(player2, inviteUrl);

        var ariaActorId = "p4-aria-" + Guid.NewGuid();
        var bramActorId = "p4-bram-" + Guid.NewGuid();
        await _app.SeedActorsAsync(new[]
        {
            new TestSeedActor { Id = ariaActorId, CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = player1Id, Visibility = ActorVisibility.Known, Hp = 12, HpMax = 12 },
            new TestSeedActor { Id = bramActorId, CampaignId = campaignId, Name = "Bram", Kind = ActorKind.PlayerCharacter, OwnerUserId = player2Id, Visibility = ActorVisibility.Known, Hp = 10, HpMax = 10 }
        });

        // ---- The DM authors two facts, one to be revealed, one DM-only. ----
        const string revealedSummary = "P4 The Bridge Is Trapped";
        const string secretSummary = "P4 Secret Villain Plot";
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "lore");
        await dm.GetByTestId("new-fact-summary").FillAsync(revealedSummary);
        await dm.GetByTestId("new-fact-visibility").SelectOptionAsync("KnowersOnly");
        await dm.GetByTestId("new-fact-add").ClickAsync();
        await dm.GetByText(revealedSummary).WaitForAsync();

        await dm.GetByTestId("new-fact-summary").FillAsync(secretSummary);
        await dm.GetByTestId("new-fact-visibility").SelectOptionAsync("DmOnly");
        await dm.GetByTestId("new-fact-add").ClickAsync();
        await dm.GetByText(secretSummary).WaitForAsync();

        // ---- Reveal the bridge fact to Aria alone. ----
        var bridgeCard = dm.GetByTestId("fact-card").Filter(new() { HasText = revealedSummary });
        await bridgeCard.GetByLabel("Reveal to").SelectOptionAsync(new SelectOptionValue { Label = "Aria" });
        await bridgeCard.GetByRole(AriaRole.Button, new() { Name = "Reveal to character" }).ClickAsync();
        await bridgeCard.GetByText("Known by: Aria").WaitForAsync();

        // ---- Players' lore: Aria's player knows, Bram's doesn't, nobody sees the secret. ----
        await E2EHelpers.GoToSectionAsync(player1, campaignId, "lore");
        await Assertions.Expect(player1.GetByText(revealedSummary)).ToBeVisibleAsync();
        await Assertions.Expect(player1.GetByText(secretSummary)).ToHaveCountAsync(0);

        await E2EHelpers.GoToSectionAsync(player2, campaignId, "lore");
        await player2.GetByText("Lore").First.WaitForAsync();
        await Assertions.Expect(player2.GetByText(revealedSummary)).ToHaveCountAsync(0);
        await Assertions.Expect(player2.GetByText(secretSummary)).ToHaveCountAsync(0);

        // ---- Session notes: private stays between the author and the DM. ----
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "sessions");
        await dm.GetByTestId("new-session-title").FillAsync("Session one");
        await dm.GetByTestId("new-session-add").ClickAsync();
        var sessionLink = dm.GetByTestId("session-list").GetByRole(AriaRole.Link, new() { Name = "Session one" });
        await sessionLink.WaitForAsync();
        var sessionUrl = await sessionLink.GetAttributeAsync("href");

        const string privateNote = "P4 aria secret theory";
        const string publicNote = "P4 party marching order";
        await E2EHelpers.GotoInteractiveAsync(player1, sessionUrl!);
        await player1.Locator("#note-visibility").SelectOptionAsync("Private");
        await player1.GetByTestId("new-note").ClickAsync();
        await player1.Keyboard.TypeAsync(privateNote);
        await player1.GetByRole(AriaRole.Button, new() { Name = "Add note" }).ClickAsync();
        await player1.GetByText(privateNote).WaitForAsync();

        await player1.Locator("#note-visibility").SelectOptionAsync("Public");
        await player1.GetByTestId("new-note").ClickAsync();
        await player1.Keyboard.TypeAsync(publicNote);
        await player1.GetByRole(AriaRole.Button, new() { Name = "Add note" }).ClickAsync();
        await player1.GetByText(publicNote).WaitForAsync();

        await E2EHelpers.GotoInteractiveAsync(player2, sessionUrl!);
        await Assertions.Expect(player2.GetByText(publicNote)).ToBeVisibleAsync();
        await Assertions.Expect(player2.GetByText(privateNote)).ToHaveCountAsync(0);

        await E2EHelpers.GotoInteractiveAsync(dm, sessionUrl!);
        await Assertions.Expect(dm.GetByText(privateNote)).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByText(publicNote)).ToBeVisibleAsync();

        // ---- Recap: the DM writes, the players read. ----
        const string recap = "P4 Goblins attacked the bridge.";
        await dm.Locator("#recap").ClickAsync();
        await dm.Keyboard.TypeAsync(recap);
        await dm.GetByRole(AriaRole.Button, new() { Name = "Save recap" }).ClickAsync();
        await dm.GetByText("Recap saved.").WaitForAsync();
        // After a save the box shows what the server stored. If keystrokes were lost on the way (typed faster than the circuit
        // answers), the failure is here and says so, instead of 30 seconds later as a player who never sees the text.
        await Assertions.Expect(dm.Locator("#recap")).ToHaveValueAsync(recap);

        await E2EHelpers.GotoInteractiveAsync(player1, sessionUrl!);
        await player1.GetByText(recap).WaitForAsync();

        // The box is not re-sent its value as the DM types (that dropped keystrokes under load), so a change the page makes to it
        // itself has to arrive another way: "Pre-fill from events" replaces what is there.
        await dm.Locator("#recap").FillAsync("P4 scratch text");
        await dm.GetByRole(AriaRole.Button, new() { Name = "Pre-fill from events" }).ClickAsync();
        await Assertions.Expect(dm.Locator("#recap")).Not.ToHaveValueAsync("P4 scratch text");

        // ---- Payload half: secrets never crossed the wire to players. ----
        string player1Payload, player2Payload;
        lock (player1Frames) player1Payload = string.Join("\n", player1Frames);
        lock (player2Frames) player2Payload = string.Join("\n", player2Frames);
        Assert.DoesNotContain(secretSummary, player1Payload);
        Assert.DoesNotContain(secretSummary, player2Payload);
        Assert.DoesNotContain(privateNote, player2Payload);
    }
}
