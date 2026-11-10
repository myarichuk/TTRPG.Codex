using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Codex.Core.Systems;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Codex.Tests;

/// <summary>
/// The system.yaml schema and reference (overhaul section 6, step 10): generated from the model, committed in docs/, and right
/// about every system the repository has, so an editor that checks against it flags what the loader would and nothing else.
/// </summary>
public sealed class SystemSchemaTests
{
    private static readonly string Docs = Path.Combine(TestPaths.RepoRoot, "docs");
    private const string Regenerate = "dotnet run --project tools/Codex.SystemCheck -- --schema docs";

    [Fact]
    public void TheCommittedSchemaAndReference_AreWhatTheModelGenerates()
    {
        Assert.True(File.ReadAllText(Path.Combine(Docs, "schema", SystemSchema.SchemaFile)).Replace("\r\n", "\n") == SystemSchema.Json(),
            $"docs/schema/{SystemSchema.SchemaFile} is out of date: {Regenerate}");
        Assert.True(File.ReadAllText(Path.Combine(Docs, SystemSchema.ReferenceFile)).Replace("\r\n", "\n") == SystemSchema.Reference(),
            $"docs/{SystemSchema.ReferenceFile} is out of date: {Regenerate}");
    }

    public static IEnumerable<object[]> Systems() =>
        new[] { "content", Path.Combine("tests", "Codex.Tests", "Fixtures") }
            .SelectMany(root => Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, root), "system.yaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => new object[] { Path.GetRelativePath(TestPaths.RepoRoot, f).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(Systems))]
    public void EverySystemInTheRepository_IsValidAgainstTheSchema(string file)
    {
        var yaml = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, file));
        // A fixture that is meant to say what this version does not read (reference/*/) lists it in its unknown keys; those
        // are what the schema flags too, so they are left out here and compared below.
        var expectedUnknown = UnknownYamlKeys.Find(yaml, typeof(Codex.Plugin.Abstractions.SystemDefinition), YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance);

        var errors = Validate(yaml);

        var unexplained = errors.Where(e => !expectedUnknown.Any(k => e == $"{k}: not a key here")).ToList();
        Assert.True(unexplained.Count == 0, $"{file}:\n  " + string.Join("\n  ", unexplained));
        Assert.Equal(expectedUnknown.Count, errors.Count(e => e.EndsWith(": not a key here", StringComparison.Ordinal)));
    }

    [Fact]
    public void AMisspeltKeyOrAWrongType_IsFlagged()
    {
        var errors = Validate("""
            id: street
            name: Street
            atributes: []
            conditions: { a: 1 }
            dice: { initiative: 1d10, checkk: 1d10 }
            attributes: [{ key: str, default: high }]
            """);

        Assert.Contains("atributes: not a key here", errors);
        Assert.Contains("conditions: expected array", errors);
        Assert.Contains("dice.checkk: not a key here", errors);
        Assert.Contains("attributes[0].default: expected integer, got 'high'", errors);
        Assert.Contains("(top): id and name are required", Validate("name: Nameless"));
    }

    // ---- A validator for the subset of JSON schema the generator writes: type, properties, additionalProperties, items, $ref,
    // enum and the top level's required. Plain YAML scalars are typed as an editor types them: 10 is a number, true a flag. ----

    private static readonly JsonObject Schema = JsonNode.Parse(SystemSchema.Json())!.AsObject();

    private static List<string> Validate(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var errors = new List<string>();
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        if (!root.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == "id") || !root.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == "name"))
        {
            errors.Add("(top): id and name are required");
        }

        Check(root, Schema, "", errors);
        return errors;
    }

    private static void Check(YamlNode node, JsonObject schema, string path, List<string> errors)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            schema = Schema["$defs"]![reference["#/$defs/".Length..]]!.AsObject();
        }

        var type = schema["type"] is JsonArray any ? "scalar" : schema["type"]?.GetValue<string>();
        var where = path.Length == 0 ? "(top)" : path;
        switch (type)
        {
            case null:
                return;
            case "object" when node is YamlMappingNode map:
                var properties = schema["properties"]?.AsObject();
                foreach (var (keyNode, value) in map.Children)
                {
                    var key = ((YamlScalarNode)keyNode).Value!;
                    var at = path.Length == 0 ? key : $"{path}.{key}";
                    if (properties != null && properties[key] is JsonObject property) Check(value, property, at, errors);
                    else if (schema["additionalProperties"] is JsonObject extra) Check(value, extra, at, errors);
                    else if (schema["additionalProperties"]?.GetValue<bool>() == false) errors.Add($"{at}: not a key here");
                }

                return;
            case "array" when node is YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++) Check(sequence.Children[i], schema["items"]!.AsObject(), $"{path}[{i}]", errors);
                return;
            case "object" or "array":
                errors.Add($"{where}: expected {type}");
                return;
        }

        if (node is not YamlScalarNode scalar)
        {
            errors.Add($"{where}: expected {type}, not a {(node is YamlMappingNode ? "map" : "list")}");
            return;
        }

        if (IsNull(scalar)) return;
        var text = scalar.Value ?? "";
        var plain = scalar.Style == ScalarStyle.Plain;
        var ok = type switch
        {
            "string" => !plain || !(IsNumber(text) || IsFlag(text)),
            "scalar" => true,
            "integer" => plain && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "number" => plain && IsNumber(text),
            "boolean" => plain && IsFlag(text),
            _ => true
        };
        if (!ok) errors.Add($"{where}: expected {type}, got '{text}'");
        if (schema["enum"] is JsonArray values && !values.Any(v => string.Equals(v!.GetValue<string>(), text, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{where}: '{text}' is not one of {string.Join(", ", values)}");
        }
    }

    private static bool IsNull(YamlScalarNode scalar) => scalar.Style == ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null";

    private static bool IsNumber(string text) => Regex.IsMatch(text, @"^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?$");

    private static bool IsFlag(string text) => text is "true" or "false" or "True" or "False" or "TRUE" or "FALSE";
}
