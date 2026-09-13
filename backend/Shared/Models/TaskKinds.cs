namespace AgentStudio.Shared;

/// <summary>
/// Card kinds. A <see cref="Task"/> is a runnable unit of work (the default).
/// An <see cref="Epic"/> is a container that brackets sub-tasks under one
/// overarching goal; it is not code-executed itself - its "run" is a planning /
/// decomposition run, and only its sub-tasks flow through the pipeline.
/// A <see cref="Decision"/> is a first-class decision request (AGT-2795): a
/// fork an agent must not take alone. It is never code-executed and never
/// enters a runner lane; the decider chooses an option and the card becomes a
/// durable record that unblocks the implementation cards depending on it.
/// Persisted as the <c>"kind"</c> field in <c>job.json</c>; keep values stable.
/// </summary>
public static class TaskKinds
{
    public const string Task = "task";
    public const string Epic = "epic";
    public const string Decision = "decision";

    public static readonly string[] All = [Task, Epic, Decision];

    /// <summary>Coerce a free-form value to a known kind; unknown / empty -> Task.</summary>
    public static string Normalize(string? value)
    {
        var v = value?.Trim();
        if (string.Equals(v, Epic, System.StringComparison.OrdinalIgnoreCase)) return Epic;
        if (string.Equals(v, Decision, System.StringComparison.OrdinalIgnoreCase)) return Decision;
        return Task;
    }

    public static bool IsEpic(string? value) =>
        string.Equals(value?.Trim(), Epic, System.StringComparison.OrdinalIgnoreCase);

    public static bool IsDecision(string? value) =>
        string.Equals(value?.Trim(), Decision, System.StringComparison.OrdinalIgnoreCase);
}
