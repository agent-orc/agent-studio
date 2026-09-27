namespace AgentStudio.Pipeline;

public static class BatchGateRoutingPolicy
{
    public static bool ValidDeferredAspect(
        AgentStudio.TaskServer.Contracts.ReviewReportRequest report,
        AgentStudio.TaskServer.Contracts.ReviewPlanDto? plan)
        => plan?.BuildTestDeferredToBatch != true
           || !report.Verdicts.Any(verdict =>
               string.Equals(verdict.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(verdict.Status, "deferred-to-batch", StringComparison.Ordinal));

    public static AgentStudio.TaskServer.Contracts.ReviewReportRequest RecordDeferredAspect(
        AgentStudio.TaskServer.Contracts.ReviewReportRequest report,
        AgentStudio.TaskServer.Contracts.ReviewPlanDto? plan)
    {
        if (plan?.BuildTestDeferredToBatch != true
            || report.Verdicts.Any(verdict => string.Equals(
                verdict.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase)))
            return report;
        return report with
        {
            Verdicts = report.Verdicts.Append(new AgentStudio.TaskServer.Contracts.ReviewVerdictDto(
                "build-tests", "deferred-to-batch", "BatchGateDeferred",
                "The full suite is deferred to the closed documentation batch gate.")).ToArray(),
        };
    }

    public static bool ShouldDefer(BatchGateFormationOptions? options,
        IReadOnlyList<string>? changedPaths)
        => options is { Enabled: true, DocumentationOnly: true }
           && changedPaths is { Count: > 0 }
           && changedPaths.All(IsDocumentationPath);

    public static bool IsDocumentationPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var document = normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        if (!document) return false;
        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".agents/skills/", StringComparison.OrdinalIgnoreCase))
            return true;
        return normalized.Equals("README.md", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ROADMAP.md", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/AGENTS.md", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               && normalized.StartsWith(".github/", StringComparison.OrdinalIgnoreCase);
    }
}
