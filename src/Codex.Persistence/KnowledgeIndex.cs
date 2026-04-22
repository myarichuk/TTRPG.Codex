using System.Linq;
using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

public class KnowledgeIndex : AbstractIndexCreationTask<FactDocument, KnowledgeEntry>
{
    public KnowledgeIndex()
    {
        Map = facts => from fact in facts
                       from knower in fact.KnownBy
                       select new KnowledgeEntry
                       {
                           KnowerId = knower.EntityId,
                           FactIds = new List<string> { fact.Id }
                       };

        Reduce = results => from result in results
                            group result by result.KnowerId into g
                            select new KnowledgeEntry
                            {
                                KnowerId = g.Key,
                                FactIds = g.SelectMany(x => x.FactIds).Distinct().ToList()
                            };
    }
}