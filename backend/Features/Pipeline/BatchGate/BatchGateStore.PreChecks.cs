namespace AgentStudio.Pipeline;

public sealed record BatchGatePreCheckVerdict(
    string Project, string TaskKey, string ReviewAttemptId,
    string ResultSha, string BaseSha, string GateProfileDigest,
    string Outcome, string Reason, IReadOnlyList<string> FailedTests,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    double EstimatedMinutesSaved = 0)
{
    public double MinutesSaved => EstimatedMinutesSaved;
}

public sealed partial class BatchGateStore
{
    private string PreCheckPath(string project, string resultSha, string baseSha, string digest)
        => Path.Combine(_root, "pre-checks", SafeName(project),
            SafeName(resultSha) + "-" + SafeName(baseSha) + "-" + SafeName(digest) + ".json");

    public BatchGatePreCheckVerdict? ReadPreCheck(string project, string resultSha, string baseSha, string digest)
    {
        var path = PreCheckPath(project, resultSha, baseSha, digest);
        return File.Exists(path) ? Read<BatchGatePreCheckVerdict>(path) : null;
    }

    public void RecordPreCheck(BatchGatePreCheckVerdict verdict)
    {
        var path = PreCheckPath(verdict.Project, verdict.ResultSha, verdict.BaseSha, verdict.GateProfileDigest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) WriteOnce(path, verdict);
    }

    public IReadOnlyList<BatchGatePreCheckVerdict> ListPreChecks(string project)
    {
        var folder = Path.Combine(_root, "pre-checks", SafeName(project));
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Select(Read<BatchGatePreCheckVerdict>).ToArray()
            : [];
    }
}
