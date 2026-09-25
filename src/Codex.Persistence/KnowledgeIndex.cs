using System.Linq;
using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

/// <summary>
/// "What does this entity know?", scoped by campaign (1.4) - two campaigns can each have their own
/// "npc:vizier" blueprint instance and must never leak facts across that boundary.
/// </summary>
public class KnowledgeIndex : AbstractIndexCreationTask<FactDocument, KnowledgeEntry>
{
    public KnowledgeIndex()
    {
        Map = facts => from fact in facts
                       from knower in fact.KnownBy
                       select new KnowledgeEntry
                       {
                           CampaignId = fact.CampaignId,
                           KnowerId = knower.EntityId,
                           FactIds = new List<string> { fact.Id }
                       };

        Reduce = results => from result in results
                            group result by new { result.CampaignId, result.KnowerId } into g
                            select new KnowledgeEntry
                            {
                                CampaignId = g.Key.CampaignId,
                                KnowerId = g.Key.KnowerId,
                                FactIds = g.SelectMany(x => x.FactIds).Distinct().ToList()
                            };
    }
}
