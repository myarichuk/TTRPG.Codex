using System.Globalization;
using Codex.Plugin.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Codex.Core.Systems;

/// <summary>Reads <c>choose</c> on a feature source: <c>choose: lineage</c> (the id alone) or <c>choose: { id, label, count, onChange, changesOn }</c>.</summary>
public sealed class RowChooseConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(RowChoose);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar)) return new RowChoose { Id = scalar.Value.Trim() };

        var choose = new RowChoose();
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            if (parser.TryConsume<SequenceStart>(out _))
            {
                var items = new List<string>();
                while (!parser.TryConsume<SequenceEnd>(out _)) items.Add(parser.Consume<Scalar>().Value.Trim());
                if (key.Equals("changesOn", StringComparison.OrdinalIgnoreCase)) choose.ChangesOn = items;
                continue;
            }

            var value = parser.Consume<Scalar>().Value.Trim();
            switch (key.ToLowerInvariant())
            {
                case "id": choose.Id = value; break;
                case "label": choose.Label = value; break;
                case "count" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count): choose.Count = count; break;
                case "onchange": choose.OnChange = value; break;
                case "changeson": choose.ChangesOn = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
            }
        }

        return choose;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("A system definition is only read from yaml.");
}
