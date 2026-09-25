using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

/// <summary>
/// Static indexes backing the campaign-scoped queries the access layer relies on (1.4). Static,
/// not auto/dynamic indexes, so the query shape a player's session runs is guaranteed rather than
/// whatever RavenDB's query optimizer happens to pick.
/// </summary>
public class ActorsByCampaignIndex : AbstractIndexCreationTask<ActorDocument>
{
    public ActorsByCampaignIndex()
    {
        Map = actors => from actor in actors
                         select new
                         {
                             actor.CampaignId,
                             actor.Visibility,
                             actor.OwnerUserId,
                             actor.CreatedAt,
                             actor.UpdatedAt
                         };
    }
}

public class NotesByTargetIndex : AbstractIndexCreationTask<NoteDocument>
{
    public NotesByTargetIndex()
    {
        Map = notes => from note in notes
                        select new
                        {
                            note.CampaignId,
                            note.TargetId,
                            note.Visibility,
                            note.AuthorId,
                            note.CreatedAt
                        };
    }
}

public class SessionsByCampaignIndex : AbstractIndexCreationTask<SessionDocument>
{
    public SessionsByCampaignIndex()
    {
        Map = sessions => from session in sessions
                           select new
                           {
                               session.CampaignId,
                               session.Date
                           };
    }
}

public class RegionsByCampaignIndex : AbstractIndexCreationTask<RegionDocument>
{
    public RegionsByCampaignIndex()
    {
        Map = regions => from region in regions
                          select new
                          {
                              region.CampaignId,
                              region.UpdatedAt
                          };
    }
}

public class FactsByCampaignIndex : AbstractIndexCreationTask<FactDocument>
{
    public FactsByCampaignIndex()
    {
        Map = facts => from fact in facts
                        select new
                        {
                            fact.CampaignId,
                            fact.Visibility
                        };
    }
}
