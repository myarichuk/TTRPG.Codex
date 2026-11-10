using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.Core.Systems;

/// <summary>
/// The JSON schema of system.yaml and the author's reference (overhaul section 6), both generated from
/// <see cref="SystemDefinition"/>: the same properties the loader reads, named as the loader names them, by the same rules
/// <see cref="UnknownYamlKeys"/> uses, so a key the schema flags is one the loader reports. Descriptions are the model's own
/// summaries, read from Codex.Plugin.Abstractions.xml. <c>Codex.SystemCheck --schema</c> writes both into docs/, and a test
/// keeps the committed copies current.
/// <para>A system.yaml points an editor at it with a first line of
/// <c># yaml-language-server: $schema=https://.../system.schema.json</c> (or a path relative to the file), so the editor
/// completes keys and flags misspelt ones as you type.</para>
/// </summary>
public static class SystemSchema
{
    public const string SchemaFile = "system.schema.json";
    public const string ReferenceFile = "SYSTEM_YAML_REFERENCE.md";

    /// <summary>Where the published schema lives, for <c>$id</c> and for a system.yaml to name.</summary>
    public const string PublishedUrl = "https://raw.githubusercontent.com/myarichuk/TTRPG.Codex/main/docs/schema/" + SchemaFile;

    private static readonly YamlDotNet.Serialization.INamingConvention Naming = CamelCaseNamingConvention.Instance;

    /// <summary>The schema, as indented JSON with <c>\n</c> line endings.</summary>
    public static string Json()
    {
        var model = Model.Build();
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = PublishedUrl,
            ["title"] = "TTRPG.Codex system.yaml",
            ["description"] = "A game system for TTRPG.Codex. Generated from the app's SystemDefinition by Codex.SystemCheck --schema; do not edit by hand.",
        };
        Fill(root, model.Root, model);
        root["required"] = new JsonArray("id", "name");
        var defs = new JsonObject();
        foreach (var type in model.Types.Where(t => t != model.Root.Type))
        {
            var def = new JsonObject();
            if (model.Summary(type) is { } summary) def["description"] = summary;
            Fill(def, model.Shapes[type], model);
            defs[type.Name] = def;
        }

        root["$defs"] = defs;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
    }

    /// <summary>The author's reference: every section of system.yaml, each key with its type and what it is.</summary>
    public static string Reference()
    {
        var model = Model.Build();
        var text = new StringBuilder();
        text.Append("# system.yaml reference\n\n");
        text.Append("Generated from the app's `SystemDefinition` by `Codex.SystemCheck --schema`; do not edit by hand. The same model is the\n");
        text.Append($"JSON schema beside it ([schema/{SchemaFile}](schema/{SchemaFile})), which an editor can check a system.yaml against as you\n");
        text.Append("type: put this first line in the file:\n\n");
        text.Append($"```yaml\n# yaml-language-server: $schema={PublishedUrl}\n```\n\n");
        text.Append("How the pieces fit together is in [CONTENT_PACKS.md](CONTENT_PACKS.md) and the worked systems under\n");
        text.Append("`tests/Codex.Tests/Fixtures/reference`. `id` and `name` are required; everything else has a default. A key this\n");
        text.Append("version does not read is ignored, and the loader says so.\n");
        foreach (var type in model.Types)
        {
            text.Append($"\n## {(type == model.Root.Type ? "system.yaml" : type.Name)}\n\n");
            if (model.Summary(type) is { } summary) text.Append(summary).Append("\n\n");
            var shape = model.Shapes[type];
            text.Append("| Key | Type | What it is |\n|---|---|---|\n");
            foreach (var (name, property) in shape.Properties)
            {
                text.Append($"| `{name}` | {TypeText(property.PropertyType, model)} | {Cell(model.Summary(property))} |\n");
            }
        }

        return text.ToString().Replace("\r\n", "\n");
    }

    private static string Anchor(Type type, Model model) => type == model.Root.Type ? "systemyaml" : type.Name.ToLowerInvariant();

    private static string Cell(string? text) => (text ?? "").Replace("|", "\\|");

    private static string TypeText(Type type, Model model)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (Scalar(type) is { } scalar) return type.IsEnum ? $"{scalar}: {string.Join(", ", Enum.GetNames(type).Select(n => $"`{n}`"))}" : scalar;
        if (UnknownYamlKeys.DictionaryValueType(type) is { } value) return $"map of {TypeText(value, model)}";
        if (UnknownYamlKeys.ListItemType(type) is { } item) return $"list of {TypeText(item, model)}";
        return model.Shapes.ContainsKey(type) ? $"[{type.Name}](#{Anchor(type, model)})" : "any";
    }

    private static string? Scalar(Type type)
    {
        if (type == typeof(object)) return "any";
        if (type == typeof(string)) return "text (a number is text too)";
        if (type == typeof(bool)) return "true or false";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "whole number";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        if (type.IsEnum) return "one of";
        return null;
    }

    private static void Fill(JsonObject node, Shape shape, Model model)
    {
        node["type"] = "object";
        var properties = new JsonObject();
        foreach (var (name, property) in shape.Properties)
        {
            var schema = SchemaOf(property.PropertyType, model);
            if (model.Summary(property) is { } summary) schema["description"] = summary;
            properties[name] = schema;
        }

        node["properties"] = properties;
        // A key the model has no property for is one the loader ignores and reports; the editor says so first.
        node["additionalProperties"] = false;
    }

    private static JsonObject SchemaOf(Type type, Model model)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(object)) return new JsonObject();
        // The loader reads any scalar into text: a formula is text, and "min: 0" is a formula as much as "min: BODY * 2" is.
        if (type == typeof(string)) return new JsonObject { ["type"] = new JsonArray("string", "number", "boolean") };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return new JsonObject { ["type"] = "number" };
        if (type.IsEnum)
        {
            // YamlDotNet reads an enum by name, ignoring case; the schema offers the names as the model spells them.
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()) };
        }

        if (UnknownYamlKeys.DictionaryValueType(type) is { } value) return new JsonObject { ["type"] = "object", ["additionalProperties"] = SchemaOf(value, model) };
        if (UnknownYamlKeys.ListItemType(type) is { } item) return new JsonObject { ["type"] = "array", ["items"] = SchemaOf(item, model) };
        return new JsonObject { ["$ref"] = $"#/$defs/{type.Name}" };
    }

    private sealed record Shape(Type Type, IReadOnlyList<(string Name, PropertyInfo Property)> Properties);

    /// <summary>Every class reachable from <see cref="SystemDefinition"/> through the properties the loader fills, in the order
    /// they are first reached, with the summaries of the model's XML documentation.</summary>
    private sealed class Model
    {
        public Shape Root { get; private set; } = null!;
        public List<Type> Types { get; } = new();
        public Dictionary<Type, Shape> Shapes { get; } = new();
        private Dictionary<string, string> _docs = new(StringComparer.Ordinal);

        public static Model Build()
        {
            var model = new Model { _docs = Docs() };
            model.Visit(typeof(SystemDefinition));
            model.Root = model.Shapes[typeof(SystemDefinition)];
            // $defs and the reference's headings are keyed by the class's own name.
            if (model.Types.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1) is { } clash)
            {
                throw new InvalidOperationException($"Two classes of the system.yaml model are both called {clash.Key}: {string.Join(", ", clash.Select(t => t.FullName))}.");
            }

            return model;
        }

        private void Visit(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (Scalar(type) != null || Shapes.ContainsKey(type)) return;
            if (UnknownYamlKeys.DictionaryValueType(type) is { } value)
            {
                Visit(value);
                return;
            }

            if (UnknownYamlKeys.ListItemType(type) is { } item)
            {
                Visit(item);
                return;
            }

            var properties = UnknownYamlKeys.Properties(type, Naming)
                .Select(p => (p.Key, p.Value))
                .OrderBy(p => Order(type, p.Value))
                .ToList();
            Shapes[type] = new Shape(type, properties);
            Types.Add(type);
            foreach (var (_, property) in properties) Visit(property.PropertyType);
        }

        /// <summary>Declaration order, base class first: what reads most like the class itself.</summary>
        private static int Order(Type type, PropertyInfo property)
        {
            var depth = 0;
            for (var t = property.DeclaringType; t != null && t != typeof(object); t = t.BaseType) depth++;
            return -depth * 10000 + property.MetadataToken % 10000;
        }

        public string? Summary(Type type) => _docs.GetValueOrDefault("T:" + type.FullName);

        public string? Summary(PropertyInfo property) =>
            _docs.GetValueOrDefault($"P:{property.DeclaringType!.FullName}.{property.Name}");

        /// <summary>The model's summaries by member id (<c>P:Codex.Plugin.Abstractions.SystemDefinition.Conditions</c>), as one
        /// line of text each: code as <c>`code`</c>, a cross-reference as its name.</summary>
        private static Dictionary<string, string> Docs()
        {
            var docs = new Dictionary<string, string>(StringComparer.Ordinal);
            var file = Path.ChangeExtension(typeof(SystemDefinition).Assembly.Location, ".xml");
            if (!File.Exists(file)) return docs;
            foreach (var member in XDocument.Load(file).Descendants("member"))
            {
                if (member.Attribute("name")?.Value is not { } name || member.Element("summary") is not { } summary) continue;
                docs[name] = Text(summary);
            }

            return docs;
        }

        private static string Text(XElement element)
        {
            var text = new StringBuilder();
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText plain:
                        text.Append(plain.Value);
                        break;
                    case XElement { Name.LocalName: "c" or "code" } code:
                        text.Append('`').Append(code.Value).Append('`');
                        break;
                    case XElement { Name.LocalName: "see" or "seealso" } see:
                        var reference = see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? see.Attribute("href")?.Value ?? "";
                        text.Append(see.Value.Length > 0 ? see.Value : reference[(reference.LastIndexOfAny(new[] { '.', ':' }) + 1)..]);
                        break;
                    case XElement { Name.LocalName: "paramref" or "typeparamref" } param:
                        text.Append(param.Attribute("name")?.Value);
                        break;
                    case XElement other:
                        text.Append(Text(other)).Append(' ');
                        break;
                }
            }

            return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
        }
    }
}
