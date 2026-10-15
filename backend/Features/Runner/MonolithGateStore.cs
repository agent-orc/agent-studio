using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Registered gate executors of the authority (runner hosts with the gate role).</summary>
public interface IGateExecutorDirectory
{
    bool TryGetGateExecutor(string runnerId, string instanceId,
        out string hostId, out IReadOnlySet<string> capabilities);
}

public sealed class GateConflictException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>One gate attempt with the lease fields that never leave the authority.</summary>
public sealed record MonolithGateAttempt(
    GateAttempt Attempt,
    string? InstanceId = null,
    string? LeaseId = null,
    DateTime? AcquiredAt = null,
    DateTime? ExpiresAt = null,
    string? ResourceNamespace = null,
    int PortBase = 0,
    GateReport? Report = null,
    string? ReportDigest = null,
    DateTime? ContainmentDeadline = null);

public sealed record MonolithGateRecord(
    GateSubject Subject, long LastFence, IReadOnlyList<MonolithGateAttempt> Attempts);

/// <summary>
/// Claimable gate work on the monolith authority (AGT-3055, D16 A). Runner
/// hosts with the gate role claim attempts through the v1 gate routes, so the
/// batch suite runs under the same lease, fence and report rules as on the
/// Task Server (<see cref="GateRules"/>). One atomic JSON file per subject
/// keeps queued attempts, fences and accepted reports across restarts.
/// </summary>
public sealed class MonolithGateStore
{
    private const long AuthorityEpoch = 0;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string? _directory;
    private readonly TimeProvider _time;
    private readonly IGateExecutorDirectory _executors;
    private readonly Dictionary<string, MonolithGateRecord> _subjects = new(StringComparer.Ordinal);
    private int _portCursor = 23992;

    public MonolithGateStore(IConfiguration configuration, TimeProvider time, IGateExecutorDirectory executors)
        : this(string.IsNullOrWhiteSpace(configuration["TaskRepository"])
            ? null
            : Path.Combine(configuration["TaskRepository"]!, ".gate-subjects"), time, executors) { }

    public MonolithGateStore(string? directory, TimeProvider time, IGateExecutorDirectory executors)
    {
        _directory = directory;
        _time = time;
        _executors = executors;
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "gsub_*.json"))
        {
            var record = JsonSerializer.Deserialize<MonolithGateRecord>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException($"Gate subject is empty: {file}");
            _subjects.Add(record.Subject.SubjectId, record);
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public GateStatus Create(CreateGateSubjectRequest request)
    {
        GateRules.ValidateSubject(request);
        lock (_gate)
        {
            var existing = _subjects.Values.FirstOrDefault(item =>
                item.Subject.SourceRunId == request.SourceRunId
                && item.Subject.Plan.GateId == request.Plan.GateId
                && string.Equals(item.Subject.PlanHash, request.PlanHash, StringComparison.OrdinalIgnoreCase)
                && item.Subject.BatchId == request.BatchId
                && item.Subject.ExpectedSha == request.ExpectedSha.ToLowerInvariant()
                && item.Subject.TestSelectionAuditDigest == request.TestSelectionAuditDigest);
            if (existing is not null)
            {
                if (!GateRules.SameSubject(existing.Subject, request))
                    throw new GateConflictException("gate-subject-conflict",
                        "Gate subject replay differs from the immutable source.");
                return Status(existing);
            }
            var now = Now;
            var subject = new GateSubject($"gsub_{Guid.NewGuid():N}", request.TaskId, request.SourceRunId,
                request.RepositoryId, request.RepositoryUrl, request.ExpectedSha.ToLowerInvariant(),
                request.ResultRef, request.SourceBundleArtifactId, request.SourceBundleSha256,
                request.PlanHash.ToLowerInvariant(), request.PolicyHash, request.PipelineDefinitionVersion,
                request.TestSelectionAuditDigest, request.Plan, now, request.DispatchDeadline.ToUniversalTime(),
                request.MaxAttempts, request.BatchId, request.MembershipDigest, request.BaseSha,
                request.MemberRunIds);
            var record = new MonolithGateRecord(subject, 0, [Queued(subject.SubjectId, 1, now)]);
            Save(record);
            return Status(record);
        }
    }

    public GateStatus? GetStatus(string subjectId)
    {
        lock (_gate)
        {
            ReconcileTimeouts();
            return _subjects.TryGetValue(subjectId, out var record) ? Status(record) : null;
        }
    }

    public GateStatus Cancel(string subjectId)
    {
        lock (_gate)
        {
            var record = RequiredSubject(subjectId);
            var current = record.Attempts[^1];
            if (GateStates.IsTerminal(current.Attempt.State)) return Status(record);
            var now = Now;
            record = Replace(record, current with
            {
                Attempt = current.Attempt with
                {
                    State = GateStates.Cancelled, Outcome = GateStates.Cancelled, ReportedAt = now,
                },
                ExpiresAt = now, ContainmentDeadline = null,
            });
            Save(record);
            return Status(record);
        }
    }

    public GateClaimResponse Claim(GateClaimRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
        lock (_gate)
        {
            ReconcileTimeouts();
            if (request.AvailableGateSlots <= 0)
                return new GateClaimResponse("empty", Message: "No free gate slot.");
            if (!_executors.TryGetGateExecutor(request.ExecutorId, request.InstanceId,
                    out var hostId, out var capabilities))
                throw new GateConflictException("gate-executor-not-registered",
                    "Register this identity with the gate-executor capability before claiming.");
            if (_subjects.Values.SelectMany(item => item.Attempts).Any(item =>
                    item.Attempt.HostId == hostId && IsActive(item.Attempt.State)))
                return new GateClaimResponse("empty", Message: "Gate host slot is occupied.");
            foreach (var (record, candidate) in _subjects.Values
                         .SelectMany(record => record.Attempts.Select(attempt => (record, attempt)))
                         .Where(item => item.attempt.Attempt.State == GateStates.Queued)
                         .OrderBy(item => item.attempt.Attempt.CreatedAt).Take(32).ToArray())
            {
                var number = candidate.Attempt.AttemptNumber;
                // A same-SHA infrastructure retry needs a different healthy host.
                if (number > 1 && record.Attempts.FirstOrDefault(item =>
                        item.Attempt.AttemptNumber == number - 1)?.Attempt.HostId == hostId)
                    continue;
                var required = new HashSet<string>(record.Subject.Plan.RequiredCapabilities, StringComparer.Ordinal)
                {
                    GateCapabilities.Executor, GateCapabilities.GitMaterialization,
                };
                if (record.Subject.ResultRef is null) required.Add(GateCapabilities.BundleMaterialization);
                if (!required.IsSubsetOf(capabilities)) continue;
                var fence = record.LastFence + 1;
                var now = Now;
                var expiry = now.AddSeconds(Ttl(request.RequestedTtlSeconds));
                _portCursor = _portCursor >= 59992 ? 24000 : _portCursor + 8;
                var claimed = candidate with
                {
                    Attempt = candidate.Attempt with
                    {
                        State = GateStates.Claimed, ExecutorId = request.ExecutorId, HostId = hostId,
                        ClaimedAt = now, Fence = fence,
                        Deadline = now.AddSeconds(record.Subject.Plan.OverallTimeoutSeconds),
                    },
                    InstanceId = request.InstanceId,
                    LeaseId = $"gls_{Guid.NewGuid():N}",
                    AcquiredAt = now,
                    ExpiresAt = expiry,
                    ResourceNamespace = $"gate-{candidate.Attempt.AttemptId}-{fence}",
                    PortBase = _portCursor,
                };
                var updated = Replace(record, claimed) with { LastFence = fence };
                Save(updated);
                return new GateClaimResponse("claimed", updated.Subject, claimed.Attempt, Lease(claimed));
            }
            return new GateClaimResponse("empty", Message: "No eligible gate subject is queued.");
        }
    }

    public GateLease Renew(string attemptId, GateRenewRequest request)
    {
        lock (_gate)
        {
            var (record, attempt) = RequiredAttempt(attemptId);
            ValidateAuthority(attempt, request.Authority);
            attempt = attempt with { ExpiresAt = Now.AddSeconds(Ttl(request.RequestedTtlSeconds)) };
            Save(Replace(record, attempt));
            return Lease(attempt);
        }
    }

    public GateAttempt Advance(string attemptId, GatePhaseRequest request)
    {
        lock (_gate)
        {
            var (record, attempt) = RequiredAttempt(attemptId);
            ValidateAuthority(attempt, request.Authority);
            var next = attempt.Attempt.State switch
            {
                GateStates.Claimed => GateStates.Materializing,
                GateStates.Materializing => GateStates.Running,
                GateStates.Running => GateStates.Reporting,
                GateStates.Reporting => GateStates.Cleaning,
                _ => null,
            };
            var failedBeforeRun = request.Phase == GateStates.Reporting
                && attempt.Attempt.State is GateStates.Claimed or GateStates.Materializing;
            if (request.Phase != next && !failedBeforeRun)
                throw new GateConflictException("gate-phase-conflict",
                    "Gate phase does not follow the durable state machine.");
            attempt = attempt with { Attempt = attempt.Attempt with { State = request.Phase } };
            Save(Replace(record, attempt));
            return attempt.Attempt;
        }
    }

    public GateStatus Report(string attemptId, SubmitGateReportRequest request)
    {
        lock (_gate)
        {
            var (record, attempt) = RequiredAttempt(attemptId);
            ValidateAuthority(attempt, request.Authority, allowTerminalReplay: true);
            if (!string.Equals(record.Subject.ExpectedSha, request.Report.TestedSha, StringComparison.OrdinalIgnoreCase))
                throw new GateConflictException("gate-tested-sha-mismatch",
                    "Tested SHA differs from the immutable subject.");
            if (GateRules.ReportViolation(record.Subject.Plan, request.Report) is { } violation)
                throw new GateConflictException("gate-report-invalid", violation);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(request.Report, Json)))).ToLowerInvariant();
            if (attempt.ReportDigest is not null)
            {
                if (attempt.ReportDigest != digest)
                    throw new GateConflictException("gate-report-conflict",
                        "A different report was already accepted for this attempt.");
                return Status(record);
            }
            if (attempt.Attempt.State != GateStates.Cleaning)
                throw new GateConflictException("gate-phase-conflict",
                    "Gate report requires completed cleanup phase.");
            var terminal = GateOutcomePolicy.Decide(request.Report, attempt.Attempt.AttemptNumber,
                record.Subject.MaxAttempts);
            var now = Now;
            attempt = attempt with
            {
                Attempt = attempt.Attempt with
                {
                    State = terminal.State, FailureClassification = terminal.FailureClassification,
                    Outcome = terminal.Outcome, ReportedAt = now,
                    CleanedAt = request.Report.CleanupStatus == "clean" ? now : null,
                },
                Report = request.Report,
                ReportDigest = digest,
            };
            record = Replace(record, attempt);
            if (terminal.State == GateStates.InfraRetry)
                record = record with { Attempts = [.. record.Attempts,
                    Queued(record.Subject.SubjectId, attempt.Attempt.AttemptNumber + 1, now)] };
            Save(record);
            return Status(record);
        }
    }

    public GateStatus ConfirmContainment(string attemptId, GateContainmentRequest request)
    {
        lock (_gate)
        {
            ReconcileTimeouts();
            var (record, attempt) = RequiredAttempt(attemptId);
            if (!_executors.TryGetGateExecutor(request.ExecutorId, request.InstanceId, out var hostId, out _)
                || hostId != request.HostId)
                throw new GateConflictException("gate-containment-identity",
                    "Containment must come from a live registered host instance.");
            if (attempt.Attempt.HostId != request.HostId || attempt.Attempt.Fence != request.Fence
                || attempt.ResourceNamespace != request.ResourceNamespace
                || !request.NoProcesses || !request.WorkspaceRemoved)
                throw new GateConflictException("gate-containment-invalid",
                    "Gate containment proof does not match the fenced namespace.");
            if (attempt.Attempt.CleanedAt is not null
                && (attempt.Attempt.State == GateStates.InfraRetry || GateStates.IsTerminal(attempt.Attempt.State)))
                return Status(record);
            var now = Now;
            if (IsActive(attempt.Attempt.State))
                attempt = attempt with
                {
                    Attempt = attempt.Attempt with
                    {
                        State = GateStates.InfraRetry, FailureClassification = GateFailureClasses.LostLease,
                        Outcome = GateFailureClasses.LostLease, ReportedAt = now,
                    },
                    ContainmentDeadline = now.AddMinutes(5),
                };
            else if (attempt.Attempt.State != GateStates.InfraRetry)
                throw new GateConflictException("gate-containment-stale",
                    "Gate attempt is no longer waiting for containment.");
            if (attempt.Attempt.AttemptNumber >= record.Subject.MaxAttempts)
                record = Replace(record, attempt with
                {
                    Attempt = attempt.Attempt with
                    {
                        State = GateStates.InfraFailed, Outcome = GateFailureClasses.GateInfra, CleanedAt = now,
                    },
                    ContainmentDeadline = null,
                });
            else
            {
                record = Replace(record, attempt with
                {
                    Attempt = attempt.Attempt with { CleanedAt = now }, ContainmentDeadline = null,
                });
                record = record with { Attempts = [.. record.Attempts,
                    Queued(record.Subject.SubjectId, attempt.Attempt.AttemptNumber + 1, now)] };
            }
            Save(record);
            return Status(record);
        }
    }

    private void ReconcileTimeouts()
    {
        var now = Now;
        foreach (var record in _subjects.Values.ToArray())
        {
            var updated = record;
            foreach (var item in record.Attempts)
            {
                var state = item.Attempt.State;
                if (state == GateStates.InfraRetry && item.ContainmentDeadline <= now)
                {
                    updated = Replace(updated, item with
                    {
                        Attempt = item.Attempt with { State = GateStates.InfraFailed, Outcome = GateFailureClasses.GateInfra },
                        ContainmentDeadline = null,
                    });
                    continue;
                }
                var expired = state == GateStates.Queued && record.Subject.DispatchDeadline <= now
                    || IsActive(state) && (item.ExpiresAt <= now || item.Attempt.Deadline <= now.AddMinutes(-5));
                if (!expired) continue;
                var failure = state == GateStates.Queued ? GateFailureClasses.NoEligibleGateExecutor
                    : item.Attempt.Deadline <= now ? GateFailureClasses.ExecutionTimeout : GateFailureClasses.LostLease;
                // Lease expiry fences reports, but a retry waits for a host
                // cleanup attestation. A missing host settles as GateInfra.
                var retry = state != GateStates.Queued && item.Attempt.AttemptNumber < record.Subject.MaxAttempts;
                updated = Replace(updated, item with
                {
                    Attempt = item.Attempt with
                    {
                        State = retry ? GateStates.InfraRetry
                            : failure == GateFailureClasses.ExecutionTimeout ? GateStates.TimedOut : GateStates.InfraFailed,
                        FailureClassification = failure,
                        Outcome = retry ? failure : GateFailureClasses.GateInfra,
                        ReportedAt = now,
                    },
                    ContainmentDeadline = retry ? now.AddMinutes(5) : null,
                });
            }
            if (!ReferenceEquals(updated, record)) Save(updated);
        }
    }

    private void ValidateAuthority(MonolithGateAttempt attempt, GateAuthority authority,
        bool allowTerminalReplay = false)
    {
        if (attempt.Attempt.ExecutorId != authority.ExecutorId || attempt.InstanceId != authority.InstanceId
            || attempt.LeaseId != authority.LeaseId || attempt.Attempt.Fence != authority.Fence
            || authority.AuthorityEpoch != AuthorityEpoch)
            throw new GateConflictException("stale-gate-fence",
                "Gate lease, executor, instance, fence or epoch is stale.");
        if (allowTerminalReplay && GateStates.IsTerminal(attempt.Attempt.State)) return;
        if (attempt.Attempt.State == GateStates.InfraRetry || GateStates.IsTerminal(attempt.Attempt.State)
            || attempt.ExpiresAt <= Now)
            throw new GateConflictException("stale-gate-fence", "Gate lease is no longer active.");
    }

    private GateStatus Status(MonolithGateRecord record)
    {
        var attempts = record.Attempts.Select(item => item.Attempt).ToArray();
        var current = record.Attempts[^1];
        var state = current.Attempt.State;
        return new GateStatus(record.Subject, attempts, current.Report,
            Math.Max(0, ((current.Attempt.ClaimedAt ?? Now) - current.Attempt.CreatedAt).TotalSeconds),
            GateStates.IsTerminal(state) || state is GateStates.Queued or GateStates.InfraRetry
                ? null : current.Attempt.HostId,
            state, attempts.Length, current.Attempt.Deadline, current.Report?.TestedSha,
            GateStates.IsTerminal(state) ? current.Attempt.Outcome : null, current.Report is not null);
    }

    private static GateLease Lease(MonolithGateAttempt attempt)
        => new(attempt.LeaseId!, attempt.Attempt.AttemptId, attempt.Attempt.ExecutorId!,
            attempt.InstanceId!, attempt.Attempt.HostId!, attempt.Attempt.Fence, AuthorityEpoch,
            attempt.AcquiredAt!.Value, attempt.ExpiresAt!.Value, attempt.ResourceNamespace!, attempt.PortBase);

    private static MonolithGateAttempt Queued(string subjectId, int number, DateTime now)
        => new(new GateAttempt($"gat_{Guid.NewGuid():N}", subjectId, number, GateStates.Queued,
            null, null, null, null, now, null, null, null, 0, null));

    private static bool IsActive(string state) => state is GateStates.Claimed or GateStates.Materializing
        or GateStates.Running or GateStates.Reporting or GateStates.Cleaning;

    private static int Ttl(int seconds) => Math.Clamp(seconds, 30, 600);

    private static MonolithGateRecord Replace(MonolithGateRecord record, MonolithGateAttempt attempt)
        => record with
        {
            Attempts = record.Attempts.Select(item =>
                item.Attempt.AttemptId == attempt.Attempt.AttemptId ? attempt : item).ToArray(),
        };

    private MonolithGateRecord RequiredSubject(string subjectId)
        => _subjects.GetValueOrDefault(subjectId) ?? throw new KeyNotFoundException("Gate subject was not found.");

    private (MonolithGateRecord Record, MonolithGateAttempt Attempt) RequiredAttempt(string attemptId)
    {
        foreach (var record in _subjects.Values)
            if (record.Attempts.FirstOrDefault(item => item.Attempt.AttemptId == attemptId) is { } attempt)
                return (record, attempt);
        throw new KeyNotFoundException("Gate attempt was not found.");
    }

    private void Save(MonolithGateRecord record)
    {
        _subjects[record.Subject.SubjectId] = record;
        if (_directory is null) return;
        var path = Path.Combine(_directory, record.Subject.SubjectId + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, record, Json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
