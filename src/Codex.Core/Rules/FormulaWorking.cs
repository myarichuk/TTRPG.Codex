using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A system's formula with the sheet's numbers in it, for the sheet's sums: "mod(dex)" reads "DEX +2" and
/// "2 + (level - 1) / 4" reads "2 + (3 - 1) / 4".</summary>
public static partial class FormulaWorking
{
    [GeneratedRegex(@"\bmod\(\s*([A-Za-z_]\w*)\s*\)|\b([A-Za-z_]\w*)\b(?!\s*\()")]
    private static partial Regex Names();

    /// <param name="abbreviationOf">The short name to show for an attribute key, in the reader's language; null for a key that is not one.</param>
    public static string Write(string formula, IReadOnlyDictionary<string, double> inputs, Func<string, string?> abbreviationOf) =>
        Names().Replace(formula, m =>
        {
            if (m.Groups[1].Success)
            {
                var key = m.Groups[1].Value;
                if (!inputs.TryGetValue(key, out var score)) return m.Value;
                var modifier = AttributeDefinition.HalfMinusFive((int)Math.Floor(score));
                return $"{abbreviationOf(key) ?? key} {(modifier >= 0 ? "+" : "")}{modifier.ToString(CultureInfo.InvariantCulture)}";
            }

            return inputs.TryGetValue(m.Groups[2].Value, out var number) ? number.ToString(CultureInfo.InvariantCulture) : m.Value;
        });
}
