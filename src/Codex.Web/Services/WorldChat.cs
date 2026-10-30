using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

/// <summary>A piece of the model's turn as it is written: more of its reply, or more of its reasoning (for models that share it).</summary>
public sealed record WorldChatDelta(bool Reasoning, string Text);

/// <summary>The turn under way, as the page shows it while the model works: the tools it has called (the last may still be
/// running), what it is writing, and its reasoning. Written by the turn, read by the page on another thread, so every read is a
/// copy taken under the lock.</summary>
public sealed class WorldChatLive
{
    private readonly object gate = new();
    private readonly List<WorldChatStep> steps = new();
    private readonly StringBuilder reply = new();
    private readonly StringBuilder reasoning = new();
    private string? aside;
    private bool thinking;

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public IReadOnlyList<WorldChatStep> Steps
    {
        get { lock (gate) return steps.ToList(); }
    }

    /// <summary>The reply as far as it has arrived.</summary>
    public string Reply
    {
        get { lock (gate) return reply.ToString(); }
    }

    /// <summary>The whole reasoning of the turn so far.</summary>
    public string Reasoning
    {
        get { lock (gate) return reasoning.ToString(); }
    }

    /// <summary>True while the model is reasoning rather than writing.</summary>
    public bool Thinking
    {
        get { lock (gate) return thinking; }
    }

    /// <summary>What the model said before its latest tool call ("Let me look…"): one line, replaced by the next, never a pile.</summary>
    public string? Aside
    {
        get { lock (gate) return aside; }
    }

    internal void Step(WorldChatStep step)
    {
        lock (gate)
        {
            if (steps.Contains(step)) return;
            // A tool call ends the round: the words before it were a preamble, not the answer.
            if (reply.Length > 0) aside = reply.ToString().Trim();
            reply.Clear();
            thinking = false;
            steps.Add(step);
        }
    }

    internal void Delta(WorldChatDelta delta)
    {
        lock (gate)
        {
            if (delta.Reasoning)
            {
                // Reasoning after a stretch of reply is a new round of thinking: a paragraph of its own.
                if (!thinking && reasoning.Length > 0) reasoning.Append("\n\n");
                reasoning.Append(delta.Text);
            }
            else
            {
                reply.Append(delta.Text);
            }

            thinking = delta.Reasoning;
        }
    }
}

/// <summary>
/// One world-building conversation (docs/WORLD_BUILDER.md): the DM talks with their own model, which reads the campaign through
/// tools and proposes related entries with <c>propose_changes</c>. A proposal is only held here, never saved, until the DM
/// applies it (<see cref="ApplyAsync"/>), which goes through <see cref="WorldBuilderService.SaveAsync"/>. The conversation is a
/// <see cref="WorldChatDocument"/>, stored after every turn and every decision, so the DM can leave it and come back. DM only.
/// </summary>
public sealed class WorldChat
{
    /// <summary>Most model round trips one attempt at a message may take (tool calls included), so a looping model can't run up a bill.
    /// A turn that ends empty gets one more attempt (<see cref="RunTurnAsync"/>), so a message costs at most twice this.</summary>
    public const int MaxToolRounds = 16;

    /// <summary>Longest tool result a step keeps for the DM to look at; the model got all of it.</summary>
    private const int MaxStepResult = 20_000;

    /// <summary>Longest reasoning a reply keeps: its last stretch.</summary>
    private const int MaxReasoning = 12_000;

    private readonly WorldBuilderService builder;
    private readonly Func<Task<IChatClient>> model;
    private readonly CampaignDocument campaign;
    private readonly CampaignAccess access;
    private readonly Func<WorldChatDocument, Task>? store;
    private readonly List<ChatMessage> history;
    // What the model looked up during the turn under way; it goes with the reply when the turn lands.
    private readonly List<WorldChatStep> trail = new();
    private CancellationTokenSource? running;

    /// <param name="model">The DM's own text model (<see cref="AiProviderService.ChatClientForAsync"/>), asked for at each message so a
    /// change in their AI settings counts at once.</param>
    /// <param name="document">The conversation to carry on; a new one when null.</param>
    /// <param name="store">Stores the conversation after each change; nothing is stored when null (tests).</param>
    public WorldChat(WorldBuilderService builder, Func<Task<IChatClient>> model, CampaignDocument campaign, CampaignAccess access,
        WorldChatDocument? document = null, Func<WorldChatDocument, Task>? store = null)
    {
        this.builder = builder;
        this.model = model;
        this.campaign = campaign;
        this.access = access;
        this.store = store;
        Document = document ?? new WorldChatDocument { CampaignId = campaign.Id, OwnerUserId = access.UserId };
        history = Document.History.Select(FromStored).ToList();
    }

    public WorldChatDocument Document { get; }
    public string CampaignId => campaign.Id;
    public string Id => Document.Id;
    public string Title => Document.Title;
    public IReadOnlyList<WorldChatLine> Lines => Document.Lines;

    /// <summary>What the model last proposed, waiting for the DM: entries they can untick before applying. Null when there is none.</summary>
    public WorldBundle? Proposal
    {
        get => Document.Proposal;
        private set => Document.Proposal = value;
    }

    public IReadOnlyList<string> ProposalWarnings => Document.ProposalWarnings;

    /// <summary>Counts the proposals the model has made from scratch; adding entries to one doesn't change it. The page keeps the
    /// DM's review decisions while it stays the same.</summary>
    public int ProposalEpoch => Document.ProposalEpoch;

    /// <summary>The entries of the waiting proposal the DM has decided on, by ref.</summary>
    public IReadOnlyCollection<string> Decided => Document.Decided;

    /// <summary>The turn under way; null when the model is not working.</summary>
    public WorldChatLive? Live { get; private set; }

    public bool IsBusy => Live != null;

    /// <summary>Raised when the turn under way moves on (a step starts or ends, more text arrives) and when it ends. Raised on the
    /// turn's thread: a page marshals it onto its own.</summary>
    public event Action? Changed;

    // The proposal as the model wrote it, so add_entries can put more entries beside them without the model resending the rest.
    private ProposalInput? Held
    {
        get => Document.ProposalInput is { } json ? JsonSerializer.Deserialize<ProposalInput>(json, ProposalJson) : null;
        set => Document.ProposalInput = value == null ? null : JsonSerializer.Serialize(value, ProposalJson);
    }

    /// <summary>Stops the turn under way; the conversation is left as it was before it, and the message goes back to the DM.</summary>
    public void Stop() => running?.Cancel();

    /// <summary>Sends the DM's message and runs the model, with its tool calls, until it answers. <paramref name="progress"/>
    /// hears each tool the model calls, when it starts and again when it ends; <paramref name="stream"/>, when given, hears the
    /// reply and the reasoning as they arrive. Both are also in <see cref="Live"/>, which <see cref="Changed"/> announces.</summary>
    public async Task SendAsync(string text, Action<WorldChatStep>? progress, CancellationToken ct, Action<WorldChatDelta>? stream = null)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
        var message = text.Trim();
        if (message.Length == 0) return;
        if (IsBusy) throw new InvalidOperationException("A turn is already under way.");

        // The model and the system prompt first: until the turn is under way (Live), nothing here needs undoing.
        var raw = await model();
        var system = new ChatMessage(ChatRole.System, AiPrompts.WorldChat(await builder.ChatRequestAsync(campaign, access)));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        running = stop;
        var live = Live = new WorldChatLive();
        var watch = Stopwatch.StartNew();
        var usage = new WorldChatUsage();
        ct = stop.Token;

        using var client = new ChatClientBuilder(raw)
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = MaxToolRounds;
                // An error in a tool goes back to the model as text it can act on, not as an exception here.
                f.IncludeDetailedErrors = true;
            })
            .Build();

        history.Add(new ChatMessage(ChatRole.User, message));
        var turnStart = Document.Lines.Count;
        trail.Clear();
        var heldProposal = Proposal;
        var heldWarnings = Document.ProposalWarnings;
        var heldInput = Document.ProposalInput;
        var heldEpoch = Document.ProposalEpoch;
        var heldDecided = Document.Decided.ToList();
        var heldTitle = Document.Title;
        Document.Lines.Add(new WorldChatLine(WorldChatSpeaker.Dm, message));
        if (!Document.Named && Document.Title.Length == 0) Document.Title = TitleFrom(message);
        var before = history.Count;
        Raise();

        void OnStep(WorldChatStep step)
        {
            live.Step(step);
            progress?.Invoke(step);
            Raise();
        }

        void OnDelta(WorldChatDelta delta)
        {
            live.Delta(delta);
            stream?.Invoke(delta);
            Raise();
        }

        try
        {
            var (reply, cutShort) = await RunTurnAsync(client, system, new ChatOptions { Tools = Tools(OnStep), Temperature = 0.8f },
                () => !ReferenceEquals(Proposal, heldProposal), OnDelta, usage, ct);
            var l = Localizer.For(Lang);
            // A proposal made and then not a word about it: the proposal is the news.
            if (reply.Length == 0 && !cutShort && !ReferenceEquals(Proposal, heldProposal))
            {
                reply = l["WorldChat.ProposalReady"];
                // The model gets its turn back too: a history that ends on tool results makes the next message two user turns in a row.
                history.Add(new ChatMessage(ChatRole.Assistant, ProposalReadyNote));
            }

            // Nothing even after the nudge: not an answer, so not a line in the log or a turn in the history. The message goes back
            // to the DM (the Undo below), instead of a placeholder the model would then read as its own last word.
            if (reply.Length == 0 && !cutShort) throw new LocalizedException("WorldChat.Error.Empty");
            usage.DurationMs = watch.ElapsedMilliseconds;
            var thought = live.Reasoning.Trim();
            Document.Lines.Add(new WorldChatLine(WorldChatSpeaker.Model, reply.Length > 0 ? reply : l["WorldChat.TooManySteps"], trail.ToList())
            {
                Reasoning = thought.Length == 0 ? null : thought.Length <= MaxReasoning ? thought : "…" + thought[^MaxReasoning..],
                Usage = usage
            });
            await SaveAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !(ex is LocalizedException) && !ct.IsCancellationRequested && SaysNoTools(ex))
        {
            Undo();
            throw new LocalizedException("WorldChat.Error.NoTools");
        }
        catch
        {
            // A stopped or failed turn leaves the conversation as it was, message included: the page hands the text back to the
            // DM to send again, so the log doesn't also keep a line for a message that was never answered.
            Undo();
            throw;
        }
        finally
        {
            running = null;
            Live = null;
            Raise();
        }

        void Undo()
        {
            history.RemoveRange(before - 1, history.Count - before + 1);
            Document.Lines.RemoveRange(turnStart, Document.Lines.Count - turnStart);
            // propose_changes holds its proposal the moment the model makes it, mid-turn. The turn is being forgotten, so
            // what it proposed goes with it, and what was waiting for the DM before it comes back.
            Proposal = heldProposal;
            Document.ProposalWarnings = heldWarnings;
            Document.ProposalInput = heldInput;
            Document.ProposalEpoch = heldEpoch;
            Document.Decided = heldDecided;
            // A name the DM gave meanwhile stays; the one the message gave goes with the message.
            if (!Document.Named) Document.Title = heldTitle;
        }
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>The name a chat gets from its first message: its first line, cut at a word near 60 characters.</summary>
    public static string TitleFrom(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? message.Trim();
        if (line.Length <= 60) return line;
        var cut = line[..60];
        var space = cut.LastIndexOf(' ');
        return (space > 30 ? cut[..space] : cut).TrimEnd(',', '.', ';', ':', ' ') + "…";
    }

    /// <summary>Renames the chat; the name stays even when the first message would name it otherwise.</summary>
    public async Task RenameAsync(string title)
    {
        var name = title.Trim();
        if (name.Length == 0) return;
        Document.Title = name.Length <= 120 ? name : name[..120];
        Document.Named = true;
        // The turn under way is adding to the history; it stores the chat, new name included, when it ends.
        if (!IsBusy) await SaveAsync();
    }

    /// <summary>Stores the conversation as it stands: the history, the log, and the proposal with the DM's ticks.</summary>
    public async Task SaveAsync()
    {
        Document.History = history.Select(ToStored).Where(m => m != null).Select(m => m!).ToList();
        Document.Waiting = Proposal?.Drafts.Count(d => d.Selected) ?? 0;
        if (store != null) await store(Document);
    }

    /// <summary>Records the DM's decision on an entry of the waiting proposal.</summary>
    public void Decide(string reference)
    {
        if (!Document.Decided.Contains(reference, StringComparer.OrdinalIgnoreCase)) Document.Decided.Add(reference);
    }

    private const string EmptyNudge = "(A note from the app, not from the game master: your last turn ended without a reply. " +
        "Answer the game master now, in one short message. If you meant to propose something, call propose_changes now instead of describing it.)";

    private const string CutOffNudge = "(A note from the app, not from the game master: your last turn was cut off by the length limit, so nothing came of it. " +
        "Keep your reply short. If you were proposing, call propose_changes now with at most 5 entries, each page short, and say the rest can follow in a later proposal.)";

    private const string ProposalReadyNote = "The proposal is ready for the game master to review.";

    private const string PreambleNudge = "(A note from the app, not from the game master: the game master has not seen anything from you yet, only your tool calls. " +
        "Reply to the game master now, in one short message, with what you found or decided. If you meant to propose something, call propose_changes now.)";

    /// <summary>Runs the model until it answers. A turn that ends with nothing to read (a small model that goes quiet after its
    /// tool results, or one whose proposal ran past the output limit) gets one nudge and one more go, so the DM isn't left
    /// guessing. So does one whose only words came before its last tool call (as likely a "let me look" as a summary): the
    /// nudge asks for the answer, and those words stand in only if it brings nothing. Returns an empty reply when there is
    /// nothing at all.</summary>
    private async Task<(string Reply, bool CutShort)> RunTurnAsync(IChatClient client, ChatMessage system, ChatOptions options, Func<bool> proposed,
        Action<WorldChatDelta> stream, WorldChatUsage usage, CancellationToken ct)
    {
        var earlier = string.Empty;
        for (var attempt = 0; ; attempt++)
        {
            var response = await StreamAsync(client, system, options, stream, ct);
            usage.Model ??= response.ModelId;
            usage.Rounds += Math.Max(1, response.Messages.Count(m => m.Role == ChatRole.Assistant));
            if (response.Usage is { } used)
            {
                usage.InputTokens = (usage.InputTokens ?? 0) + (used.InputTokenCount ?? 0);
                usage.OutputTokens = (usage.OutputTokens ?? 0) + (used.OutputTokenCount ?? 0);
            }

            var cutShort = KeepAnswered(response.Messages);
            var reply = FinalAnswer(response.Messages);
            if (reply.Length > 0 || cutShort) return (reply, cutShort);

            // Not when the round limit cut it off: what it said along the way is only preamble then.
            var said = response.Messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text.Trim()).LastOrDefault(t => t.Length > 0) ?? string.Empty;
            if (said.Length > 0) earlier = said;
            // A proposal is an answer of a kind: nudging a model that just made one invites it to make another over the first.
            if (attempt == 1 || proposed()) return (earlier, false);
            history.Add(new ChatMessage(ChatRole.User, response.FinishReason == ChatFinishReason.Length ? CutOffNudge
                : said.Length > 0 ? PreambleNudge : EmptyNudge));
        }
    }

    // The turn as one response, the way GetResponseAsync would give it, with each piece of text handed on as it arrives. The
    // system prompt is made afresh for each message from the campaign as it is now, so it is not part of the stored history.
    private async Task<ChatResponse> StreamAsync(IChatClient client, ChatMessage system, ChatOptions options, Action<WorldChatDelta> stream, CancellationToken ct)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(history.Prepend(system).ToList(), options, ct))
        {
            updates.Add(update);
            foreach (var content in update.Contents)
            {
                if (content is TextReasoningContent { Text: { Length: > 0 } thought }) stream(new WorldChatDelta(true, thought));
                else if (content is TextContent { Text: { Length: > 0 } words }) stream(new WorldChatDelta(false, words));
            }
        }

        return updates.ToChatResponse();
    }

    /// <summary>What the model said after its last tool result: its answer. What it said before each call is a preamble ("Got it,
    /// let me look"), often the same sentence each round, and run together with the answer (as <c>ChatResponse.Text</c> does) it
    /// is noise the DM has to read past.</summary>
    internal static string FinalAnswer(IList<ChatMessage> messages)
    {
        var last = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Contents.OfType<FunctionResultContent>().Any()) last = i;
        }

        return string.Concat(messages.Skip(last + 1).Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text)).Trim();
    }

    /// <summary>Adds the turn's messages to the history without a tool call the model made after its last round (the limit cut
    /// it off): OpenAI-compatible servers refuse a history where a call has no result. True when one was dropped.</summary>
    private bool KeepAnswered(IList<ChatMessage> messages)
    {
        var answered = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
        var dropped = false;
        foreach (var message in messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>().Where(c => !answered.Contains(c.CallId)).ToList())
            {
                message.Contents.Remove(call);
                dropped = true;
            }

            // A message with nothing in it (or only blank text, which Anthropic refuses) is not part of the conversation.
            if (message.Contents.Any(c => c is not TextContent text || !string.IsNullOrWhiteSpace(text.Text))) history.Add(message);
        }

        return dropped;
    }

    // ---- The history as the database keeps it ----

    /// <summary>A message as the database keeps it: its words, its tool calls and its tool results. Reasoning is not kept (the
    /// model does not need it back), so a message that held only reasoning is not kept at all.</summary>
    internal static WorldChatMessage? ToStored(ChatMessage message)
    {
        var text = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
        var calls = message.Contents.OfType<FunctionCallContent>()
            .Select(c => new WorldChatCall { Id = c.CallId, Name = c.Name, Arguments = JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions) })
            .ToList();
        var results = message.Contents.OfType<FunctionResultContent>()
            .Select(r => new WorldChatResult { CallId = r.CallId, Result = ResultText(r.Result) })
            .ToList();
        if (text.Length == 0 && calls.Count == 0 && results.Count == 0) return null;
        return new WorldChatMessage
        {
            Role = message.Role.Value,
            Text = text.Length == 0 ? null : text,
            Calls = calls.Count == 0 ? null : calls,
            Results = results.Count == 0 ? null : results
        };
    }

    // A tool's text comes back from the function wrapped as a JSON string; stored as JSON it would reach the model again quoted and
    // escaped, not as the text it read the first time.
    private static string ResultText(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions)
    };

    internal static ChatMessage FromStored(WorldChatMessage stored)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(stored.Text)) contents.Add(new TextContent(stored.Text));
        foreach (var call in stored.Calls ?? new List<WorldChatCall>())
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments, AIJsonUtilities.DefaultOptions);
            contents.Add(new FunctionCallContent(call.Id, call.Name, arguments));
        }

        foreach (var result in stored.Results ?? new List<WorldChatResult>())
        {
            contents.Add(new FunctionResultContent(result.CallId, result.Result));
        }

        return new ChatMessage(new ChatRole(stored.Role), contents);
    }

    // ---- What the DM does with a proposal ----

    /// <summary>Saves the proposal's ticked entries and tells the model what they became, so the conversation can build on them.</summary>
    public async Task<WorldBuildResult> ApplyAsync(CancellationToken ct = default)
    {
        var proposal = Proposal ?? throw new LocalizedException("WorldChat.Error.NoProposal");
        // Stored first: if another tab changed this chat (or applied this very proposal) since it was loaded, the save is refused
        // here, before anything is made, rather than making it all twice.
        await SaveAsync();
        WorldBuildResult result;
        try
        {
            // Each entry is kept in the chat the moment it is stored, so an apply cut short by anything (a failure, a closed tab,
            // a restart) leaves a chat that knows what it made: Apply again only adds the rest.
            result = await builder.SaveAsync(campaign, access, proposal, ct, SaveAsync);
        }
        catch (Exception ex) when (!IsStale(ex))
        {
            await SaveAsync();
            throw;
        }

        Proposal = null;
        Held = null;
        Document.ProposalWarnings = new List<string>();
        Document.Decided = new List<string>();

        var note = new StringBuilder("(A note from the app, not from the game master: the game master applied your proposal. These now exist, hidden from the players:");
        foreach (var page in result.Pages) note.Append($" page existing:{page.Slug} \"{page.Title}\";");
        foreach (var place in result.Places) note.Append($" place existing:{WorldBundle.PlacePrefix}{place.Id} \"{place.Name}\";");
        foreach (var actor in result.Actors) note.Append($" cast member \"{actor.Name}\";");
        foreach (var change in result.Changes) note.Append($" changed: the {change.Target switch { EditTarget.Place => "description of the place", EditTarget.Actor => "history of the cast member", EditTarget.Homebrew => "homebrew entry", _ => "text of the page" }} \"{change.Name}\" now reads as you wrote it;");
        foreach (var entry in result.Entries) note.Append($" homebrew existing:{WorldBundle.HomebrewPrefix}{entry.Type}/{entry.EntryId} \"{entry.Name}\";");
        foreach (var creature in result.Shortlisted) note.Append($" on the Bestiary: \"{creature.Name}\";");
        if (result.Retagged > 0) note.Append($" tags changed on {result.Retagged} page(s) or entries;");
        if (result.Facts.Count > 0) note.Append($" {result.Facts.Count} secret(s) in Lore;");
        history.Add(new ChatMessage(ChatRole.User, note.ToString().TrimEnd(';') + ".)"));
        Document.Lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.Applied", result.Count], icon: "circle-check") { Links = Links(result) });
        await SaveAsync();
        return result;
    }

    /// <summary>What an applied proposal made or changed, as links the conversation keeps.</summary>
    private static List<WorldChatLink> Links(WorldBuildResult result)
    {
        var links = new List<WorldChatLink>();
        links.AddRange(result.Pages.Select(p => new WorldChatLink { Kind = "page", Id = p.Slug, Name = p.Title }));
        links.AddRange(result.Actors.Select(a => new WorldChatLink { Kind = "actor", Id = a.Id, Name = a.Name }));
        // A new place's page is linked already, and the page leads to the place.
        links.AddRange(result.Places.Where(p => result.Pages.All(page => page.Id != p.WikiPageId)).Select(p => new WorldChatLink { Kind = "place", Id = p.Id, Name = p.Name }));
        links.AddRange(result.Entries.Select(e => new WorldChatLink { Kind = "homebrew", Id = $"{e.Type}/{e.EntryId}", Name = e.Name }));
        links.AddRange(result.Changes.Select(c => new WorldChatLink { Kind = "change-" + c.Target.ToString().ToLowerInvariant(), Id = c.Id, Name = c.Name }));
        links.AddRange(result.Shortlisted.Select(b => new WorldChatLink { Kind = "bestiary", Id = $"{b.EntryType}/{b.EntryId}", Name = b.Name }));
        if (result.Retagged > 0) links.Add(new WorldChatLink { Kind = "tags", Name = result.Retagged.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (result.Facts.Count > 0) links.Add(new WorldChatLink { Kind = "lore", Name = result.Facts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return links;
    }

    /// <summary>Drops the proposal without saving it, and tells the model so.</summary>
    public async Task DiscardAsync()
    {
        if (Proposal == null) return;
        Proposal = null;
        Held = null;
        Document.ProposalWarnings = new List<string>();
        Document.Decided = new List<string>();
        history.Add(new ChatMessage(ChatRole.User, "(A note from the app: the game master discarded your last proposal; nothing from it was saved.)"));
        Document.Lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.Discarded"], icon: "x"));
        await SaveAsync();
    }

    /// <summary>The errors that mean this copy of the chat is no longer the stored one: it changed in another tab, or was deleted.</summary>
    public static bool IsStale(Exception ex) => ex is LocalizedException { Key: "WorldChat.Error.ChangedElsewhere" or "WorldChat.Error.Gone" };

    private static string Lang => SupportedCultures.Normalize(System.Globalization.CultureInfo.CurrentUICulture.Name);

    // Ollama and LM Studio answer a tool list with an error naming tools when the model can't call them.
    private static bool SaysNoTools(Exception ex) =>
        ex.ToString().Contains("tool", StringComparison.OrdinalIgnoreCase)
        && (ex.ToString().Contains("support", StringComparison.OrdinalIgnoreCase) || ex.ToString().Contains("not allowed", StringComparison.OrdinalIgnoreCase));

    // ---- The tools ----

    private List<AITool> Tools(Action<WorldChatStep> progress)
    {
        // Every call is recorded as a step: what was asked, what came back, how long it took, and whether it failed.
        async Task<string> Step(string tool, object arguments, string? subject, int? count, Func<Task<string>> run)
        {
            var step = new WorldChatStep
            {
                Tool = tool,
                Subject = subject,
                Count = count,
                Arguments = JsonSerializer.Serialize(arguments, StepJson),
                Status = WorldChatStepStatus.Running,
                StartedAt = DateTime.UtcNow
            };
            trail.Add(step);
            progress(step);
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await run();
                step.Result = result.Length <= MaxStepResult ? result : result[..MaxStepResult] + "\n[…]";
                step.Rejected = result.StartsWith("Nothing was", StringComparison.Ordinal);
                step.Status = WorldChatStepStatus.Done;
                return result;
            }
            catch (Exception ex)
            {
                step.Error = ex.Message;
                step.Status = WorldChatStepStatus.Failed;
                throw;
            }
            finally
            {
                step.DurationMs = watch.ElapsedMilliseconds;
                progress(step);
            }
        }

        return new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("Words to look for in names and page texts; empty for an overview of the whole campaign.")] string? query) =>
                    Step("search_world", new { query }, string.IsNullOrWhiteSpace(query) ? null : query.Trim(), null,
                        () => builder.SearchWorldAsync(campaign, access, query)),
                "search_world",
                "Finds the campaign's wiki pages, places and cast members by name or text. Each line gives the key a proposal uses to link to it or change it."),
            AIFunctionFactory.Create(
                ([Description("The key of a page (existing:slug) or its title; or existing:place:<id> for a place's description, existing:actor:<id> for a cast member's history.")] string page) =>
                    Step("get_page", new { page }, page, null,
                        async () => await builder.PageAsync(access, page) ?? $"There is no page \"{page}\". Use search_world to find one."),
                "get_page",
                "Reads one wiki page in full, or a place's description or a cast member's history by their key, which is what an edit replaces."),
            AIFunctionFactory.Create(
                () => Step("list_places", new { }, null, null, () => builder.PlacesAsync(access)),
                "list_places",
                "Lists every region and the places inside it as a tree, with the key a new place uses as its parent."),
            AIFunctionFactory.Create(
                ([Description("A name, or part of it; empty lists the type's entries.")] string query,
                    [Description("A content type to search (\"monster\", \"spell\", \"magic-item\", \"equipment\"); empty searches creatures.")] string? type) =>
                    Step("search_library", new { query, type }, query, null, () => builder.SearchLibraryAsync(campaign, access, query, type)),
                "search_library",
                "Searches the rules Library, packs and the campaign's homebrew: for the creature whose statistics suit a person (\"stats\"), an entry to make a variant of (\"basedOn\"), " +
                "a homebrew entry to change (its key), or to see what already exists."),
            AIFunctionFactory.Create(
                ([Description("The entry as search_library lists it: type/id (\"spell/fireball\"), or a homebrew entry's key.")] string entry) =>
                    Step("get_entry", new { entry }, entry, null, () => builder.EntryAsync(campaign, access, entry)),
                "get_entry",
                "Reads one Library entry in full: its rules text, every field with its value, and its tags. Read an entry before you change it or make a variant of it."),
            AIFunctionFactory.Create(
                ([Description("The homebrew type, as listed in the instructions.")] string type) =>
                    Step("homebrew_fields", new { type }, type, null, () => builder.HomebrewFieldsAsync(campaign, access, type)),
                "homebrew_fields",
                "Lists the fields a homebrew entry of one type fills, for its \"properties\"."),
            AIFunctionFactory.Create(
                () => Step("list_tags", new { }, null, null, () => builder.TagsAsync(access)),
                "list_tags",
                "Lists the tags the campaign's wiki pages and homebrew entries use, with how many of each, so new tags match the old ones."),
            AIFunctionFactory.Create(
                (AdditionInput additions) =>
                    Step("add_entries", additions, null, additions.Entries?.Count ?? 0, () => AddEntriesAsync(additions)),
                "add_entries",
                "Adds entries to the proposal already waiting for the game master, without resending it: pass ONLY the new entries, each with a ref the proposal does not use yet. " +
                "They may use the refs already in the proposal as a place's parent, in \"links\" and as [[ref]]. The game master's ticks are kept. With no proposal waiting, it makes one."),
            AIFunctionFactory.Create(
                (ProposalInput proposal) =>
                    Step("propose_changes", proposal, null, proposal.Entries?.Count ?? 0, () => ProposeAsync(proposal)),
                "propose_changes",
                "Proposes related entries to add to the campaign or changes to what it has, all in one proposal, for the game master to review and apply. Saves nothing. " +
                "Returns errors to fix (then call it again with the whole corrected proposal) or confirms the proposal is ready for review.")
        };
    }

    // New entries beside the waiting proposal's: checked together (a new place can sit inside an old one), then what the game master
    // did with the old entries (ticks, the cast box) and what saving already made of them are put back on them.
    private async Task<string> AddEntriesAsync(AdditionInput additions)
    {
        var added = additions.Entries ?? new List<ProposalEntry>();
        if (Proposal is not { } before || Held is not { } input) return await ProposeAsync(new ProposalInput { Entries = added });

        // A ref the proposal already uses would replace that entry (and every link to it) without a word; a missing one would
        // match every other missing one. Either way the game master's proposal would change under them.
        var used = before.Drafts.Select(d => d.Ref).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clash = added.Select(e => e.Ref?.Trim() ?? string.Empty).Where(r => r.Length == 0 || used.Contains(r)).ToList();
        if (clash.Count > 0)
        {
            return "Nothing was added: every new entry needs its own \"ref\" that the proposal does not use yet (" +
                   (clash.Any(r => r.Length == 0) ? "one has none" : $"already used: {string.Join(", ", clash.Distinct(StringComparer.OrdinalIgnoreCase))}") +
                   $"). The proposal has: {string.Join(", ", used)}. Call add_entries again.";
        }

        var room = WorldBundleParser.MaxDrafts - before.Drafts.Count;
        if (added.Count > room)
        {
            return $"Nothing was added: the proposal already holds {before.Drafts.Count} entries and at most {WorldBundleParser.MaxDrafts} fit, so {Math.Max(room, 0)} more can. " +
                   "Add fewer, or tell the game master to apply the proposal first and ask for the rest after.";
        }

        var merged = new ProposalInput { Summary = input.Summary, Region = input.Region, Entries = (input.Entries ?? new List<ProposalEntry>()).Concat(added).ToList() };
        var result = await ProposeAsync(merged, "add_entries", $"only the new entries (the {input.Entries?.Count ?? 0} waiting ones come first when errors are numbered, so your first new entry is entries[{input.Entries?.Count ?? 0}])", fresh: false);
        if (!ReferenceEquals(Proposal, before) && Proposal != null) CarryOver(before, Proposal, decisions: true);
        return result;
    }

    /// <summary>Puts what belongs to the entries of <paramref name="before"/> on the same entries (by ref and kind) of
    /// <paramref name="after"/>. Always what saving already made of them (<see cref="WorldDraft.SavedAs"/>), so an apply that failed
    /// part way never makes those again. With <paramref name="decisions"/> (more entries added to the same proposal), also what the
    /// DM decided (ticks, the cast box, a new name) and, for an edit, the text it was proposed against: an edit whose target changed
    /// since stays refused. For a proposal made afresh, only an entry that is plainly the same one keeps what it became: the same
    /// name, or for an edit the same target and text (one with a new text should be applied, and is checked against the stamp).</summary>
    internal static void CarryOver(WorldBundle before, WorldBundle after, bool decisions)
    {
        foreach (var draft in after.Drafts)
        {
            if (before.Find(draft.Ref) is not { } old || old.Kind != draft.Kind) continue;
            if (decisions)
            {
                draft.Selected = old.Selected;
                draft.Cast = old.Cast;
                draft.Bestiary = old.Bestiary;
                draft.PageTitle = old.PageTitle;
                draft.Stats ??= old.Stats;
                if (!string.IsNullOrWhiteSpace(old.Name) && old.Name != old.OutlineName) draft.Name = old.Name;
                if (old.Kind == DraftKinds.Edit && string.Equals(old.Target, draft.Target, StringComparison.OrdinalIgnoreCase))
                {
                    draft.Before = old.Before;
                    draft.BeforeStamp = old.BeforeStamp;
                    draft.BeforeProperties = old.BeforeProperties;
                    draft.BeforeTags = old.BeforeTags;
                }
            }
            else if (old.SavedAs.Count == 0 || !SameEntry(old, draft))
            {
                continue;
            }

            draft.SavedAs = new Dictionary<string, string>(old.SavedAs);
        }

        static bool SameEntry(WorldDraft old, WorldDraft draft) => old.Kind switch
        {
            DraftKinds.Edit => string.Equals(old.Target, draft.Target, StringComparison.OrdinalIgnoreCase) && old.Body == draft.Body,
            DraftKinds.Tags => false, // tagging again changes nothing that is already tagged
            _ => new[] { old.OutlineName, old.Name }.Any(name => string.Equals(name.Trim(), draft.Name.Trim(), StringComparison.OrdinalIgnoreCase))
        };
    }

    private async Task<string> ProposeAsync(ProposalInput input, string tool = "propose_changes", string with = "the whole proposal", bool fresh = true)
    {
        var json = JsonSerializer.Serialize(input, ProposalJson);
        var check = await builder.CheckAsync(campaign, access, json);
        if (!check.Ok)
        {
            return $"Nothing was proposed. Fix these and call {tool} again with {with}:\n- " + string.Join("\n- ", check.Errors) +
                   (check.Warnings.Count > 0 ? "\nAlso worth fixing:\n- " + string.Join("\n- ", check.Warnings) : "");
        }

        Held = input;
        if (fresh)
        {
            Document.ProposalEpoch++;
            Document.Decided = new List<string>();
            // A proposal made again after an apply that failed part way: what was made then is not made twice.
            if (Proposal is { } waiting) CarryOver(waiting, check.Bundle, decisions: false);
        }

        Proposal = check.Bundle;
        Document.ProposalWarnings = check.Warnings.ToList();
        return $"The proposal ({check.Bundle.Drafts.Count} entries) is ready for the game master to review and apply; nothing is saved yet. Tell them in a sentence or two what it adds." +
               (check.Warnings.Count > 0 ? "\nWarnings (fix them with a new proposal if they matter):\n- " + string.Join("\n- ", check.Warnings) : "");
    }

    private static readonly JsonSerializerOptions ProposalJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // A step's arguments as the DM reads them: indented, nothing escaped that needn't be.
    private static readonly JsonSerializerOptions StepJson = new(ProposalJson) { WriteIndented = true };
}

/// <summary>What <c>propose_changes</c> takes; the model reads this shape as the tool's schema.</summary>
public sealed class ProposalInput
{
    [JsonPropertyName("summary"), Description("What the proposal adds or changes, in one sentence.")]
    public string? Summary { get; set; }

    [JsonPropertyName("region"), Description("The region new top-level places go into: an existing region's name, or a new one's. Empty: the campaign's first region.")]
    public string? Region { get; set; }

    [JsonPropertyName("entries"), Description("Every entry of the proposal; at most 30.")]
    public List<ProposalEntry>? Entries { get; set; }
}

/// <summary>What <c>add_entries</c> takes: only the entries to put beside those already waiting.</summary>
public sealed class AdditionInput
{
    [JsonPropertyName("entries"), Description("ONLY the new entries (a few at a time), each with a ref the waiting proposal does not use.")]
    public List<ProposalEntry>? Entries { get; set; }
}

/// <summary>One entry of a proposal; see <see cref="WorldBundleParser.ParseProposal"/> for how each field is read.</summary>
public sealed class ProposalEntry
{
    [JsonPropertyName("ref"), Description("A short id other entries use: \"p1\", \"n2\".")]
    public string? Ref { get; set; }

    [JsonPropertyName("kind"), Description("place, npc, wiki, fact, homebrew, edit, tags or bestiary.")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("text"), Description("The page's text in markdown (a fact's details; a homebrew entry's rules text), in full sentences under short headings, usually 150 to 350 words: written for a game master to run from, never clipped notes. Link entries as [[ref]] and pages as [[existing:slug]].")]
    public string? Text { get; set; }

    [JsonPropertyName("pitch"), Description("One or two plain sentences, at most 300 characters, saying what the entry is: shown in lists and on the review card, and as a place's short description.")]
    public string? Pitch { get; set; }

    [JsonPropertyName("target"), Description("For an edit: the key of what to change, as search_world, get_page, list_places or search_library show it: existing:<slug> (a page's text), existing:place:<id> (a place's short description), existing:actor:<id> (a cast member's history) or existing:homebrew:<type>/<id> (a homebrew entry). For a page, place or cast member the \"text\" is then the COMPLETE new text, replacing the old.")]
    public string? Target { get; set; }

    [JsonPropertyName("rename"), Description("For an edit of a homebrew entry: its new name, when it should get one.")]
    public string? Rename { get; set; }

    [JsonPropertyName("links"), Description("Refs and existing:slug keys this entry is about (a fact's subjects).")]
    public List<string>? Links { get; set; }

    [JsonPropertyName("placeKind"), Description("For a place: area, settlement, district or site.")]
    public string? PlaceKind { get; set; }

    [JsonPropertyName("parent"), Description("For a place: the ref of the place entry it is inside, or existing:place:<id>.")]
    public string? Parent { get; set; }

    [JsonPropertyName("cast"), Description("For an npc: true to add them to the cast too.")]
    public bool? Cast { get; set; }

    [JsonPropertyName("stats"), Description("For an npc who fights: the Library creature whose statistics suit them, as search_library names it.")]
    public string? Stats { get; set; }

    [JsonPropertyName("pageKind"), Description("For a wiki page: lore, faction or quest.")]
    public string? PageKind { get; set; }

    [JsonPropertyName("type"), Description("For a homebrew entry: its type.")]
    public string? Type { get; set; }

    [JsonPropertyName("basedOn"), Description("For a homebrew entry that is a variant of a Library entry: that entry as search_library lists it (type/id). Its fields and text are the starting point; give only the \"properties\" that differ, and the complete \"text\" if it changes.")]
    public string? BasedOn { get; set; }

    [JsonPropertyName("bestiary"), Description("For a homebrew creature: true to put it on the Bestiary shortlist.")]
    public bool? Bestiary { get; set; }

    [JsonPropertyName("entry"), Description("For a bestiary entry: the Library creature to put on the Bestiary shortlist, as search_library lists it (type/id).")]
    public string? Entry { get; set; }

    [JsonPropertyName("tags"), Description("For a new page or homebrew entry: two to five short lower-case tags. For an edit of a homebrew entry: its COMPLETE new tags.")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("targets"), Description("For a tags entry: the keys of the wiki pages (existing:<slug>) and homebrew entries (existing:homebrew:<type>/<id>) to tag.")]
    public List<string>? Targets { get; set; }

    [JsonPropertyName("add"), Description("For a tags entry: the tags to put on every target.")]
    public List<string>? Add { get; set; }

    [JsonPropertyName("remove"), Description("For a tags entry: the tags to take off every target.")]
    public List<string>? Remove { get; set; }

    [JsonPropertyName("properties"), Description("For a homebrew entry: its fields, by the keys homebrew_fields lists. For an edit of one: only the fields that change.")]
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

/// <summary>Keeps the DM's world-building conversations open for as long as their connection lasts (a turn keeps running while
/// they look at another chat), loading each from the database the first time it is opened. DM only.</summary>
public sealed class WorldChatSessions(WorldBuilderService builder, AiProviderService providers, IWorldChatRepository repository)
{
    private readonly Dictionary<string, WorldChat> open = new();

    /// <summary>The DM's chats in the campaign, most recent first.</summary>
    public async Task<IReadOnlyList<WorldChatSummary>> ListAsync(CampaignAccess access)
    {
        DmOnly(access);
        return await repository.ListMineAsync(access);
    }

    /// <summary>The chat <paramref name="id"/>, as it was left; null when it is not one of the DM's.</summary>
    public async Task<WorldChat?> OpenAsync(CampaignDocument campaign, CampaignAccess access, string id)
    {
        DmOnly(access);
        if (open.TryGetValue(id, out var chat) && chat.CampaignId == campaign.Id) return chat;
        var document = await repository.GetMineAsync(id, access);
        if (document == null) return null;
        return open[id] = Make(campaign, access, document);
    }

    /// <summary>A new chat, stored with its first message.</summary>
    public WorldChat New(CampaignDocument campaign, CampaignAccess access)
    {
        DmOnly(access);
        return Make(campaign, access, null);
    }

    /// <summary>True while a turn of the chat <paramref name="id"/> is under way, for the chats list.</summary>
    public bool IsBusy(string id) => open.TryGetValue(id, out var chat) && chat.IsBusy;

    public async Task<bool> DeleteAsync(CampaignAccess access, string id)
    {
        DmOnly(access);
        if (open.Remove(id, out var chat)) chat.Stop();
        return await repository.TryDeleteAsync(id, access);
    }

    private WorldChat Make(CampaignDocument campaign, CampaignAccess access, WorldChatDocument? document)
    {
        WorldChat? made = null;
        made = new WorldChat(builder, () => providers.ChatClientForAsync(access.UserId), campaign, access, document, async doc =>
        {
            switch (await repository.TrySaveAsync(doc, access))
            {
                case ChatSave.Saved:
                    open[doc.Id] = made!;
                    return;
                case ChatSave.Changed:
                    // This copy is out of date: forget it, so opening the chat again loads what is stored.
                    open.Remove(doc.Id);
                    throw new LocalizedException("WorldChat.Error.ChangedElsewhere");
                case ChatSave.Gone:
                    open.Remove(doc.Id);
                    throw new LocalizedException("WorldChat.Error.Gone");
                default:
                    throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }
        });
        return made;
    }

    private static void DmOnly(CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }
}
