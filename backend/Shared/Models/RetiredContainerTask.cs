namespace AgentStudio.Shared;

/// <summary>Preserves the kind of archived historical parent cards for read-only filtering.</summary>
internal static class RetiredContainerTask
{
    internal static string NormalizeForRead(string? kind, string state) =>
        state == TaskStates.Archive && string.Equals(kind, "epic", StringComparison.OrdinalIgnoreCase)
            ? "epic"
            : TaskKinds.Normalize(kind);

    internal static bool IsRetired(TaskInfo task) =>
        string.Equals(task.Kind, "epic", StringComparison.OrdinalIgnoreCase);
}
