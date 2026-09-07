namespace AgentStudio.TestRuns;

public static class TestRunStates
{
    public const string Planned = "planned";
    public const string Running = "running";
    public const string Completed = "completed";

    public static bool IsValid(string? value) => value is Planned or Running or Completed;
}

public static class TestRunResults
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static bool IsTerminal(string? value) => value is Passed or Failed or Canceled;
}

public sealed record TestRunScope
{
    public string Level { get; init; } = "project";
    public string TestSet { get; init; } = "all";
}

public sealed record TestRunRecord
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Trigger { get; init; } = "manual";
    public string Commit { get; init; } = "";
    public string Branch { get; init; } = "";
    public TestRunScope Scope { get; init; } = new();
    public string State { get; init; } = TestRunStates.Planned;
    public string? Result { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Host { get; init; }
    public int PlannedOrder { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed record CreateTestRunRequest
{
    public string Trigger { get; init; } = "manual";
    public string Commit { get; init; } = "";
    public string Branch { get; init; } = "";
    public TestRunScope Scope { get; init; } = new();
    public string State { get; init; } = TestRunStates.Planned;
    public string? Result { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Host { get; init; }
    public int? PlannedOrder { get; init; }
}

public sealed record UpdateTestRunRequest
{
    public string State { get; init; } = "";
    public string? Result { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Host { get; init; }
}

public sealed record TestRunAttachedTask(string TaskKey, string Title);

public sealed record ProjectTestRunItem
{
    public TestRunRecord Run { get; init; } = new();
    public IReadOnlyList<TestRunAttachedTask> AttachedTasks { get; init; } = [];
}

public sealed record ProjectTestRunsResponse
{
    public string Project { get; init; } = "";
    public string? HeadCommit { get; init; }
    public IReadOnlyList<ProjectTestRunItem> Runs { get; init; } = [];
}

public sealed record TaskTestRunEvidence
{
    public string? RunId { get; init; }
    public string? RunCommit { get; init; }
    public string? RunState { get; init; }
    public string? RunResult { get; init; }
    public string MatchQuality { get; init; } = "none";
    public string Direction { get; init; } = "none";
    public int? Distance { get; init; }
    public bool DiffContained { get; init; }
    public string EvidenceState { get; init; } = "unassigned";
    public bool AwaitingEvidence { get; init; }
    public string Summary { get; init; } = "No test evidence assigned";
    public IReadOnlyList<TaskTestEvidenceSource> Sources { get; init; } = [];
}

public sealed record TaskTestEvidenceSource
{
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Commit { get; init; } = "";
    public string Result { get; init; } = "";
    public DateTime? ObservedAt { get; init; }
    public string Summary { get; init; } = "";
    public string Reason { get; init; } = "";
    public string ReportRef { get; init; } = "";
}

public sealed record DeploymentTestRunReference
{
    public string Id { get; init; } = "";
    public string Commit { get; init; } = "";
    public string Branch { get; init; } = "";
    public TestRunScope Scope { get; init; } = new();
    public DateTime? CompletedAt { get; init; }
    public int? DistanceToHead { get; init; }
    public string HeadDirection { get; init; } = "unknown";
}

public sealed class TestRunValidationException(string message) : ArgumentException(message);
