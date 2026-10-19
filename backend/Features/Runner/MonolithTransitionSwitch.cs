namespace AgentStudio.Runner;

/// <summary>Rollout switch for each durable transition entry point.</summary>
public static class MonolithTransitionSwitch
{
    public static OrchestrationExecutionMode For(IConfiguration configuration, string transition)
        => OrchestrationRolloutStore.ReadMode(configuration, transition) ?? OrchestrationExecutionModeParser.Parse(
            configuration[$"Orchestration:Transitions:{transition}"]);

    public static bool OwnsAutoReview(IConfiguration configuration)
        => For(configuration, "CodingCompletion") != OrchestrationExecutionMode.Engine
            && For(configuration, "ReviewReport") != OrchestrationExecutionMode.Engine;
}

public static class EngineDecisionWaitMarker
{
    private const string FileName = "engine-decision-wait.txt";

    public static string? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public static void Write(string folder, string runId)
    {
        var path = Path.Combine(folder, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, runId);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Removes the marker if it names <paramref name="runId"/>; returns whether it did.</summary>
    public static bool Clear(string folder, string runId)
    {
        var path = Path.Combine(folder, FileName);
        if (Read(folder) != runId) return false;
        File.Delete(path);
        return true;
    }
}
