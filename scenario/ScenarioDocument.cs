using System.Text.Json;

namespace AgentStudio.Scenario;

/// <summary>Deployment topology the scenario is executed against.</summary>
public enum ScenarioTargetKind
{
    /// <summary>Sibling processes started from the local build. No Docker.</summary>
    InProc,

    /// <summary>The docker-compose distributed stack on any Docker host.</summary>
    Compose,

    /// <summary>An already running deployment addressed by URL and credential.</summary>
    Remote,
}

/// <summary>Depth of the run. Smoke is the fast subset the card gate runs.</summary>
public enum ScenarioLevel
{
    Smoke,
    Full,
}

/// <summary>
/// One ordered scenario step. Steps are data: the action id selects a typed
/// implementation, <see cref="With"/> carries its parameters, and
/// <see cref="Expect"/> carries the typed assertions over the facts it emits.
/// </summary>
public sealed record ScenarioStep(
    string Id,
    string Title,
    ScenarioLevel Level,
    string Action,
    IReadOnlyList<ScenarioTargetKind> Targets,
    JsonElement With,
    ScenarioExpectation Expect);

public sealed record ScenarioDocument(
    int SchemaVersion,
    string Id,
    string Title,
    IReadOnlyList<ScenarioStep> Steps);

public sealed record ScenarioDocumentLoadResult(ScenarioDocument? Document, string? Error)
{
    public static ScenarioDocumentLoadResult Failed(string error) => new(null, error);
    public static ScenarioDocumentLoadResult Loaded(ScenarioDocument document) => new(document, null);
}

/// <summary>
/// Boundary validation for the scenario definition. Everything downstream may
/// assume a loaded document is well formed: known schema version, unique step
/// ids, known levels and targets, and a non-empty action id.
/// </summary>
public static class ScenarioDocumentLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly IReadOnlyList<ScenarioTargetKind> AllTargets =
        [ScenarioTargetKind.InProc, ScenarioTargetKind.Compose, ScenarioTargetKind.Remote];

    public static ScenarioDocumentLoadResult Parse(string json)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            return ScenarioDocumentLoadResult.Failed($"Scenario document is not valid JSON: {exception.Message}");
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ScenarioDocumentLoadResult.Failed("Scenario document root must be an object.");
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || schemaVersion.GetInt32() != SupportedSchemaVersion)
            {
                return ScenarioDocumentLoadResult.Failed(
                    $"Scenario document schemaVersion must be {SupportedSchemaVersion}.");
            }
            if (RequiredText(root, "id") is not { } id)
                return ScenarioDocumentLoadResult.Failed("Scenario document needs a non-empty id.");
            if (RequiredText(root, "title") is not { } title)
                return ScenarioDocumentLoadResult.Failed("Scenario document needs a non-empty title.");
            if (!root.TryGetProperty("steps", out var stepsElement)
                || stepsElement.ValueKind != JsonValueKind.Array
                || stepsElement.GetArrayLength() == 0)
            {
                return ScenarioDocumentLoadResult.Failed("Scenario document needs a non-empty steps array.");
            }

            var steps = new List<ScenarioStep>(stepsElement.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stepElement in stepsElement.EnumerateArray())
            {
                if (ParseStep(stepElement, out var step, out var error) is false)
                    return ScenarioDocumentLoadResult.Failed(error!);
                if (!seen.Add(step!.Id))
                    return ScenarioDocumentLoadResult.Failed($"Duplicate scenario step id '{step.Id}'.");
                steps.Add(step);
            }

            return ScenarioDocumentLoadResult.Loaded(
                new ScenarioDocument(SupportedSchemaVersion, id, title, steps));
        }
    }

    private static bool ParseStep(JsonElement element, out ScenarioStep? step, out string? error)
    {
        step = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Every scenario step must be an object.";
            return false;
        }
        if (RequiredText(element, "id") is not { } id)
        {
            error = "Every scenario step needs a non-empty id.";
            return false;
        }
        if (RequiredText(element, "title") is not { } title)
        {
            error = $"Scenario step '{id}' needs a non-empty title.";
            return false;
        }
        if (RequiredText(element, "action") is not { } action)
        {
            error = $"Scenario step '{id}' needs a non-empty action.";
            return false;
        }
        if (!TryParseLevel(OptionalText(element, "level") ?? "full", out var level))
        {
            error = $"Scenario step '{id}' has an unknown level. Use 'smoke' or 'full'.";
            return false;
        }
        if (!TryParseTargets(element, out var targets, out var targetError))
        {
            error = $"Scenario step '{id}': {targetError}";
            return false;
        }
        var with = element.TryGetProperty("with", out var withElement)
            ? withElement.Clone()
            : EmptyObject();
        if (with.ValueKind != JsonValueKind.Object)
        {
            error = $"Scenario step '{id}' has a 'with' block that is not an object.";
            return false;
        }
        if (!TryParseExpectation(element, out var expectation, out var expectationError))
        {
            error = $"Scenario step '{id}': {expectationError}";
            return false;
        }

        step = new ScenarioStep(id, title, level, action, targets!, with, expectation!);
        return true;
    }

    private static bool TryParseTargets(
        JsonElement element,
        out IReadOnlyList<ScenarioTargetKind>? targets,
        out string? error)
    {
        targets = null;
        error = null;
        if (!element.TryGetProperty("targets", out var value))
        {
            targets = AllTargets;
            return true;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            error = "'targets' must be a non-empty array when present.";
            return false;
        }
        var parsed = new List<ScenarioTargetKind>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !TryParseTarget(item.GetString(), out var target))
            {
                error = "'targets' entries must be 'inproc', 'compose', or 'remote'.";
                return false;
            }
            if (!parsed.Contains(target)) parsed.Add(target);
        }
        targets = parsed;
        return true;
    }

    private static bool TryParseExpectation(
        JsonElement element,
        out ScenarioExpectation? expectation,
        out string? error)
    {
        expectation = null;
        error = null;
        var exact = new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal);
        var atLeast = new Dictionary<string, long>(StringComparer.Ordinal);

        if (element.TryGetProperty("expect", out var expect))
        {
            if (expect.ValueKind != JsonValueKind.Object)
            {
                error = "'expect' must be an object.";
                return false;
            }
            if (expect.TryGetProperty("facts", out var facts))
            {
                if (facts.ValueKind != JsonValueKind.Object)
                {
                    error = "'expect.facts' must be an object.";
                    return false;
                }
                foreach (var fact in facts.EnumerateObject())
                {
                    if (!TryParseFactValue(fact.Value, out var value))
                    {
                        error = $"'expect.facts.{fact.Name}' must be a string, integer, or boolean.";
                        return false;
                    }
                    exact[fact.Name] = value;
                }
            }
            if (expect.TryGetProperty("factsAtLeast", out var floors))
            {
                if (floors.ValueKind != JsonValueKind.Object)
                {
                    error = "'expect.factsAtLeast' must be an object.";
                    return false;
                }
                foreach (var floor in floors.EnumerateObject())
                {
                    if (floor.Value.ValueKind != JsonValueKind.Number
                        || !floor.Value.TryGetInt64(out var value))
                    {
                        error = $"'expect.factsAtLeast.{floor.Name}' must be an integer.";
                        return false;
                    }
                    atLeast[floor.Name] = value;
                }
            }
        }

        expectation = new ScenarioExpectation(exact, atLeast);
        return true;
    }

    private static bool TryParseFactValue(JsonElement element, out ScenarioFactValue value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = ScenarioFactValue.Text(element.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = ScenarioFactValue.Boolean(element.GetBoolean());
                return true;
            case JsonValueKind.Number when element.TryGetInt64(out var number):
                value = ScenarioFactValue.Number(number);
                return true;
            default:
                value = ScenarioFactValue.Text(string.Empty);
                return false;
        }
    }

    public static bool TryParseLevel(string? value, out ScenarioLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "smoke": level = ScenarioLevel.Smoke; return true;
            case "full": level = ScenarioLevel.Full; return true;
            default: level = ScenarioLevel.Full; return false;
        }
    }

    public static bool TryParseTarget(string? value, out ScenarioTargetKind target)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "inproc": target = ScenarioTargetKind.InProc; return true;
            case "compose": target = ScenarioTargetKind.Compose; return true;
            case "remote": target = ScenarioTargetKind.Remote; return true;
            default: target = ScenarioTargetKind.InProc; return false;
        }
    }

    public static string Render(ScenarioTargetKind target) => target switch
    {
        ScenarioTargetKind.Compose => "compose",
        ScenarioTargetKind.Remote => "remote",
        _ => "inproc",
    };

    public static string Render(ScenarioLevel level)
        => level == ScenarioLevel.Smoke ? "smoke" : "full";

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string? RequiredText(JsonElement element, string name)
        => OptionalText(element, name);

    private static string? OptionalText(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
