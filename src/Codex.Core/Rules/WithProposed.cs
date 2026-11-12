using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A resolver that also knows entries written for one character and kept nowhere yet, so the rules can be run on a sheet
/// that picks them. An extra entry of the same type and id hides the inner one.</summary>
public sealed class WithProposed(IEntryResolver inner, params ContentEntry[] extra) : IEntryResolver
{
    public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? inner.Resolve(type, id);

    public IEnumerable<ContentEntry> Candidates(string type) => inner.Candidates(type).Concat(extra.Where(e => e.Type == type));
}
