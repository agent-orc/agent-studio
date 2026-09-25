using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>Durable, deterministic projection of one reviewed integration conflict.</summary>
public sealed record IntegrationBounceObligation
{
    public string IdempotencyKey { get; init; } = "";
    public string TaskKey { get; init; } = "";
    public string RunAttemptId { get; init; } = "";
    public int OperatorReviewEpoch { get; init; }
    public string FailureCode { get; init; } = "";
    public string EvidenceFingerprint { get; init; } = "";
    public string IntegrationBranch { get; init; } = "";
    public string ResultRef { get; init; } = "";
    public string ResultSha { get; init; } = "";
    public IReadOnlyList<string> ConflictPaths { get; init; } = [];
    public int RoundCount { get; init; }
    public string HoldState { get; init; } = "none";
    public string RouteDecision { get; init; } = "operator";
    public string MechanicalRoute { get; init; } = "operator";
    public string State { get; init; } = "proposed";
    public DateTimeOffset ProposedAtUtc { get; init; }
    public DateTimeOffset? ClaimedAtUtc { get; init; }
    public string? PreviousRoute { get; init; }
    public string? SelectedRoute { get; init; }
    public string? RouteReason { get; init; }
    public string? PolicyVersion { get; init; }
    public bool OperatorPinPresent { get; init; }
}

public sealed record IntegrationBounceMetrics(
    int Eligible,
    int Queued,
    int ManualInterventions,
    int Repeats,
    int Deferred,
    double? MeanClaimLatencyMilliseconds,
    int LastSweepFalseEligibility);

/// <summary>
/// Task-owned sidecar. A no-overwrite move is the claim fence: duplicate ticks and a
/// restarted backend see the same proposal. Replacement preserves its identity.
/// </summary>
public static class IntegrationBounceObligationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IntegrationBounceObligation Project(
        TaskInfo job, ReviewSubjectRecord subject, TaskIntegrationStatus status,
        int epoch, int roundCount, string holdState, string routeDecision,
        string? attemptReason)
    {
        var paths = (status.Failure?.ConflictReport?.ConflictedFiles ?? [])
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var evidence = Hash(string.Join("\n", status.Status, status.Sha,
            status.Failure?.Code, status.Failure?.FailureSignature,
            attemptReason, string.Join("\n", paths)));
        var key = Hash(string.Join("\n", job.TaskKey, subject.RunAttemptId,
            epoch, subject.ResultRef, subject.ResultSha, evidence));
        return new IntegrationBounceObligation
        {
            IdempotencyKey = key,
            TaskKey = job.TaskKey,
            RunAttemptId = subject.RunAttemptId,
            OperatorReviewEpoch = epoch,
            FailureCode = status.Failure?.Code ?? "unknown",
            EvidenceFingerprint = evidence,
            IntegrationBranch = status.IntegrationBranch,
            ResultRef = subject.ResultRef ?? "",
            ResultSha = subject.ResultSha,
            ConflictPaths = paths,
            RoundCount = roundCount,
            HoldState = holdState,
            RouteDecision = routeDecision,
            MechanicalRoute = IntegrationBounceRoutePolicy.Decide(status),
            ProposedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static IntegrationBounceObligation Ensure(string folder, IntegrationBounceObligation proposed)
    {
        var directory = Path.Combine(TaskPaths.LogsDir(folder), "integration-bounce");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, proposed.IdempotencyKey + ".json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(proposed, Json));
            File.Move(temporary, path);
            return proposed;
        }
        catch (IOException) when (File.Exists(path))
        {
            return Read(path) ?? throw new InvalidDataException(
                "Existing integration bounce obligation is unreadable.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IntegrationBounceObligation? Read(string path)
    {
        try { return JsonSerializer.Deserialize<IntegrationBounceObligation>(File.ReadAllText(path), Json); }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public static void Update(string folder, IntegrationBounceObligation obligation)
    {
        var path = Path.Combine(TaskPaths.LogsDir(folder), "integration-bounce",
            obligation.IdempotencyKey + ".json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(obligation, Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IntegrationBounceMetrics Measure(
        IEnumerable<TaskInfo> jobs, int lastSweepFalseEligibility)
    {
        var obligations = new List<IntegrationBounceObligation>();
        foreach (var job in jobs)
        {
            var directory = Path.Combine(TaskPaths.LogsDir(job.FolderPath), "integration-bounce");
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                if (Read(path) is { } obligation) obligations.Add(obligation);
        }
        var latencies = obligations
            .Where(item => item.ClaimedAtUtc is not null)
            .Select(item => (item.ClaimedAtUtc!.Value - item.ProposedAtUtc).TotalMilliseconds)
            .ToArray();
        return new IntegrationBounceMetrics(
            obligations.Count(item => item.HoldState == "none"),
            obligations.Count(item => item.State == "queued"),
            obligations.Count(item => item.State == "manual-queued"),
            obligations.Count(item => item.RouteDecision == "guardian-required"),
            obligations.Count(item => item.State == "deferred"),
            latencies.Length == 0 ? null : latencies.Average(),
            lastSweepFalseEligibility);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Chooses the bounded route; content conflicts remain agent work.</summary>
public static class IntegrationBounceRoutePolicy
{
    public static string Decide(TaskIntegrationStatus status)
        => status.Status == IntegrationStatuses.ConflictSkipped
           && status.Failure?.RebaseRecoveryAvailable == true
            ? "merge-origin-into-delivery"
            : "operator";
}
