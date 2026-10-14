using System.Reflection;

namespace AgentStudio.OrchestratorEngine;

public static class EngineVersion
{
    public static string DecisionLibraryVersion
        => typeof(AgentStudio.OrchestrationDecisions.TransitionDecisionPolicy)
            .Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string ProductVersion
        => typeof(EngineVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string GitSha
    {
        get
        {
            var informational = typeof(EngineVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var separator = informational?.IndexOf('+') ?? -1;
            if (separator < 0 || separator + 1 >= informational!.Length)
                return "unknown";
            var metadata = informational[(separator + 1)..];
            return metadata.StartsWith("unknown.", StringComparison.Ordinal)
                ? metadata["unknown.".Length..]
                : metadata;
        }
    }

    public static string Display => $"orchestrator-engine {ProductVersion} ({GitSha})";
}
