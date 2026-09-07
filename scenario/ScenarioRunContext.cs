using System.Text.Json;

namespace AgentStudio.Scenario;

/// <summary>
/// State carried across steps of one scenario run. Steps are ordered and a
/// later step reads what an earlier one recorded, so the context is the only
/// coupling between them.
/// </summary>
public sealed class ScenarioRunContext(
    IScenarioTarget target,
    ScenarioEndpoints endpoints,
    ScenarioFixture fixture,
    string evidenceDirectory) : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public IScenarioTarget Target { get; } = target;
    public ScenarioEndpoints Endpoints { get; } = endpoints;
    public ScenarioFixture Fixture { get; } = fixture;
    public string EvidenceDirectory { get; } = evidenceDirectory;

    /// <summary>Task Server client authenticated as the management principal.</summary>
    public required ScenarioHttpClient Server { get; init; }

    /// <summary>Studio BFF client, when the target exposes one.</summary>
    public ScenarioHttpClient? Studio { get; init; }

    public ScenarioProcess? Runner { get; set; }

    /// <summary>Client for the empty deployment a backup was restored into.</summary>
    public ScenarioHttpClient? RestoredPeer { get; set; }

    public string ServerVersion { get; set; } = "unknown";

    public string? WorkspaceId { get; set; }
    public string? ProjectId { get; set; }
    public string? TaskId { get; set; }
    public string? TaskKey { get; set; }
    public long TaskVersion { get; set; }
    public string? RunId { get; set; }

    /// <summary>Durable result identities taken from the acknowledged handoff.</summary>
    public string? ResultSha { get; set; }
    public string? ResultRepositoryId { get; set; }
    public string? ResultRepositoryUrl { get; set; }
    public string? BackupId { get; set; }
    public string? InventoryHash { get; set; }

    public string RequireProjectId() => Require(ProjectId, "project");
    public string RequireTaskKey() => Require(TaskKey, "task");
    public string RequireTaskId() => Require(TaskId, "task id");

    private static string Require(string? value, string what)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ScenarioStepException(
                $"No {what} was recorded. An earlier step must run before this one.")
            : value;

    /// <summary>
    /// Writes one evidence file next to the report and returns its
    /// report-relative path, which is what the Markdown report links to.
    /// </summary>
    public string WriteEvidence(string fileName, string content)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        File.WriteAllText(Path.Combine(EvidenceDirectory, fileName), content);
        return $"evidence/{fileName}";
    }

    public string WriteJsonEvidence(string fileName, object value)
        => WriteEvidence(
            fileName,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    public T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_owned)) item.Dispose();
        _owned.Clear();
        Server.Dispose();
        Studio?.Dispose();
    }
}

/// <summary>What one step observed, and what it left behind for the report.</summary>
public sealed record ScenarioActionResult(
    IReadOnlyDictionary<string, ScenarioFactValue> Facts,
    IReadOnlyList<string> Evidence);

/// <summary>Accumulates the typed facts of one step.</summary>
public sealed class ScenarioFactBuilder
{
    private readonly Dictionary<string, ScenarioFactValue> _facts = new(StringComparer.Ordinal);
    private readonly List<string> _evidence = [];

    public ScenarioFactBuilder Text(string name, string value)
    {
        _facts[name] = ScenarioFactValue.Text(value);
        return this;
    }

    public ScenarioFactBuilder Number(string name, long value)
    {
        _facts[name] = ScenarioFactValue.Number(value);
        return this;
    }

    public ScenarioFactBuilder Boolean(string name, bool value)
    {
        _facts[name] = ScenarioFactValue.Boolean(value);
        return this;
    }

    public ScenarioFactBuilder Evidence(string reportRelativePath)
    {
        _evidence.Add(reportRelativePath);
        return this;
    }

    public ScenarioActionResult Build() => new(_facts, _evidence);
}
