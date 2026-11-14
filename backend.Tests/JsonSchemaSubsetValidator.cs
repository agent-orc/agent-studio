using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Tests;

/// <summary>
/// Validates a JSON document against a Draft 2020-12 schema from
/// <c>docs/app/schemas/</c> for the keywords those schemas use. The repository
/// pulls in no full validator (see <see cref="SchemaRoundTripTests"/>), so this
/// one refuses any keyword it does not implement instead of ignoring it: a
/// schema that grows a new keyword fails loudly rather than validating less.
/// </summary>
internal static class JsonSchemaSubsetValidator
{
    private static readonly HashSet<string> Annotations = ["$schema", "$id", "title", "description", "$defs"];

    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement instance)
    {
        var errors = new List<string>();
        Validate(schema, schema, instance, "$", errors);
        return errors;
    }

    private static void Validate(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        foreach (var keyword in schema.EnumerateObject())
        {
            var rule = keyword.Value;
            switch (keyword.Name)
            {
                case var name when Annotations.Contains(name):
                    break;
                case "$ref":
                    var target = rule.GetString()!;
                    if (!target.StartsWith("#/$defs/", StringComparison.Ordinal))
                        throw new NotSupportedException($"Only local $defs references are supported, not '{target}'.");
                    Validate(root, root.GetProperty("$defs").GetProperty(target["#/$defs/".Length..]), value, path, errors);
                    break;
                case "type":
                    if (!HasType(value, rule.GetString()!)) errors.Add($"{path}: expected {rule.GetString()}, found {value.ValueKind}.");
                    break;
                case "const":
                    if (!JsonElement.DeepEquals(rule, value)) errors.Add($"{path}: must be {rule.GetRawText()}.");
                    break;
                case "enum":
                    if (!rule.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value)))
                        errors.Add($"{path}: {value.GetRawText()} is not one of {rule.GetRawText()}.");
                    break;
                case "required":
                    if (value.ValueKind == JsonValueKind.Object)
                        foreach (var name in rule.EnumerateArray().Select(item => item.GetString()!))
                            if (!value.TryGetProperty(name, out _)) errors.Add($"{path}: missing required '{name}'.");
                    break;
                case "properties":
                    if (value.ValueKind == JsonValueKind.Object)
                        foreach (var property in rule.EnumerateObject())
                            if (value.TryGetProperty(property.Name, out var child))
                                Validate(root, property.Value, child, $"{path}.{property.Name}", errors);
                    break;
                case "additionalProperties":
                    if (value.ValueKind != JsonValueKind.Object) break;
                    var declared = schema.TryGetProperty("properties", out var properties)
                        ? properties.EnumerateObject().Select(property => property.Name).ToHashSet() : [];
                    foreach (var property in value.EnumerateObject().Where(property => !declared.Contains(property.Name)))
                    {
                        if (rule.ValueKind == JsonValueKind.False) errors.Add($"{path}: unknown property '{property.Name}'.");
                        else if (rule.ValueKind == JsonValueKind.Object)
                            Validate(root, rule, property.Value, $"{path}.{property.Name}", errors);
                    }
                    break;
                case "propertyNames":
                    if (value.ValueKind == JsonValueKind.Object)
                        foreach (var property in value.EnumerateObject())
                            Validate(root, rule, JsonSerializer.SerializeToElement(property.Name), $"{path}.{property.Name}(name)", errors);
                    break;
                case "minProperties":
                    if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() < rule.GetInt32())
                        errors.Add($"{path}: needs at least {rule.GetInt32()} properties.");
                    break;
                case "items":
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        var index = 0;
                        foreach (var item in value.EnumerateArray()) Validate(root, rule, item, $"{path}[{index++}]", errors);
                    }
                    break;
                case "minItems":
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() < rule.GetInt32())
                        errors.Add($"{path}: needs at least {rule.GetInt32()} items.");
                    break;
                case "uniqueItems":
                    if (rule.GetBoolean() && value.ValueKind == JsonValueKind.Array
                        && value.EnumerateArray().Select(item => item.GetRawText()).Distinct().Count() != value.GetArrayLength())
                        errors.Add($"{path}: items must be unique.");
                    break;
                case "pattern":
                    if (value.ValueKind == JsonValueKind.String && !Regex.IsMatch(value.GetString()!, rule.GetString()!))
                        errors.Add($"{path}: '{value.GetString()}' does not match {rule.GetString()}.");
                    break;
                case "minLength":
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length < rule.GetInt32())
                        errors.Add($"{path}: shorter than {rule.GetInt32()} characters.");
                    break;
                case "maxLength":
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > rule.GetInt32())
                        errors.Add($"{path}: longer than {rule.GetInt32()} characters.");
                    break;
                case "minimum":
                    if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() < rule.GetDecimal())
                        errors.Add($"{path}: below {rule.GetDecimal()}.");
                    break;
                case "maximum":
                    if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() > rule.GetDecimal())
                        errors.Add($"{path}: above {rule.GetDecimal()}.");
                    break;
                default:
                    throw new NotSupportedException($"Schema keyword '{keyword.Name}' at {path} is not supported by this validator.");
            }
        }
    }

    private static bool HasType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new NotSupportedException($"Schema type '{type}' is not supported."),
    };
}
