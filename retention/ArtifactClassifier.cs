namespace AgentStudio.Retention;

public sealed record ArtifactClassification(ArtifactClass ArtifactClass, string Family, string RulePath);

public sealed class ArtifactClassifier
{
    public const long DefaultRefuseAboveBytes = 50L * 1024 * 1024;

    public ArtifactClassification Classify(string path)
    {
        var normalized = Normalize(path);
        var name = normalized.Split('/').LastOrDefault() ?? string.Empty;

        if (normalized.StartsWith(".metadata/attempt-authority", StringComparison.Ordinal)
            || normalized.StartsWith("logs/bus/", StringComparison.Ordinal)
            || normalized.Contains("/.runtime/", StringComparison.Ordinal)
            || normalized.StartsWith(".runtime/", StringComparison.Ordinal)
            || IsRuntimeName(name))
            return new(ArtifactClass.Runtime, RuntimeFamily(normalized), normalized);

        if (name == "task.json"
            || ContainsSegment(normalized, "events")
            || normalized.Contains("timeline", StringComparison.Ordinal)
            || normalized.Contains("lease", StringComparison.Ordinal)
            || normalized.Contains("fence", StringComparison.Ordinal)
            || normalized.Contains("review-attempt", StringComparison.Ordinal)
            || normalized.Contains("audit", StringComparison.Ordinal)
            || normalized.Contains("orchestrator-chat", StringComparison.Ordinal))
            return new(ArtifactClass.Authority, "authority", normalized);

        if (normalized.EndsWith("logs/cli-output.log", StringComparison.Ordinal)
            || normalized.EndsWith("logs/cli-output.log.1", StringComparison.Ordinal))
            return new(ArtifactClass.HeavyWorkingData, "cli-output", normalized);
        if (normalized.Contains("review", StringComparison.Ordinal)
            && (normalized.EndsWith(".log", StringComparison.Ordinal)
                || normalized.Contains("stdout", StringComparison.Ordinal)))
            return new(ArtifactClass.HeavyWorkingData, "review-stdout", normalized);
        if (ContainsSegment(normalized, "results"))
            return new(ArtifactClass.HeavyWorkingData, "results", normalized);
        if (ContainsSegment(normalized, "attachments"))
            return new(ArtifactClass.HeavyWorkingData, "attachments", normalized);

        if (name == "status.md"
            || name.StartsWith("prompt", StringComparison.Ordinal)
            || normalized.Contains("review-grade", StringComparison.Ordinal)
            || normalized.Contains("report", StringComparison.Ordinal)
            || normalized.Contains("integration", StringComparison.Ordinal)
            || normalized.Contains("commit", StringComparison.Ordinal)
            || name == "session-events.jsonl"
            || normalized.Contains("enrichment", StringComparison.Ordinal)
            || ContainsSegment(normalized, "post-steps"))
            return new(ArtifactClass.Evidence, "evidence", normalized);

        return new(ArtifactClass.Evidence, "evidence-other", normalized);
    }

    public bool IsCommitRefused(string path, long size, long limit = DefaultRefuseAboveBytes)
        => Classify(path).ArtifactClass == ArtifactClass.HeavyWorkingData && size > limit;

    private static bool ContainsSegment(string path, string segment)
        => path.Split('/').Contains(segment, StringComparer.Ordinal);

    private static bool IsRuntimeName(string name)
        => name.EndsWith(".tmp", StringComparison.Ordinal)
           || name.EndsWith(".cache", StringComparison.Ordinal)
           || name.EndsWith(".bak", StringComparison.Ordinal)
           || name.StartsWith("~", StringComparison.Ordinal);

    private static string RuntimeFamily(string path)
        => path.StartsWith("logs/bus/", StringComparison.Ordinal) ? "bus-log"
            : path.StartsWith(".metadata/attempt-authority.archive-", StringComparison.Ordinal) ? "attempt-authority-archive"
            : path.StartsWith(".metadata/attempt-authority", StringComparison.Ordinal) ? "attempt-authority-live"
            : "runtime";

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}
