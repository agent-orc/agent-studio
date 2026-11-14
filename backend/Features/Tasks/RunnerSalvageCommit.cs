namespace AgentStudio.Tasks;

/// <summary>Identifies runner teardown commits across current and historical messages.</summary>
internal static class RunnerSalvageCommit
{
    internal static bool IsTeardown(string message)
    {
        var lines = message.Split(['\r', '\n'], StringSplitOptions.None);
        if (lines[0].Trim().StartsWith("wip(runner): salvage before teardown", StringComparison.OrdinalIgnoreCase))
            return true;

        return lines.Any(line => string.Equals(
            line.Trim(), "Agent-Studio-Salvage: teardown", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for the runner's teardown commit of <paramref name="taskKey"/>: the
    /// full message carries both the task trailer for this key and the salvage
    /// trailer. Runner builds before AGT-3717 put the card title, and with it
    /// any related card key, into the subject of this commit.
    /// </summary>
    internal static bool IsTeardownFor(string? message, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(taskKey)) return false;
        var lines = message.Split(['\r', '\n'], StringSplitOptions.None)
            .Skip(1)
            .Select(line => line.Trim())
            .ToList();
        return lines.Contains("Agent-Studio-Salvage: teardown", StringComparer.Ordinal)
            && lines.Contains($"Agent-Studio-Task: {taskKey.Trim()}", StringComparer.OrdinalIgnoreCase);
    }
}
