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
/// what the model looked up.</summary>
public sealed record WorldChatLine(WorldChatSpeaker Speaker, string Text);

public enum WorldChatSpeaker
{
    Dm,
    Model,
    Tool
}

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
    /// hears each tool the model calls, as a line the page shows while it works.</summary>
    public async Task SendAsync(string text, Action<WorldChatLine> progress, CancellationToken ct)
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
        lines.Add(new WorldChatLine(WorldChatSpeaker.Dm, message));
        var before = history.Count;
        try
        {
            var response = await client.GetResponseAsync(history, new ChatOptions { Tools = Tools(progress), Temperature = 0.8f }, ct);
            var cutShort = KeepAnswered(response.Messages);
            var reply = response.Text?.Trim();
            lines.Add(new WorldChatLine(WorldChatSpeaker.Model, !string.IsNullOrEmpty(reply) ? reply
                : Localizer.For(Lang)[cutShort ? "WorldChat.TooManySteps" : "WorldChat.NoReply"]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !(ex is LocalizedException) && SaysNoTools(ex))
        {
            history.RemoveRange(before - 1, history.Count - before + 1);
            throw new LocalizedException("WorldChat.Error.NoTools");
        }
        catch
        {
            // A stopped or failed turn leaves the conversation as it was, so the DM can send it again.
            history.RemoveRange(before - 1, history.Count - before + 1);
            lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.NotAnswered"]));
            throw;
        }
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
        foreach (var entry in result.Entries) note.Append($" homebrew {entry.Type}/{entry.EntryId} \"{entry.Name}\";");
        if (result.Facts.Count > 0) note.Append($" {result.Facts.Count} secret(s) in Lore;");
        history.Add(new ChatMessage(ChatRole.User, note.ToString().TrimEnd(';') + ".)"));
        lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.Applied", result.Count]));
        return result;
    }

    /// <summary>Drops the proposal without saving it, and tells the model so.</summary>
    public void Discard()
    {
        if (Proposal == null) return;
        Proposal = null;
        ProposalWarnings = Array.Empty<string>();
        history.Add(new ChatMessage(ChatRole.User, "(A note from the app: the game master discarded your last proposal; nothing from it was saved.)"));
        lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.Discarded"]));
    }

    private static string Lang => SupportedCultures.Normalize(System.Globalization.CultureInfo.CurrentUICulture.Name);

    // Ollama and LM Studio answer a tool list with an error naming tools when the model can't call them.
    private static bool SaysNoTools(Exception ex) =>
        ex.ToString().Contains("tool", StringComparison.OrdinalIgnoreCase)
        && (ex.ToString().Contains("support", StringComparison.OrdinalIgnoreCase) || ex.ToString().Contains("not allowed", StringComparison.OrdinalIgnoreCase));

    private List<AITool> Tools(Action<WorldChatLine> progress)
    {
        var l = Localizer.For(Lang);
        void Note(string key, params object[] args) => progress(new WorldChatLine(WorldChatSpeaker.Tool, l[key, args]));

        return new List<AITool>
        {
            AIFunctionFactory.Create(
                async ([Description("Words to look for in names and page texts; empty for an overview of the whole campaign.")] string? query) =>
                {
                    Note("WorldChat.Tool.Search", string.IsNullOrWhiteSpace(query) ? "…" : query.Trim());
                    return await builder.SearchWorldAsync(campaign, access, query);
                },
                "search_world",
                "Finds the campaign's wiki pages, places and cast members by name or text. Each line gives the key a proposal uses to link to it."),
            AIFunctionFactory.Create(
                async ([Description("The page's key (existing:slug) or title.")] string page) =>
                {
                    Note("WorldChat.Tool.Page", page);
                    return await builder.PageAsync(access, page) ?? $"There is no page \"{page}\". Use search_world to find one.";
                },
                "get_page",
                "Reads one wiki page in full."),
            AIFunctionFactory.Create(
                async () =>
                {
                    Note("WorldChat.Tool.Places");
                    return await builder.PlacesAsync(access);
                },
                "list_places",
                "Lists every region and the places inside it as a tree, with the key a new place uses as its parent."),
            AIFunctionFactory.Create(
                async ([Description("A creature's or entry's name, or part of it.")] string query,
                    [Description("A content type to search (\"monster\", \"spell\"); empty searches creatures.")] string? type) =>
                {
                    Note("WorldChat.Tool.Library", query);
                    return await builder.SearchLibraryAsync(campaign, access, query, type);
                },
                "search_library",
                "Searches the rules Library, for the creature whose statistics suit a person (\"stats\") or to see what already exists."),
            AIFunctionFactory.Create(
                async ([Description("The homebrew type, as listed in the instructions.")] string type) =>
                {
                    Note("WorldChat.Tool.Fields", type);
                    return await builder.HomebrewFieldsAsync(campaign, access, type);
                },
                "homebrew_fields",
                "Lists the fields a homebrew entry of one type fills, for its \"properties\"."),
            AIFunctionFactory.Create(
                async (ProposalInput proposal) =>
                {
                    Note("WorldChat.Tool.Propose", proposal.Entries?.Count ?? 0);
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

    [JsonPropertyName("kind"), Description("place, npc, wiki, fact or homebrew.")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("text"), Description("The page's text in markdown (a fact's details; a homebrew entry's rules text). Link entries as [[ref]] and pages as [[existing:slug]].")]
    public string? Text { get; set; }

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
