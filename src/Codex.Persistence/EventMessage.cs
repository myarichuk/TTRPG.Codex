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

    /// <summary>Messages rendered, joined with ", " and passed as the last argument (a rest's list of effects).</summary>
    public List<EventMessage> Inner { get; set; } = new();

    public static EventMessage Of(string key, params object?[] args) => new()
    {
        Key = key,
        Args = args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? string.Empty).ToList()
    };

    public EventMessage With(IEnumerable<EventMessage> inner)
    {
        Inner.AddRange(inner);
        return this;
    }

    public string Render(Localizer l)
    {
        var args = Args.Cast<object?>().ToList();
        if (Inner.Count > 0)
        {
            args.Add(string.Join(", ", Inner.Select(m => m.Render(l))));
        }

        return l.Get("Event." + Key, args.ToArray());
    }
}
