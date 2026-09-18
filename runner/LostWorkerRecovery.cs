using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// What a daemon does with the worktree of a detached worker that disappeared
/// before it recorded a result.
/// </summary>
public enum LostWorkerRecoveryAction
{
    /// <summary>Nothing to preserve: no checkout, or a read-only planning checkout.</summary>
    None,

    /// <summary>
    /// The detecting daemon still knows task, attempt and fence, so the work is
    /// published under the normal generation-scoped salvage ref of that attempt.
    /// </summary>
    Salvage,

    /// <summary>
    /// Attribution is impossible (no fenced generation on this workspace), so the
    /// content is preserved without claiming a delivery identity.
    /// </summary>
    Quarantine,
}

/// <summary>
/// Pure policy for a lost detached worker (AGT-2870).
///
/// <para>
/// A worker that dies without writing <c>result.json</c> leaves the run's only
/// copy of the work in its worktree. The daemon that detects the loss holds the
/// persisted slot, so it knows the task, the attempt id and the fencing token:
/// that is exactly the attribution a generation-scoped salvage ref needs.
/// Quarantine stays reserved for a worktree whose slot state is really unknown,
/// because a quarantine ref names no attempt and therefore cannot carry a
/// continuation.
/// </para>
/// </summary>
public static class LostWorkerRecoveryPolicy
{
    /// <summary>Outcome label written into the salvage commit message and the release.</summary>
    public const string Outcome = "WorkerLost";

    /// <summary>Release outcome the server reads to open a continuation round.</summary>
    public const string ReleaseOutcome = "worker-lost";

    public static LostWorkerRecoveryAction Decide(
        bool worktreeExists,
        bool readOnlyCheckout,
        bool hasFencedGeneration)
    {
        if (!worktreeExists || readOnlyCheckout) return LostWorkerRecoveryAction.None;
        return hasFencedGeneration
            ? LostWorkerRecoveryAction.Salvage
            : LostWorkerRecoveryAction.Quarantine;
    }
}

/// <summary>
/// What a lost worker hands back to the Task Server: the salvage ref its work
/// was published under and the crash line that explains the loss. Both are
/// optional, because a worker can die with an empty checkout and without having
/// logged anything.
/// </summary>
public sealed record LostWorkerHandoff(
    string? SalvageBranch,
    string? SalvageCommitSha,
    string? Detail)
{
    public static LostWorkerHandoff None { get; } = new(null, null, null);

    public bool HasSalvage
        => !string.IsNullOrWhiteSpace(SalvageBranch) && !string.IsNullOrWhiteSpace(SalvageCommitSha);
}

/// <summary>
/// What the host could still see about a worker after it died: the tail of its
/// own output and the counters of the cgroup it ran in. Without this the card
/// shows a bare "worker disappeared" and the actual cause (in the observed
/// incident a <c>PAL_SEHException</c> raised at the task ceiling) is only
/// readable on the host.
/// </summary>
public sealed record WorkerCrashEvidence(
    IReadOnlyList<string> Lines,
    WorkerCgroupPressure? Pressure)
{
    public static WorkerCrashEvidence None { get; } = new([], null);

    /// <summary>The single most specific crash line, or null when the worker logged none.</summary>
    public string? CrashLine => Lines.Count == 0 ? null : Lines[^1];

    /// <summary>
    /// The run-summary block. One line per fact so the summary stays greppable:
    /// the counters first (they answer "did it hit its envelope"), then the
    /// worker's own last words.
    /// </summary>
    public IReadOnlyList<string> Describe(string attemptId, string detail)
    {
        var lines = new List<string>
        {
            $"[runner] worker-lost attempt={attemptId} detail={OneLine(detail)}",
        };
        if (Pressure is not null)
            lines.Add($"[runner] worker-lost-counters attempt={attemptId} {Pressure.Describe()}");
        foreach (var line in Lines)
            lines.Add($"[runner] worker-lost-stderr {OneLine(line)}");
        return lines;
    }

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>
/// Reads the crash evidence of one lost worker from host state only. Every read
/// is best effort: a worker that died before it wrote anything still has to
/// produce a completion, so a missing file is an empty evidence block and never
/// an exception.
/// </summary>
public static class WorkerCrashEvidenceReader
{
    /// <summary>How many trailing worker lines are worth carrying on a card.</summary>
    public const int MaxLines = 5;

    /// <summary>Bound on the tail that is parsed, so a multi-megabyte log cannot be read into memory.</summary>
    private const int MaxScannedLines = 2000;

    public static WorkerCrashEvidence Read(string workerDirectory, int maxLines = MaxLines)
        => new(ReadDiagnosticLines(workerDirectory, maxLines), WorkerCgroup.ReadPressureFor(workerDirectory));

    private static IReadOnlyList<string> ReadDiagnosticLines(string workerDirectory, int maxLines)
    {
        var path = Path.Combine(workerDirectory, "output.jsonl");
        if (maxLines <= 0 || !File.Exists(path)) return [];
        try
        {
            var tail = new Queue<string>();
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var scanned = 0;
            while (reader.ReadLine() is { } raw)
            {
                if (++scanned > MaxScannedLines)
                {
                    scanned = 0;
                    tail.Clear();
                }
                if (!TryReadDiagnostic(raw, out var text)) continue;
                tail.Enqueue(text);
                if (tail.Count > maxLines) tail.Dequeue();
            }
            return [.. tail];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool TryReadDiagnostic(string raw, out string text)
    {
        text = string.Empty;
        try
        {
            var line = JsonSerializer.Deserialize<DetachedJobLogLine>(
                raw,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (line is null) return false;
            // stdout carries the provider's own protocol frames; only the
            // diagnostic streams say why a process died.
            if (!string.Equals(line.Stream, "stderr", StringComparison.Ordinal)
                && !string.Equals(line.Stream, "system", StringComparison.Ordinal))
                return false;
            if (string.IsNullOrWhiteSpace(line.Text)) return false;
            text = line.Text.Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
