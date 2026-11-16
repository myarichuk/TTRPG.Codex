using Codex.Plugin.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Codex.Core.Systems;

/// <summary>Reads an item of <c>implies</c>: <c>Dazzled</c> (the name alone) or <c>{ condition, value, stays }</c>.</summary>
public sealed class ConditionImplicationConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ConditionImplication);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar)) return new ConditionImplication { Condition = scalar.Value.Trim() };

        var item = new ConditionImplication();
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var keyScalar = parser.Consume<Scalar>();
            var key = keyScalar.Value;
            if (!parser.TryConsume<Scalar>(out var value))
            {
                throw new YamlException(keyScalar.Start, keyScalar.End, $"implies: '{key}' needs a single value.");
            }

            switch (key.ToLowerInvariant())
            {
                case "condition": item.Condition = value.Value.Trim(); break;
                case "value": item.Value = value.Value.Trim(); break;
                case "stays":
                    var word = value.Value.Trim();
                    if (!word.Equals("true", StringComparison.OrdinalIgnoreCase) && !word.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new YamlException(value.Start, value.End, $"implies: 'stays' is true or false, not '{word}'.");
                    }

                    item.Stays = word.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    throw new YamlException(keyScalar.Start, keyScalar.End, $"implies: '{key}' is not a key of an implied condition (condition, value, stays).");
            }
        }

        return item;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("A system definition is only read from yaml.");
}
