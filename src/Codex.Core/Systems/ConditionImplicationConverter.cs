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
            var key = parser.Consume<Scalar>().Value;
            if (!parser.TryConsume<Scalar>(out var value))
            {
                parser.SkipThisAndNestedEvents();
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "condition": item.Condition = value.Value.Trim(); break;
                case "value": item.Value = value.Value.Trim(); break;
                case "stays": item.Stays = value.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase); break;
            }
        }

        return item;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("A system definition is only read from yaml.");
}
