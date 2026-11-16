using System.Globalization;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>The problems with <c>conditionEffects.&lt;name&gt;.implies</c> (docs/CONDITION_IMPLIES_DESIGN.md): a name the system knows, not the condition's own
/// and not twice; no loop (named: <c>Dread -&gt; Shaken -&gt; Dread</c>); a <c>value</c> only on a valued condition, never on exhaustion, a number within its
/// maximum or a formula that reads; <c>stays</c> only on a condition with no value.</summary>
internal static class ImplicationChecks
{
    public static IEnumerable<string> Problems(SystemDefinition definition)
    {
        if (!definition.ConditionEffects.Values.Any(e => e.Implies.Count > 0)) yield break;
        var known = new HashSet<string>(definition.Conditions.Concat(definition.Effects).Concat(definition.ConditionEffects.Keys), StringComparer.OrdinalIgnoreCase);
        if (definition.Vitals is { } vitals)
        {
            foreach (var name in new[] { vitals.Concentration?.Condition, vitals.Exhaustion?.Condition, vitals.Down?.Condition, vitals.Down?.DeadCondition, vitals.Down?.StableCondition })
            {
                if (!string.IsNullOrWhiteSpace(name)) known.Add(name);
            }
        }

        var exhaustion = definition.Vitals?.Exhaustion?.Condition;
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, effect) in definition.ConditionEffects)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < effect.Implies.Count; i++)
            {
                var item = effect.Implies[i];
                var where = $"conditionEffects.{name}.implies[{i}]";
                if (string.IsNullOrWhiteSpace(item.Condition)) { yield return $"{where}: needs a condition name."; continue; }
                if (!known.Contains(item.Condition)) { yield return $"{where}: '{item.Condition}' is not a condition or effect this system knows."; continue; }
                if (string.Equals(item.Condition, name, StringComparison.OrdinalIgnoreCase)) { yield return $"{where}: '{name}' cannot imply itself."; continue; }
                if (!seen.Add(item.Condition)) { yield return $"{where}: '{item.Condition}' is implied twice by '{name}'."; continue; }
                if (!edges.TryGetValue(name, out var list)) edges[name] = list = new List<string>();
                list.Add(item.Condition);

                var target = SheetConditions.EffectOf(definition, item.Condition)?.Effect;
                var valued = target?.Value != null;
                if (item.Stays && effect.Value != null) yield return $"{where}: 'stays' is not allowed under '{name}', which is valued: it ends by its number (a drop or the stepper), and a stay is written only when it is removed.";
                if (item.Stays && valued) yield return $"{where}: 'stays' is for a condition with no value; '{item.Condition}' is valued.";
                if (string.IsNullOrWhiteSpace(item.Value)) continue;
                if (exhaustion != null && string.Equals(item.Condition, exhaustion, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"{where}: '{item.Condition}' is the exhaustion level; an implied level never kills, so it is not given a value.";
                }
                else if (!valued) yield return $"{where}: '{item.Condition}' has no value, so 'value' has nothing to set.";
                else if (double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    if (double.TryParse(target!.Value!.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && number > max)
                    {
                        yield return $"{where}: value {item.Value} is above the highest '{item.Condition}' goes ({target.Value.Max}).";
                    }
                }
                else if (SheetFormulas.For(definition).Check(item.Value) is { } formulaError)
                {
                    yield return $"{where}.value '{item.Value}' cannot be read: {formulaError}";
                }
            }
        }

        // A loop: walk each condition depth first, and name the way round when the walk meets one it is already inside.
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var loops = new List<string>();

        void Walk(string node)
        {
            var at = path.FindIndex(p => string.Equals(p, node, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                var loop = path.Skip(at).Append(node).ToList();
                var identity = string.Join("|", loop.Take(loop.Count - 1).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                if (reported.Add(identity)) loops.Add($"conditionEffects.{loop[0]}.implies: a loop, {string.Join(" -> ", loop)}.");
                return;
            }

            if (!done.Add(node) || !edges.TryGetValue(node, out var next)) return;
            path.Add(node);
            foreach (var to in next) Walk(to);
            path.RemoveAt(path.Count - 1);
        }

        foreach (var node in edges.Keys.ToList()) Walk(node);
        foreach (var loop in loops) yield return loop;
    }
}
