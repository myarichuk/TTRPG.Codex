using System.ComponentModel;
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

/// <summary>A line of the world-building conversation as the page shows it: the DM's message, the model's reply, or a note of
/// what the model looked up. A model reply carries the <paramref name="Steps"/> it took to get there; a note, the
/// <paramref name="Icon"/> that says what kind of step it was.</summary>
public sealed record WorldChatLine(WorldChatSpeaker Speaker, string Text, IReadOnlyList<WorldChatLine>? Steps = null, string? Icon = null);

public enum WorldChatSpeaker
{
    Dm,
    Model,
    Tool
}

/// <summary>A piece of the model's turn as it is written: more of its reply, or more of its reasoning (for models that share it).</summary>
public sealed record WorldChatDelta(bool Reasoning, string Text);

/// <summary>
/// One world-building conversation (docs/WORLD_BUILDER.md): the DM talks with their own model, which reads the campaign through
/// tools and proposes related entries with <c>propose_changes</c>. A proposal is only held here, never saved, until the DM
/// applies it (<see cref="ApplyAsync"/>), which goes through <see cref="WorldBuilderService.SaveAsync"/>. Lives as long as the
/// DM's connection; DM only.
/// </summary>
public sealed class WorldChat
{
    /// <summary>Most model round trips one message may take (tool calls included), so a looping model can't run up a bill.</summary>
    public const int MaxToolRounds = 16;

    private readonly WorldBuilderService builder;
    private readonly Func<Task<IChatClient>> model;
    private readonly CampaignDocument campaign;
    private readonly CampaignAccess access;
    private readonly List<ChatMessage> history = new();
    private readonly List<WorldChatLine> lines = new();
    // What the model looked up during the turn under way; it goes with the reply when the turn lands.
    private readonly List<WorldChatLine> trail = new();

    /// <param name="model">The DM's own text model (<see cref="AiProviderService.ChatClientForAsync"/>), asked for at each message so a
    /// change in their AI settings counts at once.</param>
    public WorldChat(WorldBuilderService builder, Func<Task<IChatClient>> model, CampaignDocument campaign, CampaignAccess access)
    {
        this.builder = builder;
        this.model = model;
        this.campaign = campaign;
        this.access = access;
    }

    public string CampaignId => campaign.Id;
    public IReadOnlyList<WorldChatLine> Lines => lines;

    /// <summary>What the model last proposed, waiting for the DM: entries they can untick before applying. Null when there is none.</summary>
    public WorldBundle? Proposal { get; private set; }

    public IReadOnlyList<string> ProposalWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>Sends the DM's message and runs the model, with its tool calls, until it answers. <paramref name="progress"/>
    /// hears each tool the model calls, as a line the page shows while it works; <paramref name="stream"/>, when given, hears the
    /// reply and the reasoning as they arrive, so the page can show them being written instead of a wait.</summary>
    public async Task SendAsync(string text, Action<WorldChatLine> progress, CancellationToken ct, Action<WorldChatDelta>? stream = null)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
        var message = text.Trim();
        if (message.Length == 0) return;

        if (history.Count == 0)
        {
            history.Add(new ChatMessage(ChatRole.System, AiPrompts.WorldChat(await builder.ChatRequestAsync(campaign, access))));
        }

        var raw = await model();
        using var client = new ChatClientBuilder(raw)
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = MaxToolRounds;
                // An error in a tool goes back to the model as text it can act on, not as an exception here.
                f.IncludeDetailedErrors = true;
            })
            .Build();

        history.Add(new ChatMessage(ChatRole.User, message));
        var turnStart = lines.Count;
        trail.Clear();
        var heldProposal = Proposal;
        var heldWarnings = ProposalWarnings;
        lines.Add(new WorldChatLine(WorldChatSpeaker.Dm, message));
        var before = history.Count;
        try
        {
            var response = await StreamAsync(client, new ChatOptions { Tools = Tools(progress), Temperature = 0.8f }, stream, ct);
            var cutShort = KeepAnswered(response.Messages);
            var reply = FinalAnswer(response.Messages);
            lines.Add(new WorldChatLine(WorldChatSpeaker.Model, !string.IsNullOrEmpty(reply) ? reply
                : Localizer.For(Lang)[cutShort ? "WorldChat.TooManySteps" : "WorldChat.NoReply"], trail.ToList()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !(ex is LocalizedException) && SaysNoTools(ex))
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

        void Undo()
        {
            history.RemoveRange(before - 1, history.Count - before + 1);
            lines.RemoveRange(turnStart, lines.Count - turnStart);
            // propose_changes holds its proposal the moment the model makes it, mid-turn. The turn is being forgotten, so
            // what it proposed goes with it, and what was waiting for the DM before it comes back.
            Proposal = heldProposal;
            ProposalWarnings = heldWarnings;
        }
    }

    // The turn as one response, the way GetResponseAsync would give it, with each piece of text handed on as it arrives.
    private async Task<ChatResponse> StreamAsync(IChatClient client, ChatOptions options, Action<WorldChatDelta>? stream, CancellationToken ct)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(history, options, ct))
        {
            updates.Add(update);
            if (stream == null) continue;
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

            if (message.Contents.Count > 0) history.Add(message);
        }

        return dropped;
    }

    /// <summary>Saves the proposal's ticked entries and tells the model what they became, so the conversation can build on them.</summary>
    public async Task<WorldBuildResult> ApplyAsync(CancellationToken ct = default)
    {
        var proposal = Proposal ?? throw new LocalizedException("WorldChat.Error.NoProposal");
        var result = await builder.SaveAsync(campaign, access, proposal, ct);
        Proposal = null;
        ProposalWarnings = Array.Empty<string>();

        var note = new StringBuilder("(A note from the app, not from the game master: the game master applied your proposal. These now exist, hidden from the players:");
        foreach (var page in result.Pages) note.Append($" page existing:{page.Slug} \"{page.Title}\";");
        foreach (var place in result.Places) note.Append($" place existing:{WorldBundle.PlacePrefix}{place.Id} \"{place.Name}\";");
        foreach (var actor in result.Actors) note.Append($" cast member \"{actor.Name}\";");
        foreach (var change in result.Changes) note.Append($" changed: the {(change.Target switch { EditTarget.Place => "description of the place", EditTarget.Actor => "history of the cast member", _ => "text of the page" })} \"{change.Name}\" now reads as you wrote it;");
        foreach (var entry in result.Entries) note.Append($" homebrew {entry.Type}/{entry.EntryId} \"{entry.Name}\";");
        if (result.Facts.Count > 0) note.Append($" {result.Facts.Count} secret(s) in Lore;");
        history.Add(new ChatMessage(ChatRole.User, note.ToString().TrimEnd(';') + ".)"));
        lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.Applied", result.Count], Icon: "circle-check"));
        return result;
    }

    /// <summary>Drops the proposal without saving it, and tells the model so.</summary>
    public void Discard()
    {
        if (Proposal == null) return;
        Proposal = null;
        ProposalWarnings = Array.Empty<string>();
        history.Add(new ChatMessage(ChatRole.User, "(A note from the app: the game master discarded your last proposal; nothing from it was saved.)"));
        lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.Discarded"], Icon: "x"));
    }

    private static string Lang => SupportedCultures.Normalize(System.Globalization.CultureInfo.CurrentUICulture.Name);

    // Ollama and LM Studio answer a tool list with an error naming tools when the model can't call them.
    private static bool SaysNoTools(Exception ex) =>
        ex.ToString().Contains("tool", StringComparison.OrdinalIgnoreCase)
        && (ex.ToString().Contains("support", StringComparison.OrdinalIgnoreCase) || ex.ToString().Contains("not allowed", StringComparison.OrdinalIgnoreCase));

    private List<AITool> Tools(Action<WorldChatLine> progress)
    {
        var l = Localizer.For(Lang);
        void Note(string icon, string key, params object[] args)
        {
            var step = new WorldChatLine(WorldChatSpeaker.Tool, l[key, args], Icon: icon);
            trail.Add(step);
            progress(step);
        }

        return new List<AITool>
        {
            AIFunctionFactory.Create(
                async ([Description("Words to look for in names and page texts; empty for an overview of the whole campaign.")] string? query) =>
                {
                    Note("search", "WorldChat.Tool.Search", string.IsNullOrWhiteSpace(query) ? "…" : query.Trim());
                    return await builder.SearchWorldAsync(campaign, access, query);
                },
                "search_world",
                "Finds the campaign's wiki pages, places and cast members by name or text. Each line gives the key a proposal uses to link to it."),
            AIFunctionFactory.Create(
                async ([Description("The key of a page (existing:slug) or its title; or existing:place:<id> for a place's description, existing:actor:<id> for a cast member's history.")] string page) =>
                {
                    Note("file-text", "WorldChat.Tool.Page", page);
                    return await builder.PageAsync(access, page) ?? $"There is no page \"{page}\". Use search_world to find one.";
                },
                "get_page",
                "Reads one wiki page in full, or a place's description or a cast member's history by their key, which is what an edit replaces."),
            AIFunctionFactory.Create(
                async () =>
                {
                    Note("map-pin", "WorldChat.Tool.Places");
                    return await builder.PlacesAsync(access);
                },
                "list_places",
                "Lists every region and the places inside it as a tree, with the key a new place uses as its parent."),
            AIFunctionFactory.Create(
                async ([Description("A creature's or entry's name, or part of it.")] string query,
                    [Description("A content type to search (\"monster\", \"spell\"); empty searches creatures.")] string? type) =>
                {
                    Note("book-marked", "WorldChat.Tool.Library", query);
                    return await builder.SearchLibraryAsync(campaign, access, query, type);
                },
                "search_library",
                "Searches the rules Library, for the creature whose statistics suit a person (\"stats\") or to see what already exists."),
            AIFunctionFactory.Create(
                async ([Description("The homebrew type, as listed in the instructions.")] string type) =>
                {
                    Note("list", "WorldChat.Tool.Fields", type);
                    return await builder.HomebrewFieldsAsync(campaign, access, type);
                },
                "homebrew_fields",
                "Lists the fields a homebrew entry of one type fills, for its \"properties\"."),
            AIFunctionFactory.Create(
                async (ProposalInput proposal) =>
                {
                    Note("clipboard-check", "WorldChat.Tool.Propose", proposal.Entries?.Count ?? 0);
                    return await ProposeAsync(proposal);
                },
                "propose_changes",
                "Proposes related entries to add to the campaign, all in one proposal, for the game master to review and apply. Saves nothing. " +
                "Returns errors to fix (then call it again with the whole corrected proposal) or confirms the proposal is ready for review.")
        };
    }

    private async Task<string> ProposeAsync(ProposalInput input)
    {
        var json = JsonSerializer.Serialize(input, ProposalJson);
        var check = await builder.CheckAsync(campaign, access, json);
        if (!check.Ok)
        {
            return "Nothing was proposed. Fix these and call propose_changes again with the whole proposal:\n- " + string.Join("\n- ", check.Errors) +
                   (check.Warnings.Count > 0 ? "\nAlso worth fixing:\n- " + string.Join("\n- ", check.Warnings) : "");
        }

        Proposal = check.Bundle;
        ProposalWarnings = check.Warnings;
        return $"The proposal ({check.Bundle.Drafts.Count} entries) is ready for the game master to review and apply; nothing is saved yet. Tell them in a sentence or two what it adds." +
               (check.Warnings.Count > 0 ? "\nWarnings (fix them with a new proposal if they matter):\n- " + string.Join("\n- ", check.Warnings) : "");
    }

    private static readonly JsonSerializerOptions ProposalJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>What <c>propose_changes</c> takes; the model reads this shape as the tool's schema.</summary>
public sealed class ProposalInput
{
    [JsonPropertyName("summary"), Description("What the proposal adds, in one sentence.")]
    public string? Summary { get; set; }

    [JsonPropertyName("region"), Description("The region new top-level places go into: an existing region's name, or a new one's. Empty: the campaign's first region.")]
    public string? Region { get; set; }

    [JsonPropertyName("entries"), Description("Every entry of the proposal; at most 30.")]
    public List<ProposalEntry>? Entries { get; set; }
}

/// <summary>One entry of a proposal; see <see cref="WorldBundleParser.ParseProposal"/> for how each field is read.</summary>
public sealed class ProposalEntry
{
    [JsonPropertyName("ref"), Description("A short id other entries use: \"p1\", \"n2\".")]
    public string? Ref { get; set; }

    [JsonPropertyName("kind"), Description("place, npc, wiki, fact, homebrew or edit.")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("text"), Description("The page's text in markdown (a fact's details; a homebrew entry's rules text), in full sentences under short headings, usually 150 to 350 words: written for a game master to run from, never clipped notes. Link entries as [[ref]] and pages as [[existing:slug]].")]
    public string? Text { get; set; }

    [JsonPropertyName("pitch"), Description("One or two plain sentences, at most 300 characters, saying what the entry is: shown in lists and on the review card, and as a place's short description.")]
    public string? Pitch { get; set; }

    [JsonPropertyName("target"), Description("For an edit: the key of what to change, as search_world, get_page or list_places show it: existing:<slug> (a page's text), existing:place:<id> (a place's short description) or existing:actor:<id> (a cast member's history). The \"text\" is then the COMPLETE new text, replacing the old.")]
    public string? Target { get; set; }

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

    [JsonPropertyName("bestiary"), Description("For a homebrew creature: true to put it on the Bestiary shortlist.")]
    public bool? Bestiary { get; set; }

    [JsonPropertyName("tags"), Description("For a homebrew entry: two to five short lower-case tags.")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("properties"), Description("For a homebrew entry: its fields, by the keys homebrew_fields lists.")]
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

/// <summary>Keeps each campaign's world-building conversation for as long as the DM's connection lasts, so leaving the page and
/// coming back finds it as it was.</summary>
public sealed class WorldChatSessions(WorldBuilderService builder, AiProviderService providers)
{
    private readonly Dictionary<string, WorldChat> chats = new();

    public WorldChat For(CampaignDocument campaign, CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
        if (!chats.TryGetValue(campaign.Id, out var chat))
        {
            chats[campaign.Id] = chat = new WorldChat(builder, () => providers.ChatClientForAsync(access.UserId), campaign, access);
        }

        return chat;
    }

    /// <summary>Starts the campaign's conversation over.</summary>
    public WorldChat Restart(CampaignDocument campaign, CampaignAccess access)
    {
        chats.Remove(campaign.Id);
        return For(campaign, access);
    }
}
