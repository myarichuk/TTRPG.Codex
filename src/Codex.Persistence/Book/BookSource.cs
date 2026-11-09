namespace Codex.Persistence;

/// <summary>Who a book is for. <see cref="Dm"/> is the DM's prep book (secrets included, for the renderer to box);
/// <see cref="Party"/> is what a player who owns no character may read, so it is safe to hand out.</summary>
public enum BookAudience
{
    Dm,
    Party
}

/// <summary>The campaign as a book may know it: what the overview shows, and nothing about who is in it. No owner, members,
/// invite code or world state, so a book cannot carry them by accident.</summary>
public sealed record BookCampaign(string Id, string Name, string SystemId, string Tagline, string Description, IReadOnlyList<string> ActivePacks, bool HasCover = false);

/// <summary>
/// Everything a book may be built from, read for one <see cref="BookAudience"/> through the repositories' own access-scoped
/// reads (see <see cref="BookSourceReader"/>). It is the only input of the book builder, and its constructor is internal, so the
/// builder cannot be handed a <see cref="CampaignBundle"/> (the DM's raw view, <c>CampaignExportService</c>) or any other
/// unfiltered collection: nothing but the reader makes one.
/// </summary>
public sealed class BookSource
{
    internal BookSource(
        BookAudience audience,
        BookCampaign campaign,
        IReadOnlyList<ActorDocument> actors,
        IReadOnlyList<SessionDocument> sessions,
        IReadOnlyList<FactDocument> facts,
        IReadOnlyList<RegionDocument> regions,
        IReadOnlyList<WikiPageDocument> wiki,
        IReadOnlyList<NoteDocument> notes,
        IReadOnlyList<HomebrewEntryDocument> homebrew,
        IReadOnlyList<EncounterDocument> encounters)
    {
        Audience = audience;
        Campaign = campaign;
        Actors = actors;
        Sessions = sessions;
        Facts = facts;
        Regions = regions;
        Wiki = wiki;
        Notes = notes;
        Homebrew = homebrew;
        Encounters = encounters;
    }

    public BookAudience Audience { get; }
    public BookCampaign Campaign { get; }

    /// <summary>The cast: not retired, and for the party edition as a player sees them (a stranger is only a public name and look).</summary>
    public IReadOnlyList<ActorDocument> Actors { get; }

    /// <summary>The party edition holds completed sessions only: a planned or live one is prep, and its title alone can spoil.</summary>
    public IReadOnlyList<SessionDocument> Sessions { get; }

    /// <summary>For a player's own book (<see cref="BookSourceReader.ReadForPlayerAsync"/>): the characters they own, whose blocks it holds.
    /// Empty for the party and GM editions.</summary>
    public IReadOnlyList<string> OwnedActorIds { get; init; } = [];

    public IReadOnlyList<FactDocument> Facts { get; }
    public IReadOnlyList<RegionDocument> Regions { get; }
    public IReadOnlyList<WikiPageDocument> Wiki { get; }

    /// <summary>Public table notes only, in both editions: a player's private note is theirs even though the DM can open it.</summary>
    public IReadOnlyList<NoteDocument> Notes { get; }

    public IReadOnlyList<HomebrewEntryDocument> Homebrew { get; }

    /// <summary>Always empty in the party edition: encounters have no player-scoped read, and are the DM's.</summary>
    public IReadOnlyList<EncounterDocument> Encounters { get; }
}

/// <summary>A player who has a book of their own: their account and the names of the characters they own, which is how the GM picks them and
/// what the book's file and notes are named by.</summary>
public sealed record BookPlayer(string UserId, IReadOnlyList<string> Characters);
