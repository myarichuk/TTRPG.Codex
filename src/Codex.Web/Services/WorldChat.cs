using System.Diagnostics;
using System.Text.Json;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

/// <summary>
/// One world-building conversation (docs/WORLD_BUILDER.md): the DM talks with their own model, which reads the campaign through
/// tools and proposes related entries with <c>propose_changes</c>. A proposal is only held here, never saved, until the DM
/// applies it (<see cref="ApplyAsync"/>), which goes through <see cref="WorldBuilderService.SaveAsync"/>. The conversation is a
/// <see cref="WorldChatDocument"/>, stored after every turn and every decision, so the DM can leave it and come back. DM only.
/// </summary>
public sealed partial class WorldChat
{
    /// <summary>Most model round trips one attempt at a message may take (tool calls included), so a looping model can't run up a bill.
    /// A turn that ends empty gets one more attempt (<see cref="RunTurnAsync"/>), so a message costs at most twice this.</summary>
    public const int MaxToolRounds = 16;

    /// <summary>Longest tool result a step keeps for the DM to look at; the model got all of it.</summary>
    private const int MaxStepResult = 20_000;

    /// <summary>Longest reasoning a reply keeps: its last stretch.</summary>
    private const int MaxReasoning = 12_000;

    /// <summary>Longest reply the model may write in one go; a proposal that runs past it gets the cut-off nudge.</summary>
    private const int MaxReplyTokens = 8_192;

    /// <summary>Tool results from before this many of the DM's messages back reach the model as a line saying what they were.</summary>
    private const int FreshMessages = 3;

    /// <summary>Tool results up to this long are sent again whole, however old: a stub would save next to nothing.</summary>
    private const int MaxOldResult = 1_200;

    /// <summary>How long an apply under way (<see cref="WorldChatDocument.ApplyingSince"/>) keeps another tab from applying: longer
    /// than any apply takes, short enough that one cut off by a crash does not hold the proposal for long.</summary>
    private static readonly TimeSpan ApplyClaim = TimeSpan.FromMinutes(10);

    private readonly WorldBuilderService builder;
    private readonly Func<Task<IChatClient>> model;
    private readonly CampaignDocument campaign;
    private readonly CampaignAccess access;
    private readonly Func<WorldChatDocument, Task>? store;
    private readonly List<ChatMessage> history;
    // What the model looked up during the turn under way; it goes with the reply when the turn lands.
    private readonly List<WorldChatStep> trail = new();
    private CancellationTokenSource? running;
    // Set before a turn's or an apply's first await, so a second one can't slip in while the first is still getting started.
    private bool claimed;
    private readonly Func<Task<bool>>? stillDm;

    /// <param name="model">The DM's own text model (<see cref="AiProviderService.ChatClientForAsync"/>), asked for at each message so a
    /// change in their AI settings counts at once.</param>
    /// <param name="document">The conversation to carry on; a new one when null.</param>
    /// <param name="store">Stores the conversation after each change; nothing is stored when null (tests).</param>
    /// <param name="stillDm">Asks again, before each turn and each apply, whether the user still runs the campaign: a chat stays
    /// open for as long as the connection lasts, and the DM may hand the campaign over meanwhile. Not asked when null (tests).</param>
    public WorldChat(WorldBuilderService builder, Func<Task<IChatClient>> model, CampaignDocument campaign, CampaignAccess access,
        WorldChatDocument? document = null, Func<WorldChatDocument, Task>? store = null, Func<Task<bool>>? stillDm = null)
    {
        this.stillDm = stillDm;
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

    /// <summary>True while a turn or an apply is under way.</summary>
    public bool IsBusy => Live != null || claimed;

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
        claimed = true;

        // The model and the system prompt first: until the turn is under way (Live), nothing here needs undoing.
        IChatClient raw;
        ChatMessage system;
        try
        {
            await StillDmAsync();
            raw = await model();
            system = new ChatMessage(ChatRole.System, AiPrompts.WorldChat(await builder.ChatRequestAsync(campaign, access)));
        }
        catch
        {
            claimed = false;
            throw;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        running = stop;
        var live = Live = new WorldChatLive();
        var watch = Stopwatch.StartNew();
        var usage = new WorldChatUsage();
        ct = stop.Token;

        // Not disposed: disposing the pipeline disposes the model client under it, which the provider service keeps for every call.
        var client = new ChatClientBuilder(raw)
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
            var (reply, cutShort) = await RunTurnAsync(client, system, new ChatOptions { Tools = Tools(OnStep), Temperature = 0.8f, MaxOutputTokens = MaxReplyTokens },
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
        catch (Exception ex) when (ex is not LocalizedException && !ct.IsCancellationRequested && TooLong(ex))
        {
            Undo();
            throw new LocalizedException("WorldChat.Error.TooLong");
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
            claimed = false;
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
        await foreach (var update in client.GetStreamingResponseAsync(ForModel(system, history), options, ct))
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

    /// <summary>The conversation as the model is sent it: the history, except that what tools returned before the DM's last few
    /// messages is cut to a line saying what it was. Those results are the bulk of a long chat (whole pages, search lists), the model
    /// has long since used them, and it can look them up again; the stored history keeps them whole for the DM.</summary>
    public static List<ChatMessage> ForModel(ChatMessage system, IReadOnlyList<ChatMessage> history)
    {
        // Where the DM's last few messages begin; a chat with fewer than that has nothing old yet.
        var fresh = 0;
        for (int i = history.Count - 1, seen = 0; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User && !history[i].Contents.OfType<FunctionResultContent>().Any() && ++seen == FreshMessages)
            {
                fresh = i;
                break;
            }
        }

        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.First().Name);
        var sent = new List<ChatMessage>(history.Count + 1) { system };
        for (var i = 0; i < history.Count; i++)
        {
            var message = history[i];
            if (i >= fresh || !message.Contents.OfType<FunctionResultContent>().Any(r => ResultText(r.Result).Length > MaxOldResult))
            {
                sent.Add(message);
                continue;
            }

            // A copy: the message itself is the stored history's.
            var contents = message.Contents.Select(c => c is FunctionResultContent result && ResultText(result.Result) is { Length: > MaxOldResult } text
                ? new FunctionResultContent(result.CallId,
                    $"[An earlier result of {(calls.TryGetValue(result.CallId, out var name) ? name : "a tool")}, {text.Length:N0} characters, left out to save room. Call the tool again if you need it.]")
                : c).ToList();
            sent.Add(new ChatMessage(message.Role, contents));
        }

        return sent;
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

    /// <summary>The errors that mean this copy of the chat is no longer the stored one: it changed in another tab, or was deleted, or another tab is applying its proposal (F10).</summary>
    public static bool IsStale(Exception ex) => ex is LocalizedException { Key: "WorldChat.Error.ChangedElsewhere" or "WorldChat.Error.Gone" or "WorldChat.Error.ApplyingElsewhere" };

    private async Task StillDmAsync()
    {
        if (stillDm != null && !await stillDm()) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }

    // What providers say when the conversation no longer fits the model: OpenAI's context_length_exceeded, Anthropic's "prompt is
    // too long" (in the API's error body, which AiFailure reads), and the wording local servers use. Read from the messages along
    // the chain, not ToString (the stack trace).
    public static bool TooLong(Exception ex)
    {
        var said = new List<string> { AiFailure.From(ex).Detail };
        for (var e = ex; e != null; e = e.InnerException) said.Add(e.Message);
        return said.Any(text => new[] { "context_length", "context length", "maximum context", "context window", "prompt is too long", "too many tokens" }
            .Any(words => text.Contains(words, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Lang => SupportedCultures.Normalize(System.Globalization.CultureInfo.CurrentUICulture.Name);

    // Ollama and LM Studio answer a tool list with an error naming tools when the model can't call them. Read, as TooLong is,
    // from the messages along the chain and not ToString: a NotSupportedException thrown through a frame named *Tool* is a bug
    // of ours, not a reason to tell the DM to change models (R14).
    public static bool SaysNoTools(Exception ex)
    {
        var said = new List<string> { AiFailure.From(ex).Detail };
        for (var e = ex; e != null; e = e.InnerException) said.Add(e.Message);
        return said.Any(text => text.Contains("tool", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("support", StringComparison.OrdinalIgnoreCase) || text.Contains("not allowed", StringComparison.OrdinalIgnoreCase)));
    }
}
