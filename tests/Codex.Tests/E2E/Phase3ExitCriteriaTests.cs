using System.Net.Http.Json;
using Codex.Persistence;
using Codex.Web;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The table loop, end to end:
///
///   1. The DM creates a campaign and invites two players.
///   2. The DM starts a session and runs three rounds against three goblins.
///   3. Each player sees their own HP change live.
///   4. Players never receive goblin HP numbers or the hidden NPC - asserted on the rendered DOM
///      and on the circuit payload.
///   5. Poisoned + Stunned coexist and expire independently.
///   6. The session log contains every command.
///   7. A player whose Table page was already open attaches when the DM starts the table.
///
/// One scenario, one Fact: the setup (a real Codex.Web process plus a headless browser driving
/// three separate logged-in circuits) is too expensive to pay seven times over.
/// </summary>
[Collection("RavenDb")]
public class Phase3ExitCriteriaTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public Phase3ExitCriteriaTests(AppFixture app) => _app = app;

    [Fact]
    public async Task TableLoop_FullScenario_Async()
    {
        const string dmPassword = "Correct-Horse-1";
        const string playerPassword = "Correct-Horse-2";
        var dmId = "e2e-dm-" + Guid.NewGuid();
        var player1Id = "e2e-p1-" + Guid.NewGuid();
        var player2Id = "e2e-p2-" + Guid.NewGuid();

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

        // Capture every SignalR frame sent to Player 1 from the moment their page exists (criterion 4).
        var player1Frames = new List<string>();
        player1.WebSocket += (_, ws) => ws.FrameReceived += (_, frame) =>
        {
            if (frame.Text != null)
            {
                lock (player1Frames) player1Frames.Add(frame.Text);
            }
        };

        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, dmPassword);
        await E2EHelpers.LoginAsync(player1, "aria_" + player1Id, playerPassword);
        await E2EHelpers.LoginAsync(player2, "bram_" + player2Id, playerPassword);

        // ---- Criterion 1 ----
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Goblin Ambush");
        var inviteUrl = await E2EHelpers.ReadInviteLinkAsync(dm, campaignId);
        await E2EHelpers.JoinAsync(player1, inviteUrl);
        await E2EHelpers.JoinAsync(player2, inviteUrl);
        await Assertions.Expect(player1.GetByTestId("my-role")).ToHaveTextAsync("Player");
        await Assertions.Expect(player2.GetByTestId("my-role")).ToHaveTextAsync("Player");

        const int goblinMaxHp = 15;
        const int ariaMaxHp = 12;
        const int bramMaxHp = 10;
        var goblin1Id = "goblin-1-" + Guid.NewGuid();
        var goblin2Id = "goblin-2-" + Guid.NewGuid();
        var goblin3Id = "goblin-3-" + Guid.NewGuid();
        var hiddenNpcId = "hidden-npc-" + Guid.NewGuid();
        var ariaActorId = "aria-actor-" + Guid.NewGuid();
        var bramActorId = "bram-actor-" + Guid.NewGuid();
        const string hiddenNpcName = "Shadow Ambusher";

        await _app.SeedActorsAsync(new[]
        {
            new TestSeedActor { Id = goblin1Id, CampaignId = campaignId, Name = "Goblin 1", Kind = ActorKind.Monster, Visibility = ActorVisibility.Known, Hp = goblinMaxHp, HpMax = goblinMaxHp },
            new TestSeedActor { Id = goblin2Id, CampaignId = campaignId, Name = "Goblin 2", Kind = ActorKind.Monster, Visibility = ActorVisibility.Known, Hp = goblinMaxHp, HpMax = goblinMaxHp },
            new TestSeedActor { Id = goblin3Id, CampaignId = campaignId, Name = "Goblin 3", Kind = ActorKind.Monster, Visibility = ActorVisibility.Known, Hp = goblinMaxHp, HpMax = goblinMaxHp },
            new TestSeedActor { Id = hiddenNpcId, CampaignId = campaignId, Name = hiddenNpcName, Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Hidden, Hp = 20, HpMax = 20 },
            new TestSeedActor { Id = ariaActorId, CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = player1Id, Visibility = ActorVisibility.Known, Hp = ariaMaxHp, HpMax = ariaMaxHp },
            new TestSeedActor { Id = bramActorId, CampaignId = campaignId, Name = "Bram", Kind = ActorKind.PlayerCharacter, OwnerUserId = player2Id, Visibility = ActorVisibility.Known, Hp = bramMaxHp, HpMax = bramMaxHp }
        });

        // ---- Criterion 7: players open the Table before the DM starts it. ----
        await E2EHelpers.GoToSectionAsync(player1, campaignId, "table");
        await E2EHelpers.GoToSectionAsync(player2, campaignId, "table");
        await Assertions.Expect(player1.GetByText("Waiting for the DM")).ToBeVisibleAsync();

        // ---- Criterion 2 (part 1): the DM starts the table. ----
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "table");
        await dm.GetByTestId("start-table").ClickAsync();
        await dm.GetByTestId("initiative-header").WaitForAsync();

        // Without reloading, both players' pages attach to the now-running table.
        await Assertions.Expect(player1.GetByTestId("initiative-header")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Assertions.Expect(player2.GetByTestId("initiative-header")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await SetInitiativeAsync(dm, "Aria", 20);
        await SetInitiativeAsync(dm, "Goblin 1", 18);
        await SetInitiativeAsync(dm, "Bram", 15);
        await SetInitiativeAsync(dm, "Goblin 2", 12);
        await SetInitiativeAsync(dm, hiddenNpcName, 10);
        await SetInitiativeAsync(dm, "Goblin 3", 8);

        var goblin1Row = dm.Locator($"[data-testid='participant-row'][data-actor-id='{goblin1Id}']");
        var ariaRowOnDm = dm.Locator($"[data-testid='participant-row'][data-actor-id='{ariaActorId}']");

        const int goblin1DamagedHp = 6;
        await goblin1Row.GetByTestId("damage-input").FillAsync((goblinMaxHp - goblin1DamagedHp).ToString());
        await goblin1Row.GetByTestId("damage-btn").ClickAsync();
        await Assertions.Expect(goblin1Row.GetByTestId("hp-badge")).ToHaveTextAsync($"{goblin1DamagedHp}/{goblinMaxHp} HP");

        // ---- Criterion 5 ----
        await AddConditionAsync(ariaRowOnDm, "Poisoned", rounds: 3);
        await AddConditionAsync(ariaRowOnDm, "Stunned", rounds: 1);
        await Assertions.Expect(ariaRowOnDm.GetByTestId("condition-badge").Filter(new() { HasText = "Poisoned" })).ToContainTextAsync("Poisoned (3)");
        await Assertions.Expect(ariaRowOnDm.GetByTestId("condition-badge").Filter(new() { HasText = "Stunned" })).ToContainTextAsync("Stunned (1)");

        // ---- Criterion 3 ----
        const int ariaDamagedHp = 8;
        await ariaRowOnDm.GetByTestId("damage-input").FillAsync((ariaMaxHp - ariaDamagedHp).ToString());
        await ariaRowOnDm.GetByTestId("damage-btn").ClickAsync();

        var ariaRowOnPlayer1 = player1.Locator($"[data-testid='participant-row'][data-actor-id='{ariaActorId}']");
        await Assertions.Expect(ariaRowOnPlayer1.GetByTestId("hp-badge")).ToHaveTextAsync($"{ariaDamagedHp}/{ariaMaxHp} HP", new() { Timeout = 10_000 });

        // ---- Criterion 2 (part 2): three full rounds. ----
        for (var round = 1; round <= 3; round++)
        {
            for (var turn = 0; turn < 6; turn++)
            {
                await dm.GetByTestId("next-turn-btn").ClickAsync();
            }

            await Assertions.Expect(dm.GetByTestId("initiative-header")).ToHaveTextAsync($"Initiative - Round {round + 1}");
        }

        await Assertions.Expect(ariaRowOnDm.GetByTestId("condition-badge")).ToHaveCountAsync(0);

        // ---- Criterion 4 ----
        var goblin1RowOnPlayer2 = player2.Locator($"[data-testid='participant-row'][data-actor-id='{goblin1Id}']");
        var goblin1HpTextForPlayer = await goblin1RowOnPlayer2.GetByTestId("hp-badge").TextContentAsync();
        Assert.DoesNotContain($"{goblin1DamagedHp}/{goblinMaxHp}", goblin1HpTextForPlayer);
        Assert.Contains(goblin1HpTextForPlayer, new[] { "Healthy", "Bloodied", "Critical", "Down", "Unknown" });

        var player1Html = await player1.ContentAsync();
        var player2Html = await player2.ContentAsync();
        Assert.DoesNotContain(hiddenNpcName, player1Html);
        Assert.DoesNotContain(hiddenNpcName, player2Html);
        Assert.DoesNotContain(hiddenNpcId, player1Html);
        Assert.DoesNotContain(hiddenNpcId, player2Html);
        await Assertions.Expect(player1.Locator($"[data-actor-id='{hiddenNpcId}']")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.Locator($"[data-actor-id='{hiddenNpcId}']")).ToHaveCountAsync(1);

        List<string> framesSnapshot;
        lock (player1Frames) framesSnapshot = new List<string>(player1Frames);
        Assert.NotEmpty(framesSnapshot);
        Assert.DoesNotContain(framesSnapshot, f => f.Contains(hiddenNpcName));
        Assert.DoesNotContain(framesSnapshot, f => f.Contains(hiddenNpcId));
        Assert.DoesNotContain(framesSnapshot, f => f.Contains($"{goblin1DamagedHp}/{goblinMaxHp}"));

        // ---- Criterion 6 ----
        var events = await _app.Http.GetFromJsonAsync<List<string>>($"/test/inspect/session-events/{campaignId}");
        Assert.NotNull(events);
        Assert.Equal(6, events!.Count(e => e == "Initiative"));
        Assert.Equal(2, events.Count(e => e == "Damage"));
        Assert.Equal(2, events.Count(e => e == "StatusEffect"));
        Assert.Equal(18, events.Count(e => e == "TurnAdvance"));

        // The DM ends the table; players' pages return to the waiting state without a reload.
        await dm.GetByTestId("end-table").ClickAsync();
        await Assertions.Expect(player1.GetByText("Waiting for the DM")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    private static async Task SetInitiativeAsync(IPage dm, string actorName, int roll)
    {
        var select = dm.GetByTestId("add-to-initiative-select");
        var setBtn = dm.GetByTestId("set-initiative-btn");

        await select.Locator("option", new() { HasText = actorName })
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20_000 });

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await select.SelectOptionAsync(new SelectOptionValue { Label = actorName });
            try
            {
                await Assertions.Expect(setBtn).ToBeEnabledAsync(new() { Timeout = 3_000 });
                break;
            }
            catch (PlaywrightException) when (attempt < 5)
            {
                await select.SelectOptionAsync(new SelectOptionValue { Label = "Choose someone…" });
            }
        }

        await dm.GetByTestId("manual-initiative-input").FillAsync(roll.ToString());
        await setBtn.ClickAsync();
    }

    private static async Task AddConditionAsync(ILocator participantRow, string effectId, int rounds)
    {
        await participantRow.GetByTestId("condition-input").FillAsync(effectId);
        await participantRow.GetByTestId("condition-rounds-input").FillAsync(rounds.ToString());
        await participantRow.GetByTestId("condition-add-btn").ClickAsync();
    }
}
