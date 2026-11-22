namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3858: a deterministic batch red that no isolation child reproduced.
/// <see cref="RedRunId"/> is the cohort's red run, <see cref="FailedTests"/>
/// the tests that failed in it. The mark is written once, before the cohort's
/// same-code re-gate starts, so a restart never re-gates the same red twice.
/// </summary>
public sealed record BatchFlakySuspect(
    string Project, string BatchId, string RedRunId, string BaseSha,
    IReadOnlyList<string> MemberKeys, IReadOnlyList<string> FailedTests,
    DateTimeOffset MarkedAtUtc);

/// <summary>
/// The verdict of the one same-code re-gate of a flaky suspect.
/// <see cref="Outcome"/> is <c>passed</c> (published, no per-card suite),
/// <c>reproduced</c> (a second deterministic red on the same code),
/// <c>unpublished</c> (green, but its publication did not land; <see cref="Reason"/>
/// names why), <c>requeued</c> (the gate host could not be reached; the members
/// return to the batch pool), <c>interrupted</c> (the parent left isolation
/// before the re-gate recorded a verdict), or <c>inconclusive</c> (any other
/// result). Every outcome but <c>passed</c> and <c>requeued</c> sends the
/// members down the existing per-card path.
/// </summary>
public sealed record BatchFlakySuspectRegate(
    string BatchId, string RegateBatchId, IReadOnlyList<string> MemberKeys,
    string Outcome, double Minutes, DateTimeOffset RecordedAtUtc, string? Reason = null)
{
    public const string Passed = "passed";
    public const string Reproduced = "reproduced";
    public const string Unpublished = "unpublished";
    public const string Requeued = "requeued";
    public const string Interrupted = "interrupted";
    public const string Inconclusive = "inconclusive";
}

public sealed record BatchFlakySuspectView(BatchFlakySuspect Mark, BatchFlakySuspectRegate? Regate)
{
    /// <summary>
    /// A reproduced red is no longer a flaky suspect. An interrupted re-gate
    /// ends the mark without a verdict, so the red stops counting as one too.
    /// </summary>
    public bool Active => Regate?.Outcome is not (BatchFlakySuspectRegate.Reproduced
        or BatchFlakySuspectRegate.Interrupted);
}

public sealed partial class BatchGateStore
{
    /// <summary>Returns false when this red batch was already marked, so its re-gate already ran or runs.</summary>
    public bool RecordFlakySuspect(BatchFlakySuspect mark)
    {
        var folder = FlakySuspectFolder(mark.Project);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(mark.BatchId) + ".json");
        if (File.Exists(path)) return false;
        WriteOnce(path, mark);
        return true;
    }

    public void RecordFlakySuspectRegate(string project, BatchFlakySuspectRegate regate)
    {
        var folder = FlakySuspectFolder(project);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(regate.BatchId) + ".regate.json");
        if (!File.Exists(path)) WriteOnce(path, regate);
    }

    public BatchFlakySuspectView? ReadFlakySuspect(string project, string batchId)
    {
        var folder = FlakySuspectFolder(project);
        var path = Path.Combine(folder, SafeName(batchId) + ".json");
        if (!File.Exists(path)) return null;
        var regate = Path.Combine(folder, SafeName(batchId) + ".regate.json");
        return new BatchFlakySuspectView(Read<BatchFlakySuspect>(path),
            File.Exists(regate) ? Read<BatchFlakySuspectRegate>(regate) : null);
    }

    public IReadOnlyList<BatchFlakySuspectView> ListFlakySuspects(string project)
    {
        var folder = FlakySuspectFolder(project);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json")
            .Where(path => !path.EndsWith(".regate.json", StringComparison.Ordinal))
            .Select(Read<BatchFlakySuspect>)
            .Select(mark => ReadFlakySuspect(project, mark.BatchId)!)
            .OrderBy(view => view.Mark.MarkedAtUtc).ToArray();
    }

    private string FlakySuspectFolder(string project)
        => Path.Combine(_root, "flaky-suspects", SafeName(project));
}
