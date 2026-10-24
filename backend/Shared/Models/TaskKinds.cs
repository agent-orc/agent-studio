namespace AgentStudio.Shared;

/// <summary>
/// Card kinds. A <see cref="Task"/> is a runnable unit of work (the default).
/// A <see cref="Decision"/> is a first-class decision request (AGT-2795): a
/// fork an agent must not take alone. It is never code-executed and never
/// enters a runner lane; the decider chooses an option and the card becomes a
/// durable record that unblocks the implementation cards depending on it.
/// Persisted as the <c>"kind"</c> field in <c>task.json</c>; keep values stable.
/// </summary>
public static class TaskKinds
{
    public const string Task = "task";
    public const string Decision = "decision";

    public static readonly string[] All = [Task, Decision];

    /// <summary>Coerce a free-form value to a known kind; unknown / empty -> Task.</summary>
    public static string Normalize(string? value)
    {
        var v = value?.Trim();
        if (string.Equals(v, Decision, System.StringComparison.OrdinalIgnoreCase)) return Decision;
        return Task;
    }

    public static bool IsDecision(string? value) =>
        string.Equals(value?.Trim(), Decision, System.StringComparison.OrdinalIgnoreCase);
}
