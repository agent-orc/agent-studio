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
}
