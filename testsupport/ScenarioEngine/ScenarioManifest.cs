using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.TestSupport.Scenario;

/// <summary>One ordered step from a scenario's <c>steps.json</c>. Assertions are typed C#, keyed by <see cref="Id"/>; this record only carries the data a report needs to describe the step.</summary>
public sealed record ScenarioStepDefinition(
    string Id,
    string Title,
    string Level,
    string Description,
    string ExpectedOutcome);

public sealed record ScenarioKnownGap(string Topic, string Reason, string TrackedIn);

public sealed record ScenarioManifest(
    string Description,
    IReadOnlyList<ScenarioStepDefinition> Steps,
    IReadOnlyList<ScenarioKnownGap>? KnownGaps = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static ScenarioManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ScenarioManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Scenario manifest at '{path}' deserialized to null.");
    }

    /// <summary>Steps in file order for the requested level: exactly the `smoke`-tagged steps, or every step when running `full`.</summary>
    public IReadOnlyList<ScenarioStepDefinition> StepsFor(string level)
        => level == "smoke"
            ? Steps.Where(step => step.Level == "smoke").ToList()
            : Steps;
}
