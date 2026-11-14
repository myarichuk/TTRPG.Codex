using System.Globalization;
using Codex.Localization;

namespace Codex.Persistence;

/// <summary>
/// One sentence of the session log as a resource key plus its arguments, so each reader sees it in their own
/// language. Names, numbers and ids are arguments; the words around them live in <c>Strings.Event*.resx</c>
/// under the key <c>Event.&lt;Key&gt;</c>.
/// </summary>
public class EventMessage
{
    public string Key { get; set; } = string.Empty;
    public List<string> Args { get; set; } = new();

    /// <summary>For each argument that is a game term (<see cref="EventTerm"/>), <c>kind:key</c>; empty for plain arguments.
    /// The argument itself keeps the English text, shown when the reader's language has no translation for the term.
    /// Empty when no argument is a term.</summary>
    public List<string> Kinds { get; set; } = new();

    /// <summary>Messages rendered, joined with ", " and passed as the last argument (a rest's list of effects).</summary>
    public List<EventMessage> Inner { get; set; } = new();

    public static EventMessage Of(string key, params object?[] args)
    {
        var message = new EventMessage { Key = key };
        foreach (var arg in args)
        {
            if (arg is EventTerm term)
            {
                message.Args.Add(term.Text);
                message.Kinds.Add($"{term.Kind}:{term.Key}");
            }
            else
            {
                message.Args.Add(Convert.ToString(arg, CultureInfo.InvariantCulture) ?? string.Empty);
                message.Kinds.Add(string.Empty);
            }
        }

        if (message.Kinds.All(k => k.Length == 0)) message.Kinds.Clear();
        return message;
    }

    public EventMessage With(IEnumerable<EventMessage> inner)
    {
        Inner.AddRange(inner);
        return this;
    }

    /// <param name="terms">Text to show for a term in the reader's language (kind, key), or null to show the stored English.</param>
    public string Render(Localizer l, EventTermText? terms = null)
    {
        var args = Args.Select((arg, i) => (object?)Term(i, arg, terms)).ToList();
        if (Inner.Count > 0)
        {
            args.Add(string.Join(", ", Inner.Select(m => m.Render(l, terms))));
        }

        return l.Get("Event." + Key, args.ToArray());
    }

    private string Term(int index, string text, EventTermText? terms)
    {
        if (terms == null || index >= Kinds.Count) return text;
        var colon = Kinds[index].IndexOf(':');
        return colon < 0 ? text : terms(Kinds[index][..colon], Kinds[index][(colon + 1)..]) ?? text;
    }
}

/// <summary>Text to show for a game term: <paramref name="kind"/> is one of the <see cref="EventTerm"/> kinds, <paramref name="key"/>
/// the value the rules know it by. Null when there is nothing better than the stored English.</summary>
public delegate string? EventTermText(string kind, string key);

/// <summary>An event argument the reader's language may name differently: a condition, a damage type, a rest, a resource,
/// a slot or a user. <see cref="Text"/> is the English shown when there is no translation.</summary>
public sealed record EventTerm(string Kind, string Key, string Text)
{
    public const string ConditionKind = "condition";
    public const string DamageTypeKind = "damage";
    public const string RestKind = "rest";
    public const string ResourceKind = "resource";
    public const string SlotKind = "slot";
    public const string UserKind = "user";

    /// <summary>What a roll bonus says in a roll's log line (a tick box's label): the key is the label and the feature it came from, joined by
    /// <see cref="BonusSeparator"/> (<see cref="Bonus"/>), as the roll's label is built from.</summary>
    public const string BonusKind = "bonus";

    /// <summary>Between a bonus's label and its source in the key of a <see cref="BonusKind"/> term (a control character, so no name holds it).</summary>
    public const char BonusSeparator = '\u001f';

    public static EventTerm Bonus(string label, string source) => new(BonusKind, label + BonusSeparator + source, label);

    /// <summary>A word of the campaign's system: a track's label, an event's or a log line's key (overhaul step 6).</summary>
    public const string SystemKind = "system";

    /// <summary>An actor, by id: each reader sees the name they know it by (a stranger's public name), or "someone" for one
    /// they cannot see (F20). The stored text is the real name, the DM's.</summary>
    public const string ActorKind = "actor";

    /// <summary>An actor this reader cannot see, put in place of its name by <see cref="SessionEventRedactor"/>.</summary>
    public const string UnseenKind = "unseen";

    /// <summary>Something only the GM may know of (a clock kept for the GM): a message naming one is not in a player's log
    /// (<see cref="SessionEventRedactor"/>).</summary>
    public const string GmOnlyKind = "gm";

    public static EventTerm GmOnly(string text) => new(GmOnlyKind, text, text);

    public static EventTerm Condition(string name) => new(ConditionKind, name, name);
    public static EventTerm DamageType(string type) => new(DamageTypeKind, type, type);
    public static EventTerm Rest(string name) => new(RestKind, name, name);
    public static EventTerm Resource(string key, string label) => new(ResourceKind, key, label);
    public static EventTerm Slot(string key, string label) => new(SlotKind, key, label);
    public static EventTerm User(string userId) => new(UserKind, userId, userId);
    public static EventTerm Actor(string actorId, string name) => new(ActorKind, actorId, name);
}
