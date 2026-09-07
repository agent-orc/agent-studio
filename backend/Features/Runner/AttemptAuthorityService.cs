using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

public sealed record AttemptIndexedDeliveryRef(
    string TaskKey,
    string Ref,
    string ResultSha,
    DateTime RecordedAt);

/// <summary>
/// Durable Task Server authority for coding and review attempts. This store owns
/// identity, leases, monotonically increasing fences, the authority epoch,
/// immutable review subjects, terminal facts, and delivery idempotency. It does
/// not resolve or materialize a repository and never runs a product command.
/// </summary>
public sealed class AttemptAuthorityService
{
    public const string RelativePath = ".metadata/attempt-authority.json";
    public const int DefaultTerminalRetentionCount = 2_000;
    public const int ReviewInfrastructureRetryBudget = 3;
    public const string UnmaterializableReviewSubjectReason = "review-subject-unmaterialisierbar";
    private const int CurrentSchemaVersion = 4;
    private const int ArchiveSchemaVersion = 1;
    private const string ArchiveFilePattern = "attempt-authority.archive-*.json";
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LegacyEnvelopeTerminalizeGrace = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger<AttemptAuthorityService> _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly IAtomicJsonFileWriter _writer;
    private readonly int _terminalRetentionCount;
    private AuthorityState _state;

    public AttemptAuthorityService(
        IConfiguration configuration,
        ILogger<AttemptAuthorityService> logger,
        Func<DateTime>? utcNow = null,
        IAtomicJsonFileWriter? writer = null)
    {
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _writer = writer ?? new AtomicJsonFileWriter();
        var root = configuration["TaskRepository"];
        _path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, RelativePath);
        _state = Load();
        var requiresCompactionMigration = _state.SchemaVersion < CurrentSchemaVersion;
        NormalizeLoadedState();
        if (_state.AuthorityEpoch <= 0) _state.AuthorityEpoch = 1;
        _terminalRetentionCount = configuration.GetValue<int?>("AttemptAuthority:TerminalRetentionCount")
            ?? DefaultTerminalRetentionCount;
        if (_terminalRetentionCount <= 0)
            throw new InvalidDataException("AttemptAuthority:TerminalRetentionCount must be greater than zero.");
        if (requiresCompactionMigration && _path is not null)
            PersistLocked(forceCompaction: true);
    }

    internal AttemptAuthorityService(ILogger<AttemptAuthorityService> logger, Func<DateTime>? utcNow = null)
        : this(new ConfigurationBuilder().Build(), logger, utcNow)
    {
    }

    public long AuthorityEpoch
    {
        get { lock (_gate) return _state.AuthorityEpoch; }
    }

    public AttemptWriteResult AcquireRun(
        string taskKey,
        string repositoryId,
        string? sourceAttemptId,
        string executorId,
        string hostId,
        int? requestedTtlSeconds,
        string idempotencyKey,
        string? executorDisplayName = null,
        string? backendName = null,
        int processId = 0,
        string? clientId = null,
        string? leaseInstanceId = null)
    {
        if (Blank(taskKey) || Blank(repositoryId) || Blank(executorId) || Blank(hostId) || Blank(idempotencyKey))
            return InvalidRun("TaskKey, RepositoryId, ExecutorId, HostId, and IdempotencyKey are required.");

        lock (_gate)
        {
            var deliveryKey = DeliveryKey("acquire", idempotencyKey);
            var duplicate = FindIdempotentRun(taskKey, deliveryKey);
            if (duplicate is not null)
                return ClassifyRunAcquireReplay(duplicate, executorId);

            var now = _utcNow();
            var current = CurrentRun(taskKey);
            if (current is { State: AttemptLifecycleState.Leased, Lease: not null }
                && current.Lease.ExpiresAt > now)
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.InvalidState,
                    current.AttemptId,
                    $"Task '{Normalize(taskKey)}' is leased by '{current.Lease.ExecutorId}' until {current.Lease.ExpiresAt:o}.",
                    RunAttempt: ToDto(current));
            }

            if (current is not null && !Terminal(current.State))
            {
                current.State = AttemptLifecycleState.Superseded;
                current.TerminalAt = now;
                current.TerminalOutcome = "superseded";
                current.TerminalReason = current.AuthorityEpoch != _state.AuthorityEpoch
                    ? "draining authority epoch lease expired and a new executor took authority"
                    : "lease expired and a new executor took authority";
            }

            var fence = NextFenceLocked(taskKey);
            var attempt = new RunAttemptRecord
            {
                AttemptId = NewId("run"),
                TaskKey = Normalize(taskKey),
                RepositoryId = Normalize(repositoryId),
                SourceAttemptId = NormalizeNull(sourceAttemptId),
                State = AttemptLifecycleState.Leased,
                LastFence = fence,
                AuthorityEpoch = _state.AuthorityEpoch,
                CreatedAt = now,
                Lease = NewLease(executorId, hostId, fence, requestedTtlSeconds, now,
                    executorDisplayName, backendName, processId, clientId, leaseInstanceId),
                IdempotencyKeys = [deliveryKey],
            };
            _state.RunAttempts.Add(attempt);
            _state.CurrentRunByTask[Normalize(taskKey)] = attempt.AttemptId;
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, attempt.AttemptId, RunAttempt: ToDto(attempt));
        }
    }

    /// <summary>
    /// Looks up a previously successful acquire delivery without requiring the
    /// caller to know which task the server selected. Daemon claim replay uses
    /// this before scanning Ready tasks, because the original card is already
    /// in Progress by then. This is read-only and can never mint authority.
    /// </summary>
    public AttemptWriteResult ReplayRunAcquire(string executorId, string idempotencyKey)
    {
        if (Blank(executorId) || Blank(idempotencyKey))
            return InvalidRun("ExecutorId and IdempotencyKey are required.");

        lock (_gate)
        {
            var deliveryKey = DeliveryKey("acquire", idempotencyKey);
            var matches = _state.RunAttempts
                .Where(run => run.IdempotencyKeys.Contains(deliveryKey)
                              && Same(run.Lease?.ExecutorId, executorId))
                .Take(2)
                .ToList();
            if (matches.Count == 0)
                return new AttemptWriteResult(AttemptWriteStatus.NotFound, string.Empty);
            if (matches.Count > 1)
                return InvalidRun("The acquire idempotency key is ambiguous for this executor.");

            return ClassifyRunAcquireReplay(matches[0], executorId);
        }
    }

    public AttemptWriteResult RenewRun(
        AttemptWriteReference write,
        string executorId,
        int? requestedTtlSeconds,
        string? leaseId = null)
    {
        lock (_gate)
        {
            var replayed = FindRun(write.AttemptId);
            if (replayed is not null
                && replayed.IdempotencyKeys.Contains(DeliveryKey("renew", write.IdempotencyKey)))
            {
                return ClassifyRunLeaseReplay(replayed, write, executorId, leaseId);
            }
            var validation = ValidateRunWriteLocked(
                write, executorId, recordIdempotency: false, idempotencyScope: "renew", leaseId: leaseId);
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            var run = FindRun(write.AttemptId)!;
            var now = _utcNow();
            run.Lease!.ExpiresAt = now.Add(NormalizeTtl(requestedTtlSeconds));
            run.Lease.LastHeartbeat = now;
            run.IdempotencyKeys.Add(DeliveryKey("renew", write.IdempotencyKey));
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult ReleaseRun(
        AttemptWriteReference write,
        string executorId,
        string? leaseId = null)
    {
        lock (_gate)
        {
            var existing = FindRun(write.AttemptId);
            var deliveryKey = DeliveryKey("release", write.IdempotencyKey);
            if (existing is not null && existing.IdempotencyKeys.Contains(deliveryKey))
            {
                var duplicateStatus = IsCurrentRun(existing)
                    ? AttemptWriteStatus.Duplicate
                    : AttemptWriteStatus.Superseded;
                return new AttemptWriteResult(duplicateStatus, existing.AttemptId, RunAttempt: ToDto(existing));
            }

            // Completion revokes authority through state, but the final cleanup
            // delivery is still acknowledged and the historical lease facts are
            // retained for audit and restart reconstruction.
            if (existing is not null && Terminal(existing.State))
            {
                if (!MatchesAttemptEpoch(write.AuthorityEpoch, existing.AuthorityEpoch))
                    return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, existing.AttemptId, RunAttempt: ToDto(existing));
                if (!IsCurrentRun(existing) || existing.State == AttemptLifecycleState.Superseded)
                    return new AttemptWriteResult(AttemptWriteStatus.Superseded, existing.AttemptId, RunAttempt: ToDto(existing));
                if (write.Fence != existing.LastFence)
                    return new AttemptWriteResult(AttemptWriteStatus.StaleFence, existing.AttemptId, RunAttempt: ToDto(existing));
                if (existing.Lease is null || !Same(existing.Lease.ExecutorId, executorId))
                    return new AttemptWriteResult(AttemptWriteStatus.StaleFence, existing.AttemptId,
                        "Executor does not own this attempt's terminal cleanup.", RunAttempt: ToDto(existing));
                if (!Blank(leaseId) && !Same(existing.Lease.LeaseId, leaseId))
                    return new AttemptWriteResult(AttemptWriteStatus.StaleFence, existing.AttemptId,
                        "Lease ID does not own this attempt's terminal cleanup.", RunAttempt: ToDto(existing));

                existing.IdempotencyKeys.Add(deliveryKey);
                PersistLocked();
                return new AttemptWriteResult(AttemptWriteStatus.Accepted, existing.AttemptId, RunAttempt: ToDto(existing));
            }

            var validation = ValidateRunWriteLocked(
                write, executorId, recordIdempotency: false, idempotencyScope: "release", leaseId: leaseId);
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            var run = FindRun(write.AttemptId)!;
            run.IdempotencyKeys.Add(deliveryKey);
            if (!Terminal(run.State)) run.State = AttemptLifecycleState.Pending;
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult AcceptRunWrite(AttemptWriteReference write)
        => ExecuteRunWrite(write, "write", expectedTaskKey: null, static () => { });

    public AttemptWriteResult RecordEvidenceDigest(AttemptWriteReference write, string digest)
    {
        if (Blank(digest)) return InvalidRun("Evidence digest is required.");
        return ExecuteRunWrite(
            write, "evidence", expectedTaskKey: null, static () => { }, digest);
    }

    /// <summary>
    /// Validates one fenced delivery and performs its Task Server side effect
    /// inside the same authority critical section. The idempotency key is
    /// persisted after the side effect succeeds. Side effects that can become
    /// durable independently of the authority store must therefore deduplicate
    /// on this delivery identity as well; log ingestion embeds such a receipt
    /// in the same durable append.
    /// </summary>
    public AttemptWriteResult ExecuteRunWrite(
        AttemptWriteReference write,
        string operation,
        string? expectedTaskKey,
        Action sideEffect,
        string? evidenceDigest = null)
    {
        if (Blank(operation)) return InvalidRun("Write operation is required.");
        ArgumentNullException.ThrowIfNull(sideEffect);

        lock (_gate)
        {
            var result = ValidateRunWriteLocked(
                write,
                null,
                recordIdempotency: false,
                idempotencyScope: operation,
                expectedTaskKey: expectedTaskKey);
            if (result.Status != AttemptWriteStatus.Accepted) return result;

            sideEffect();
            var run = FindRun(write.AttemptId)!;
            run.IdempotencyKeys.Add(DeliveryKey(operation, write.IdempotencyKey));
            if (!Blank(evidenceDigest)
                && !run.EvidenceDigests.Contains(evidenceDigest!, StringComparer.Ordinal))
            {
                run.EvidenceDigests.Add(evidenceDigest!);
            }
            PersistLocked();
            return new AttemptWriteResult(
                AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult SettleRun(SettleRunAttemptRequest request)
    {
        lock (_gate)
        {
            var existing = FindRun(request.Write.AttemptId);
            var deliveryKey = DeliveryKey("settle", request.Write.IdempotencyKey);
            if (existing is not null && existing.IdempotencyKeys.Contains(deliveryKey))
            {
                var duplicateStatus = IsCurrentRun(existing)
                    ? AttemptWriteStatus.Duplicate
                    : AttemptWriteStatus.Superseded;
                return new AttemptWriteResult(duplicateStatus, existing.AttemptId, RunAttempt: ToDto(existing));
            }

            var validation = ValidateRunWriteLocked(
                request.Write,
                request.ExecutorId,
                recordIdempotency: false,
                idempotencyScope: "settle",
                leaseId: request.LeaseId,
                expectedTaskKey: request.ExpectedTaskKey);
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            var run = FindRun(request.Write.AttemptId)!;
            var normalizedOutcome = Normalize(request.Outcome).ToLowerInvariant();
            if (request.RequireResultSha
                && normalizedOutcome is ("done" or "noop")
                && Blank(request.ResultSha))
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.Invalid,
                    run.AttemptId,
                    "Successful remote completion requires the immutable Result-SHA.",
                    RunAttempt: ToDto(run));
            }
            if (request.ResultEnvelope is not null)
            {
                try
                {
                    AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Validate(request.ResultEnvelope);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException)
                {
                    return new AttemptWriteResult(
                        AttemptWriteStatus.Invalid,
                        run.AttemptId,
                        exception.Message,
                        RunAttempt: ToDto(run));
                }
                var computedDigest =
                    AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(request.ResultEnvelope);
                if (!Same(request.ResultEnvelope.SourceRunAttemptId, run.AttemptId)
                    || !Same(request.ResultEnvelope.RepositoryId, run.RepositoryId)
                    || !Same(request.ResultEnvelope.ResultSha, request.ResultSha)
                    || (!Blank(request.ResultEnvelopeDigest)
                        && !Same(computedDigest, request.ResultEnvelopeDigest)))
                {
                    return new AttemptWriteResult(
                        AttemptWriteStatus.SubjectMismatch,
                        run.AttemptId,
                        "Immutable result envelope does not match the fenced RunAttempt.",
                        RunAttempt: ToDto(run));
                }
                run.ResultEnvelope = request.ResultEnvelope;
                run.ResultEnvelopeDigest = computedDigest;
            }

            run.IdempotencyKeys.Add(deliveryKey);
            run.State = normalizedOutcome is "done" or "noop"
                ? AttemptLifecycleState.Completed
                : normalizedOutcome is "cancelled" ? AttemptLifecycleState.Cancelled : AttemptLifecycleState.Failed;
            run.TerminalAt = _utcNow();
            run.TerminalOutcome = normalizedOutcome;
            run.TerminalReason = NormalizeNull(request.Reason);
            run.ResultSha = NormalizeNull(request.ResultSha)?.ToLowerInvariant();
            if (run.State == AttemptLifecycleState.Completed
                && CurrentReview(run.TaskKey) is { } olderReview
                && !Terminal(olderReview.State)
                && !Same(olderReview.SourceRunAttemptId, run.AttemptId))
            {
                olderReview.State = AttemptLifecycleState.Superseded;
                olderReview.Outcome = ReviewTerminalOutcome.Superseded;
                olderReview.TerminalAt = run.TerminalAt;
                olderReview.TerminalReason = $"RunAttempt {run.AttemptId} produced a newer immutable result.";
            }
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult CreateReviewAttempt(CreateReviewAttemptRequest request)
    {
        if (Blank(request.TaskKey) || Blank(request.RepositoryId) || Blank(request.ExpectedResultSha)
            || Blank(request.SourceRunAttemptId) || Blank(request.TaskRequirementsHash)
            || Blank(request.ReviewPolicyHash) || Blank(request.IdempotencyKey))
            return new AttemptWriteResult(AttemptWriteStatus.Invalid, string.Empty, "Complete immutable ReviewSubject identity and IdempotencyKey are required.");

        lock (_gate)
        {
            var deliveryKey = DeliveryKey("create", request.IdempotencyKey);
            var duplicate = FindIdempotentReview(request.TaskKey, deliveryKey);
            if (duplicate is not null)
            {
                if (!IsCurrentReview(duplicate) || duplicate.State == AttemptLifecycleState.Superseded)
                    return new AttemptWriteResult(AttemptWriteStatus.Superseded, duplicate.AttemptId, ReviewAttempt: ToDto(duplicate));
                if (!KnownAttemptEpoch(duplicate.AuthorityEpoch))
                    return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, duplicate.AttemptId, ReviewAttempt: ToDto(duplicate));
                return new AttemptWriteResult(AttemptWriteStatus.Duplicate, duplicate.AttemptId, ReviewAttempt: ToDto(duplicate));
            }

            var run = FindRun(request.SourceRunAttemptId);
            var expectedSha = Normalize(request.ExpectedResultSha).ToLowerInvariant();
            if (run is null || run.State != AttemptLifecycleState.Completed
                || !Same(run.TaskKey, request.TaskKey)
                || !Same(run.RepositoryId, request.RepositoryId)
                || !Same(run.ResultSha, expectedSha))
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.SubjectMismatch,
                    string.Empty,
                    "ReviewAttempt source must be the completed RunAttempt for the same task, repository, and exact Result-SHA.");
            }
            if (!IsCurrentRun(run))
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.Superseded,
                    run.AttemptId,
                    "ReviewAttempt source is no longer the current RunAttempt for this task.",
                    RunAttempt: ToDto(run));
            }

            ReviewAttemptRecord? sourceReview = null;
            if (!Blank(request.SourceReviewAttemptId))
            {
                sourceReview = FindReview(request.SourceReviewAttemptId!);
                if (sourceReview is null
                    || sourceReview.Outcome is not (ReviewTerminalOutcome.InfrastructureFailure or ReviewTerminalOutcome.Inconclusive or ReviewTerminalOutcome.Cancellation)
                    || !Same(sourceReview.Subject.ExpectedResultSha, expectedSha))
                {
                    return new AttemptWriteResult(AttemptWriteStatus.InvalidState, string.Empty,
                        "A review retry must link a terminal infrastructure, inconclusive, or cancelled ReviewAttempt for the same subject.");
                }
            }

            var now = _utcNow();
            var evidence = (request.EvidenceDigestInputs ?? [])
                .Select(Normalize)
                .Where(x => x.Length > 0)
                .Order(StringComparer.Ordinal)
                .ToList();
            var subjectId = SubjectId(request.RepositoryId, expectedSha, request.SourceRunAttemptId,
                request.TaskRequirementsHash, request.ReviewPolicyHash, evidence);
            if (sourceReview is not null && !Same(sourceReview.Subject.SubjectId, subjectId))
            {
                return new AttemptWriteResult(AttemptWriteStatus.SubjectMismatch, sourceReview.AttemptId,
                    "A review infrastructure retry must retain the exact immutable ReviewSubject identity.",
                    ReviewAttempt: ToDto(sourceReview));
            }
            ReviewSubjectRecord subject;
            if (sourceReview is null)
            {
                subject = new ReviewSubjectRecord
                {
                    SubjectId = subjectId,
                    RepositoryId = Normalize(request.RepositoryId),
                    ExpectedResultSha = expectedSha,
                    SourceRunAttemptId = run.AttemptId,
                    TaskRequirementsHash = Normalize(request.TaskRequirementsHash),
                    ReviewPolicyHash = Normalize(request.ReviewPolicyHash),
                    EvidenceDigestInputs = evidence,
                    RepositoryUrl = NormalizeNull(request.RepositoryUrl),
                    ResultRef = NormalizeNull(request.ResultRef),
                    Plan = request.Plan,
                    CreatedAt = now,
                };
            }
            else if (ReviewInfrastructureRetryPlanPolicy.RequiresRebuild(
                         sourceReview.FailureClassification)
                     && request.Plan is not null)
            {
                subject = CopySubjectWithPlan(sourceReview.Subject, request.Plan);
            }
            else
            {
                subject = sourceReview.Subject;
            }

            var current = CurrentReview(request.TaskKey);
            if (current is not null && !Terminal(current.State))
            {
                current.State = AttemptLifecycleState.Superseded;
                current.Outcome = ReviewTerminalOutcome.Superseded;
                current.TerminalAt = now;
                current.TerminalReason = Same(current.Subject.SubjectId, subjectId)
                    ? "replaced by review retry"
                    : "a newer immutable result became current";
            }

            var attempt = new ReviewAttemptRecord
            {
                AttemptId = NewId("review"),
                TaskKey = Normalize(request.TaskKey),
                RepositoryId = Normalize(request.RepositoryId),
                SourceRunAttemptId = run.AttemptId,
                SourceReviewAttemptId = sourceReview?.AttemptId,
                Subject = subject,
                State = AttemptLifecycleState.Pending,
                AuthorityEpoch = _state.AuthorityEpoch,
                CreatedAt = now,
                IdempotencyKeys = [deliveryKey],
            };
            _state.ReviewAttempts.Add(attempt);
            _state.CurrentReviewByTask[attempt.TaskKey] = attempt.AttemptId;
            _state.CurrentSubjectByTask[attempt.TaskKey] = subject;
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, attempt.AttemptId, ReviewAttempt: ToDto(attempt));
        }
    }

    public AttemptWriteResult RenewReview(AttemptWriteReference write, string executorId, int? requestedTtlSeconds)
    {
        lock (_gate)
        {
            var review = FindReview(write.AttemptId);
            if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, write.AttemptId);
            var deliveryKey = DeliveryKey("renew", write.IdempotencyKey);
            if (review.IdempotencyKeys.Contains(deliveryKey))
                return ClassifyReviewLeaseReplay(review, executorId, write: write);
            var validation = ValidateReviewWriteLocked(write, "renew");
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            if (!Same(review.Lease!.ExecutorId, executorId))
                return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId,
                    "Executor does not own this ReviewAttempt lease.", ReviewAttempt: ToDto(review));

            var now = _utcNow();
            review.Lease.ExpiresAt = now.Add(NormalizeTtl(requestedTtlSeconds));
            review.Lease.LastHeartbeat = now;
            review.IdempotencyKeys.Add(deliveryKey);
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
        }
    }

    public AttemptWriteResult ClaimReview(
        string attemptId,
        string executorId,
        string hostId,
        int? requestedTtlSeconds,
        string idempotencyKey,
        string? instanceId = null)
    {
        if (Blank(attemptId) || Blank(executorId) || Blank(hostId) || Blank(idempotencyKey))
            return new AttemptWriteResult(
                AttemptWriteStatus.Invalid,
                Normalize(attemptId),
                "AttemptId, ExecutorId, HostId, and IdempotencyKey are required.");

        lock (_gate)
        {
            var review = FindReview(attemptId);
            if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, Normalize(attemptId));
            var deliveryKey = DeliveryKey("claim", idempotencyKey);
            if (review.IdempotencyKeys.Contains(deliveryKey))
            {
                var replay = ClassifyReviewLeaseReplay(review, executorId, claimDeliveryKey: deliveryKey);
                // A crash-restarted executor re-claims with the same identity and
                // therefore the same delivery key. Once the recorded lease is dead
                // there is no surviving authority to replay - this is a takeover
                // request, so fall through and mint a fresh lease instead of
                // bouncing the claim poll with LeaseExpired (which crash-looped
                // the review daemon: claim -> run -> lease dies -> 409 -> restart).
                if (replay.Status != AttemptWriteStatus.LeaseExpired)
                    return replay;
                // BUT: if the dead lease was claimed with exactly this delivery
                // key, the claiming PROCESS is still alive (a restart changes the
                // instance id and thus the key) and its executor may still be
                // running - minting a fresh fence here would double-execute the
                // review and discard the first run as StaleFence. Keep answering
                // LeaseExpired; the daemon's in-flight dedup skips the re-claim.
                if (Same(review.CurrentClaimDeliveryKey, deliveryKey))
                    return replay;
            }
            if (!IsCurrentReview(review) || Terminal(review.State))
                return new AttemptWriteResult(AttemptWriteStatus.Superseded, review.AttemptId, ReviewAttempt: ToDto(review));
            var now = _utcNow();
            if (review.Lease is { } live && live.ExpiresAt > now)
                return new AttemptWriteResult(AttemptWriteStatus.InvalidState, review.AttemptId, $"ReviewAttempt is leased by '{live.ExecutorId}'.", ReviewAttempt: ToDto(review));

            var fence = NextFenceLocked(review.TaskKey);
            review.LastFence = fence;
            review.AuthorityEpoch = _state.AuthorityEpoch;
            review.State = AttemptLifecycleState.Leased;
            review.Lease = NewLease(
                executorId,
                hostId,
                fence,
                requestedTtlSeconds,
                now,
                clientId: NormalizeNull(instanceId));
            review.CurrentClaimDeliveryKey = deliveryKey;
            review.IdempotencyKeys.Add(deliveryKey);
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
        }
    }

    /// <summary>
    /// Atomically selects the oldest current unleased ReviewAttempt and claims it.
    /// The versioned review plane uses this operation so selection and fencing
    /// cannot race across concurrent review executors.
    /// </summary>
    public AttemptWriteResult ClaimNextReview(
        string executorId,
        string hostId,
        string instanceId,
        int? requestedTtlSeconds)
    {
        if (Blank(executorId) || Blank(hostId) || Blank(instanceId))
            return new AttemptWriteResult(
                AttemptWriteStatus.Invalid,
                string.Empty,
                "ExecutorId, HostId, and InstanceId are required.");

        lock (_gate)
        {
            var now = _utcNow();
            var candidate = _state.ReviewAttempts
                .Where(review => IsCurrentReview(review) && !Terminal(review.State))
                .Where(review => review.Lease is null || review.Lease.ExpiresAt <= now)
                // A subject whose source run carries no Result-Envelope cannot be
                // materialized by any executor. Inside the terminalization grace it
                // is not yet evidence of a pre-plane completion either (the
                // completion ingest may still be in flight), so it is neither killed
                // nor handed out - it waits for its envelope or for the grace to run
                // out. Handing it out would burn a fenced attempt on a subject the
                // executor provably cannot check out.
                .Where(review => !IsUnmaterializableWithinGrace(review, now))
                .OrderBy(review => review.CreatedAt)
                .FirstOrDefault();
            if (candidate is null)
                return new AttemptWriteResult(AttemptWriteStatus.NotFound, string.Empty);

            return ClaimReview(
                candidate.AttemptId,
                executorId,
                hostId,
                requestedTtlSeconds,
                $"v1-review-claim:{executorId}:{instanceId}:{candidate.AttemptId}",
                instanceId);
        }
    }

    /// <summary>
    /// Revokes every non-terminal ReviewAttempt whose owning task is no longer
    /// eligible for Remote Review. The resolver returns <c>null</c> for an
    /// eligible task and a durable terminal reason for a task that must be
    /// rejected. A single authority-store write covers the whole sweep.
    /// </summary>
    public IReadOnlyList<ReviewAttemptDto> SupersedeOpenReviewAttempts(
        Func<string, string?> rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(rejectionReason);

        lock (_gate)
        {
            var now = _utcNow();
            var changed = new List<ReviewAttemptRecord>();
            foreach (var review in _state.ReviewAttempts.Where(review => !Terminal(review.State)))
            {
                var reason = NormalizeNull(rejectionReason(review.TaskKey));
                if (reason is null)
                    continue;

                review.State = AttemptLifecycleState.Superseded;
                review.Outcome = ReviewTerminalOutcome.Superseded;
                review.TerminalAt = now;
                review.TerminalReason = reason;
                changed.Add(review);
            }

            if (changed.Count == 0)
                return [];

            PersistLocked();
            foreach (var review in changed)
            {
                _logger.LogInformation(
                    "review-attempt-superseded attempt={AttemptId} task={TaskKey} reason={Reason}",
                    review.AttemptId,
                    review.TaskKey,
                    review.TerminalReason);
            }
            return changed.Select(ToDto).ToList();
        }
    }

    /// <summary>
    /// True while a ReviewAttempt has no materializable subject (its source run
    /// carries no Result-Envelope) but is still young enough that the missing
    /// envelope may simply be an in-flight completion ingest. Caller must hold
    /// <see cref="_gate"/>.
    /// </summary>
    private bool IsUnmaterializableWithinGrace(ReviewAttemptRecord review, DateTime now)
        => FindRun(review.SourceRunAttemptId)?.ResultEnvelope is null
           && now - review.CreatedAt < LegacyEnvelopeTerminalizeGrace;

    /// <summary>
    /// Returns whether another infrastructure retry may be created for the
    /// immutable subject owned by <paramref name="attemptId"/>. The initial
    /// ReviewAttempt is not a retry; at most three linked retry attempts may be
    /// scheduled after it.
    /// </summary>
    public bool HasReviewInfrastructureRetryBudget(string attemptId)
    {
        lock (_gate)
        {
            var review = FindReview(attemptId);
            if (review is null) return false;

            var retryCount = 0;
            var cursor = review;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                cursor.AttemptId,
            };
            while (!Blank(cursor.SourceReviewAttemptId)
                   && retryCount < ReviewInfrastructureRetryBudget)
            {
                retryCount++;
                var source = FindReview(cursor.SourceReviewAttemptId!);
                if (source is null || !visited.Add(source.AttemptId)) break;
                cursor = source;
            }

            return retryCount < ReviewInfrastructureRetryBudget;
        }
    }

    /// <summary>
    /// Returns the infrastructure retry chain that ends at
    /// <paramref name="attemptId"/>, oldest attempt first, with each attempt's
    /// terminal classification and reason. Retries are only ever linked after an
    /// infrastructure failure, so this is the evidence a repeat diagnosis needs
    /// (AGT-2220: four linked attempts, one identical cause, no named diagnosis).
    /// </summary>
    public IReadOnlyList<ReviewInfrastructureAttemptFact> ReviewInfrastructureChain(string attemptId)
    {
        lock (_gate)
        {
            var review = FindReview(attemptId);
            if (review is null) return [];

            var chain = new List<ReviewInfrastructureAttemptFact>();
            var cursor = review;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cursor is not null && visited.Add(cursor.AttemptId))
            {
                chain.Insert(0, new ReviewInfrastructureAttemptFact(
                    cursor.AttemptId,
                    cursor.FailureClassification,
                    cursor.TerminalReason));
                cursor = Blank(cursor.SourceReviewAttemptId)
                    ? null
                    : FindReview(cursor.SourceReviewAttemptId!);
            }
            return chain;
        }
    }

    /// <summary>
    /// Terminalizes current ReviewSubjects created from pre-plane completions
    /// that have no immutable Result-Envelope. Returning every matching current
    /// record lets the claim endpoint retry a failed lane escalation without
    /// changing the terminal attempt a second time.
    /// </summary>
    public IReadOnlyList<ReviewAttemptDto> TerminalizeLegacyReviewSubjectsWithoutResultEnvelope()
    {
        lock (_gate)
        {
            var now = _utcNow();
            var changed = false;
            foreach (var review in _state.ReviewAttempts.Where(IsCurrentReview))
            {
                var run = FindRun(review.SourceRunAttemptId);
                if (run?.ResultEnvelope is not null)
                    continue;

                // Grace window: the claim poll races the completion ingest, and
                // during a runner rollout in-flight old-binary completions land
                // without an envelope. A fresh review must not be killed by the
                // very first poll; only reviews that stayed envelope-less past
                // the grace are terminal evidence of a pre-plane completion.
                if (now - review.CreatedAt < LegacyEnvelopeTerminalizeGrace)
                    continue;

                // Never kill a review an executor is actively holding. The live
                // lease is running work, and terminalizing under it would clear
                // the lease and flip the state while its fenced report is still
                // on the way - the executor would then lose to a Superseded /
                // fence mismatch instead of reporting its own outcome. The
                // terminalization is picked up once the lease has expired.
                if (review.Lease is { } lease && lease.ExpiresAt > now)
                    continue;

                if (!Terminal(review.State))
                {
                    review.State = AttemptLifecycleState.Failed;
                    review.Outcome = ReviewTerminalOutcome.InfrastructureFailure;
                    review.FailureClassification = "SnapshotUnavailable";
                    review.TerminalReason = UnmaterializableReviewSubjectReason;
                    review.TerminalAt = now;
                    review.Lease = null;
                    changed = true;
                }
            }

            if (changed) PersistLocked();
            return _state.ReviewAttempts
                .Where(IsCurrentReview)
                .Where(review =>
                    review.State == AttemptLifecycleState.Failed
                    && review.Outcome == ReviewTerminalOutcome.InfrastructureFailure
                    && Same(review.FailureClassification, "SnapshotUnavailable")
                    && Same(review.TerminalReason, UnmaterializableReviewSubjectReason))
                .Select(ToDto)
                .ToList();
        }
    }

    /// <summary>
    /// Test seam: backdates a review's CreatedAt past the legacy-envelope
    /// terminalization grace so the terminalize path can be exercised without
    /// waiting out the real clock. Never called in production.
    /// </summary>
    internal void AgeReviewForTests(string reviewAttemptId, TimeSpan age)
    {
        lock (_gate)
        {
            var review = FindReview(reviewAttemptId)
                ?? throw new InvalidOperationException($"Unknown review '{reviewAttemptId}'.");
            review.CreatedAt -= age;
            PersistLocked();
        }
    }

    public AttemptWriteResult SettleReview(SettleReviewAttemptRequest request)
    {
        lock (_gate)
        {
            var review = FindReview(request.Write.AttemptId);
            if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, request.Write.AttemptId);
            var deliveryKey = DeliveryKey("settle", request.Write.IdempotencyKey);
            if (review.IdempotencyKeys.Contains(deliveryKey))
            {
                var delivered = review.Reports.LastOrDefault(report => Same(
                    report.IdempotencyKey,
                    request.Write.IdempotencyKey));
                if (delivered is not null
                    && (!Same(delivered.MaterializedResultSha, request.MaterializedResultSha)
                        || delivered.Outcome != request.Outcome
                        || !Same(delivered.FailureClassification, request.FailureClassification)
                        || !Same(delivered.Reason, request.Reason)))
                {
                    return new AttemptWriteResult(
                        AttemptWriteStatus.Invalid,
                        review.AttemptId,
                        "A review settlement idempotency key cannot be replayed with a different terminal payload.",
                        ReviewAttempt: ToDto(review));
                }
                var replayStatus = !MatchesAttemptEpoch(
                                       request.Write.AuthorityEpoch,
                                       review.AuthorityEpoch)
                    ? AttemptWriteStatus.AuthorityEpochMismatch
                    : !IsCurrentReview(review) || review.State == AttemptLifecycleState.Superseded
                        ? AttemptWriteStatus.Superseded
                        : request.Write.Fence != review.LastFence
                            ? AttemptWriteStatus.StaleFence
                            : AttemptWriteStatus.Duplicate;
                return new AttemptWriteResult(replayStatus, review.AttemptId, ReviewAttempt: ToDto(review));
            }
            var validation = ValidateReviewWriteLocked(request.Write, "settle");
            if (validation.Status != AttemptWriteStatus.Accepted)
            {
                if (validation.Status == AttemptWriteStatus.Superseded)
                {
                    review.IdempotencyKeys.Add(deliveryKey);
                    review.Reports.Add(ToReport(request, validation.Status, _utcNow()));
                    PersistLocked();
                    return validation with { ReviewAttempt = ToDto(review) };
                }
                return validation;
            }

            review.IdempotencyKeys.Add(deliveryKey);
            review.TestedResultSha = Normalize(request.MaterializedResultSha).ToLowerInvariant();
            review.TerminalAt = _utcNow();
            if (!Same(review.TestedResultSha, review.Subject.ExpectedResultSha))
            {
                review.Reports.Add(ToReport(request, AttemptWriteStatus.SubjectMismatch, review.TerminalAt.Value));
                review.State = AttemptLifecycleState.Failed;
                review.Outcome = ReviewTerminalOutcome.InfrastructureFailure;
                review.FailureClassification = "immutable-result-mismatch";
                review.TerminalReason = $"Expected {review.Subject.ExpectedResultSha}, materialized {review.TestedResultSha}.";
                PersistLocked();
                return new AttemptWriteResult(AttemptWriteStatus.SubjectMismatch, review.AttemptId,
                    review.TerminalReason, ReviewAttempt: ToDto(review));
            }

            review.Reports.Add(ToReport(request, AttemptWriteStatus.Accepted, review.TerminalAt.Value));
            review.Outcome = request.Outcome;
            review.FailureClassification = NormalizeNull(request.FailureClassification);
            review.TerminalReason = NormalizeNull(request.Reason);
            review.State = request.Outcome switch
            {
                ReviewTerminalOutcome.Pass => AttemptLifecycleState.Completed,
                ReviewTerminalOutcome.Cancellation => AttemptLifecycleState.Cancelled,
                ReviewTerminalOutcome.Superseded => AttemptLifecycleState.Superseded,
                _ => AttemptLifecycleState.Failed,
            };
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
        }
    }

    /// <summary>
    /// Starts a new claim generation without revoking already leased attempts.
    /// Existing leases keep their own epoch and drain through renewal and
    /// settlement. Every lease minted after this call uses the new epoch.
    /// </summary>
    public long RotateAuthorityEpoch(string reason)
    {
        lock (_gate)
        {
            var previousEpoch = _state.AuthorityEpoch;
            _state.AuthorityEpoch++;
            var now = _utcNow();
            var drainingRuns = _state.RunAttempts.Count(run =>
                run.State == AttemptLifecycleState.Leased
                && run.Lease is { } lease
                && lease.ExpiresAt > now);
            var drainingReviews = _state.ReviewAttempts.Count(review =>
                review.State == AttemptLifecycleState.Leased
                && review.Lease is { } lease
                && lease.ExpiresAt > now);
            PersistLocked();
            _logger.LogWarning(
                "attempt-authority-epoch-rotated previousEpoch={PreviousEpoch} epoch={Epoch} drainingRuns={DrainingRuns} drainingReviews={DrainingReviews} reason={Reason}",
                previousEpoch,
                _state.AuthorityEpoch,
                drainingRuns,
                drainingReviews,
                reason);
            return _state.AuthorityEpoch;
        }
    }

    /// <summary>
    /// Re-opens the renewal window for exact fenced attempts reported during
    /// runner registration. Expiry alone is recoverable because the durable
    /// attempt, lease, fence, epoch, executor, host, and lease instance still
    /// have to match. A takeover or terminal transition changes at least one of
    /// those facts and therefore cannot be reversed by re-registration.
    /// </summary>
    public IReadOnlyList<AgentStudio.TaskServer.Contracts.RunnerAttemptAdoption> ReAdoptRunnerAttempts(
        string runnerId,
        string hostId,
        string instanceId,
        IReadOnlyList<AgentStudio.TaskServer.Contracts.RunnerActiveAttempt>? attempts,
        int requestedTtlSeconds)
    {
        if (attempts is null || attempts.Count == 0) return [];
        if (Blank(runnerId) || Blank(hostId) || Blank(instanceId))
            throw new ArgumentException("Runner, host, and instance ids are required for attempt re-adoption.");
        if (attempts.Count > 256)
            throw new ArgumentException("At most 256 active attempts may be reported during registration.");

        lock (_gate)
        {
            var now = _utcNow();
            var expiresAt = now.Add(NormalizeTtl(requestedTtlSeconds));
            var changed = false;
            var results = new List<AgentStudio.TaskServer.Contracts.RunnerAttemptAdoption>(attempts.Count);
            foreach (var reported in attempts)
            {
                AgentStudio.TaskServer.Contracts.RunnerAttemptAdoption result;
                if (Same(reported.Kind, AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Coding))
                {
                    result = ReAdoptRunLocked(reported, runnerId, hostId, expiresAt, now, out var adopted);
                    changed |= adopted;
                }
                else if (Same(reported.Kind, AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Review))
                {
                    result = ReAdoptReviewLocked(
                        reported, runnerId, hostId, expiresAt, now, out var adopted);
                    changed |= adopted;
                }
                else
                {
                    result = Adoption(reported, "invalid", message: "Attempt kind must be coding or review.");
                }
                results.Add(result);
            }

            if (changed) PersistLocked();
            return results;
        }
    }

    public IReadOnlyList<ReviewAttemptDto> ListActiveReviewAttempts()
    {
        lock (_gate)
        {
            var now = _utcNow();
            return _state.ReviewAttempts
                .Where(IsCurrentReview)
                .Where(review =>
                    review.State == AttemptLifecycleState.Leased
                    && review.Lease is { } lease
                    && lease.ExpiresAt > now)
                .Select(ToDto)
                .ToList();
        }
    }

    public RunAttemptDto? GetRun(string attemptId)
    {
        lock (_gate) return FindRun(attemptId) is { } run ? ToDto(run) : null;
    }

    private AgentStudio.TaskServer.Contracts.RunnerAttemptAdoption ReAdoptRunLocked(
        AgentStudio.TaskServer.Contracts.RunnerActiveAttempt reported,
        string runnerId,
        string hostId,
        DateTime expiresAt,
        DateTime now,
        out bool adopted)
    {
        adopted = false;
        var run = FindRun(reported.AttemptId);
        if (run is null) return Adoption(reported, "not-found", message: "RunAttempt was not found.");
        if (!IsCurrentRun(run) || run.State == AttemptLifecycleState.Superseded)
            return Adoption(reported, "superseded", message: "RunAttempt is no longer current.");
        if (run.State != AttemptLifecycleState.Leased || run.Lease is null)
            return Adoption(reported, "invalid-state", message: $"RunAttempt is {run.State}.");
        if (!Same(run.TaskKey, reported.TaskKey)
            || !Same(run.Lease.ExecutorId, runnerId)
            || !Same(run.Lease.HostId, hostId)
            || !Same(run.Lease.LeaseId, reported.LeaseId)
            || run.LastFence != reported.Fence
            || run.AuthorityEpoch != reported.AuthorityEpoch
            || !Same(run.Lease.LeaseInstanceId, reported.LeaseInstanceId))
        {
            return Adoption(reported, "stale-authority", message: "RunAttempt authority does not match the durable server record.");
        }

        run.Lease.ExpiresAt = expiresAt;
        run.Lease.LastHeartbeat = now;
        adopted = true;
        return Adoption(reported, "adopted", expiresAt);
    }

    private AgentStudio.TaskServer.Contracts.RunnerAttemptAdoption ReAdoptReviewLocked(
        AgentStudio.TaskServer.Contracts.RunnerActiveAttempt reported,
        string runnerId,
        string hostId,
        DateTime expiresAt,
        DateTime now,
        out bool adopted)
    {
        adopted = false;
        var review = FindReview(reported.AttemptId);
        if (review is null) return Adoption(reported, "not-found", message: "ReviewAttempt was not found.");
        if (!IsCurrentReview(review) || review.State == AttemptLifecycleState.Superseded)
            return Adoption(reported, "superseded", message: "ReviewAttempt is no longer current.");
        if (review.State != AttemptLifecycleState.Leased || review.Lease is null)
            return Adoption(reported, "invalid-state", message: $"ReviewAttempt is {review.State}.");
        if (!Same(review.TaskKey, reported.TaskKey)
            || !Same(review.Lease.ExecutorId, runnerId)
            || !Same(review.Lease.HostId, hostId)
            || !Same(review.Lease.LeaseId, reported.LeaseId)
            || review.LastFence != reported.Fence
            || review.AuthorityEpoch != reported.AuthorityEpoch
            || !Same(review.Lease.ClientId, reported.LeaseInstanceId))
        {
            return Adoption(reported, "stale-authority", message: "ReviewAttempt authority does not match the durable server record.");
        }

        review.Lease.ExpiresAt = expiresAt;
        review.Lease.LastHeartbeat = now;
        adopted = true;
        return Adoption(reported, "adopted", expiresAt);
    }

    private static AgentStudio.TaskServer.Contracts.RunnerAttemptAdoption Adoption(
        AgentStudio.TaskServer.Contracts.RunnerActiveAttempt reported,
        string status,
        DateTime? expiresAt = null,
        string? message = null)
        => new(reported.Kind, reported.AttemptId, reported.TaskKey, status, expiresAt, message);

    public AgentStudio.TaskServer.Contracts.ResultHandoffDto? GetResultHandoff(string attemptId)
    {
        lock (_gate)
        {
            var run = FindRun(attemptId);
            if (run?.ResultEnvelope is null || Blank(run.ResultEnvelopeDigest)) return null;
            var acknowledgedAt = run.TerminalAt ?? run.CreatedAt;
            return new AgentStudio.TaskServer.Contracts.ResultHandoffDto(
                run.AttemptId,
                run.ResultEnvelope,
                run.ResultEnvelopeDigest!,
                1,
                acknowledgedAt,
                acknowledgedAt.AddDays(30));
        }
    }

    public ReviewAttemptDto? GetReview(string attemptId)
    {
        lock (_gate) return FindReview(attemptId) is { } review ? ToDto(review) : null;
    }

    public AttemptAuthorityProjection GetTaskProjection(string taskKey, bool includeArchived = false)
    {
        var key = Normalize(taskKey);
        long authorityEpoch;
        RunAttemptDto? currentRun;
        ReviewAttemptDto? currentReview;
        ReviewSubjectDto? currentSubject;
        List<RunAttemptDto> runs;
        List<ReviewAttemptDto> reviews;
        lock (_gate)
        {
            authorityEpoch = _state.AuthorityEpoch;
            currentRun = CurrentRun(key) is { } run ? ToDto(run) : null;
            currentReview = CurrentReview(key) is { } review ? ToDto(review) : null;
            currentSubject = _state.CurrentSubjectByTask.TryGetValue(key, out var subject)
                ? ToDto(subject)
                : null;
            runs = _state.RunAttempts
                .Where(x => Same(x.TaskKey, key))
                .Select(ToDto)
                .ToList();
            reviews = _state.ReviewAttempts
                .Where(x => Same(x.TaskKey, key))
                .Select(ToDto)
                .ToList();
        }

        // Archive I/O is an explicit history operation and must never hold the
        // authority gate needed by claim, lease, and report traffic. Taking the
        // live snapshot first also prevents a concurrent compaction from
        // disappearing between an earlier archive read and the live snapshot.
        if (includeArchived)
        {
            var archives = LoadArchivesForHistory();
            runs = runs
                .Concat(archives
                    .SelectMany(archive => archive.RunAttempts)
                    .Where(x => Same(x.TaskKey, key))
                    .Select(ToDto))
                .DistinctBy(x => x.AttemptId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            reviews = reviews
                .Concat(archives
                    .SelectMany(archive => archive.ReviewAttempts)
                    .Where(x => Same(x.TaskKey, key))
                    .Select(ToDto))
                .DistinctBy(x => x.AttemptId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        runs = runs.OrderBy(x => x.CreatedAt).ToList();
        reviews = reviews.OrderBy(x => x.CreatedAt).ToList();
        return new AttemptAuthorityProjection(
            key,
            authorityEpoch,
            currentRun,
            currentSubject,
            currentReview,
            runs,
            reviews,
            runs.Count == 0 && reviews.Count == 0);
    }

    /// <summary>
    /// Returns delivery refs already known by attempt authority without
    /// consulting Git. Archive files are loaded once for the whole project
    /// snapshot instead of once per task.
    /// </summary>
    public IReadOnlyList<AttemptIndexedDeliveryRef> GetIndexedDeliveryRefs(
        IEnumerable<string> taskKeys,
        bool includeArchived)
    {
        var keys = taskKeys.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return [];
        List<RunAttemptDto> runs;
        List<ReviewAttemptDto> reviews;
        lock (_gate)
        {
            runs = _state.RunAttempts
                .Where(attempt => keys.Contains(attempt.TaskKey))
                .Select(ToDto)
                .ToList();
            reviews = _state.ReviewAttempts
                .Where(attempt => keys.Contains(attempt.TaskKey))
                .Select(ToDto)
                .ToList();
        }

        if (includeArchived)
        {
            var archives = LoadArchivesForHistory();
            runs.AddRange(archives.SelectMany(archive => archive.RunAttempts)
                .Where(attempt => keys.Contains(attempt.TaskKey))
                .Select(ToDto));
            reviews.AddRange(archives.SelectMany(archive => archive.ReviewAttempts)
                .Where(attempt => keys.Contains(attempt.TaskKey))
                .Select(ToDto));
        }

        var indexed = runs
            .Where(run => run.ResultEnvelope?.ImmutableRemoteRef is { Length: > 0 })
            .Select(run => new AttemptIndexedDeliveryRef(
                run.TaskKey,
                run.ResultEnvelope!.ImmutableRemoteRef!,
                run.ResultEnvelope.ResultSha,
                run.TerminalAt ?? run.CreatedAt))
            .Concat(reviews
                .Where(review => !string.IsNullOrWhiteSpace(review.Subject.ResultRef))
                .Select(review => new AttemptIndexedDeliveryRef(
                    review.TaskKey,
                    review.Subject.ResultRef!,
                    review.Subject.ExpectedResultSha,
                    review.Subject.CreatedAt)))
            .Where(item => ReviewSubjectStore.IsValidResultSha(item.ResultSha))
            .DistinctBy(item => $"{item.TaskKey}\0{item.Ref}\0{item.ResultSha}",
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.RecordedAt)
            .ToList();
        return indexed;
    }

    private AttemptWriteResult ValidateRunWriteLocked(
        AttemptWriteReference write,
        string? executorId,
        bool recordIdempotency,
        string idempotencyScope,
        string? leaseId = null,
        string? expectedTaskKey = null)
    {
        if (Blank(write.AttemptId) || write.Fence <= 0 || write.AuthorityEpoch <= 0 || Blank(write.IdempotencyKey))
            return InvalidRun("AttemptId, Fence, AuthorityEpoch, and IdempotencyKey are required.");
        var run = FindRun(write.AttemptId);
        if (run is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, write.AttemptId);
        var deliveryKey = DeliveryKey(idempotencyScope, write.IdempotencyKey);
        if (run.IdempotencyKeys.Contains(deliveryKey))
            return new AttemptWriteResult(AttemptWriteStatus.Duplicate, run.AttemptId, RunAttempt: ToDto(run));
        if (!MatchesAttemptEpoch(write.AuthorityEpoch, run.AuthorityEpoch))
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, run.AttemptId, RunAttempt: ToDto(run));
        if (!IsCurrentRun(run) || run.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, run.AttemptId, RunAttempt: ToDto(run));
        if (write.Fence != run.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, RunAttempt: ToDto(run));
        if (run.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, run.AttemptId,
                $"RunAttempt is {run.State} and no longer has write authority.", RunAttempt: ToDto(run));
        if (run.Lease is null || run.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, run.AttemptId, RunAttempt: ToDto(run));
        if (!Blank(executorId) && !Same(run.Lease.ExecutorId, executorId))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, "Executor does not own this lease.", RunAttempt: ToDto(run));
        if (!Blank(leaseId) && !Same(run.Lease.LeaseId, leaseId))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, "Lease ID does not own this attempt.", RunAttempt: ToDto(run));
        if (!Blank(expectedTaskKey) && !Same(run.TaskKey, expectedTaskKey))
            return new AttemptWriteResult(AttemptWriteStatus.SubjectMismatch, run.AttemptId, "RunAttempt does not belong to the requested task.", RunAttempt: ToDto(run));
        if (recordIdempotency) run.IdempotencyKeys.Add(deliveryKey);
        return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
    }

    private AttemptWriteResult ClassifyRunLeaseReplay(
        RunAttemptRecord run,
        AttemptWriteReference write,
        string executorId,
        string? leaseId)
    {
        if (!MatchesAttemptEpoch(write.AuthorityEpoch, run.AuthorityEpoch))
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, run.AttemptId, RunAttempt: ToDto(run));
        if (!IsCurrentRun(run) || run.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, run.AttemptId, RunAttempt: ToDto(run));
        if (write.Fence != run.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, RunAttempt: ToDto(run));
        if (run.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, run.AttemptId, RunAttempt: ToDto(run));
        if (run.Lease is null || run.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, run.AttemptId, RunAttempt: ToDto(run));
        if (!Same(run.Lease.ExecutorId, executorId)
            || (!Blank(leaseId) && !Same(run.Lease.LeaseId, leaseId)))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, RunAttempt: ToDto(run));
        return new AttemptWriteResult(AttemptWriteStatus.Duplicate, run.AttemptId, RunAttempt: ToDto(run));
    }

    private AttemptWriteResult ClassifyRunAcquireReplay(
        RunAttemptRecord run,
        string executorId)
    {
        var replayedAt = _utcNow();
        if (!IsCurrentRun(run) || run.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(
                AttemptWriteStatus.Superseded, run.AttemptId, RunAttempt: ToDto(run));
        if (!KnownAttemptEpoch(run.AuthorityEpoch))
            return new AttemptWriteResult(
                AttemptWriteStatus.AuthorityEpochMismatch, run.AttemptId, RunAttempt: ToDto(run));
        if (run.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(
                AttemptWriteStatus.InvalidState,
                run.AttemptId,
                $"RunAttempt is {run.State} and cannot be reacquired by replaying an old delivery.",
                RunAttempt: ToDto(run));
        if (run.Lease is null || run.Lease.ExpiresAt <= replayedAt)
            return new AttemptWriteResult(
                AttemptWriteStatus.LeaseExpired, run.AttemptId, RunAttempt: ToDto(run));
        if (!Same(run.Lease.ExecutorId, executorId))
            return new AttemptWriteResult(
                AttemptWriteStatus.StaleFence,
                run.AttemptId,
                "The acquire delivery belongs to another executor.",
                RunAttempt: ToDto(run));
        return new AttemptWriteResult(
            AttemptWriteStatus.Duplicate, run.AttemptId, RunAttempt: ToDto(run));
    }

    private AttemptWriteResult ClassifyReviewLeaseReplay(
        ReviewAttemptRecord review,
        string executorId,
        AttemptWriteReference? write = null,
        string? claimDeliveryKey = null)
    {
        if (!KnownAttemptEpoch(review.AuthorityEpoch)
            || (write is not null
                && !MatchesAttemptEpoch(write.AuthorityEpoch, review.AuthorityEpoch)))
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!IsCurrentReview(review) || review.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, review.AttemptId, ReviewAttempt: ToDto(review));
        if (write is not null && write.Fence != review.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId, ReviewAttempt: ToDto(review));
        if (review.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, review.AttemptId, ReviewAttempt: ToDto(review));
        if (review.Lease is null || review.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!Same(review.Lease.ExecutorId, executorId))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!Blank(claimDeliveryKey) && !Same(review.CurrentClaimDeliveryKey, claimDeliveryKey))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId,
                "The claim delivery belongs to an older review lease.", ReviewAttempt: ToDto(review));
        return new AttemptWriteResult(AttemptWriteStatus.Duplicate, review.AttemptId, ReviewAttempt: ToDto(review));
    }

    private AttemptWriteResult ValidateReviewWriteLocked(AttemptWriteReference write, string idempotencyScope)
    {
        if (Blank(write.AttemptId) || write.Fence <= 0 || write.AuthorityEpoch <= 0 || Blank(write.IdempotencyKey))
            return new AttemptWriteResult(
                AttemptWriteStatus.Invalid,
                Normalize(write.AttemptId),
                "AttemptId, Fence, AuthorityEpoch, and IdempotencyKey are required.");
        var review = FindReview(write.AttemptId);
        if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, write.AttemptId);
        if (review.IdempotencyKeys.Contains(DeliveryKey(idempotencyScope, write.IdempotencyKey)))
            return new AttemptWriteResult(AttemptWriteStatus.Duplicate, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!MatchesAttemptEpoch(write.AuthorityEpoch, review.AuthorityEpoch))
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!IsCurrentReview(review) || review.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, review.AttemptId, ReviewAttempt: ToDto(review));
        if (write.Fence != review.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId, ReviewAttempt: ToDto(review));
        if (review.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, review.AttemptId,
                $"ReviewAttempt is {review.State} and no longer has write authority.", ReviewAttempt: ToDto(review));
        if (review.Lease is null || review.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, review.AttemptId, ReviewAttempt: ToDto(review));
        return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
    }

    private AttemptLeaseRecord NewLease(
        string executorId,
        string hostId,
        long fence,
        int? ttlSeconds,
        DateTime now,
        string? executorDisplayName = null,
        string? backendName = null,
        int processId = 0,
        string? clientId = null,
        string? leaseInstanceId = null) => new()
    {
        LeaseId = Guid.NewGuid().ToString("N"),
        Fence = fence,
        AuthorityEpoch = _state.AuthorityEpoch,
        ExecutorId = Normalize(executorId),
        HostId = Normalize(hostId),
        AcquiredAt = now,
        ExpiresAt = now.Add(NormalizeTtl(ttlSeconds)),
        LastHeartbeat = now,
        ExecutorDisplayName = NormalizeNull(executorDisplayName),
        BackendName = NormalizeNull(backendName),
        ProcessId = processId,
        ClientId = NormalizeNull(clientId),
        LeaseInstanceId = NormalizeNull(leaseInstanceId),
    };

    private long NextFenceLocked(string taskKey)
    {
        var key = Normalize(taskKey);
        var last = _state.LastFenceByTask.TryGetValue(key, out var value) ? value : 0;
        _state.LastFenceByTask[key] = last + 1;
        return last + 1;
    }

    private bool IsCurrentRun(RunAttemptRecord run)
        => _state.CurrentRunByTask.TryGetValue(run.TaskKey, out var id) && Same(id, run.AttemptId);

    private bool IsCurrentReview(ReviewAttemptRecord review)
        => _state.CurrentReviewByTask.TryGetValue(review.TaskKey, out var id) && Same(id, review.AttemptId);

    private RunAttemptRecord? CurrentRun(string taskKey)
        => _state.CurrentRunByTask.TryGetValue(Normalize(taskKey), out var id) ? FindRun(id) : null;

    private ReviewAttemptRecord? CurrentReview(string taskKey)
        => _state.CurrentReviewByTask.TryGetValue(Normalize(taskKey), out var id) ? FindReview(id) : null;

    private RunAttemptRecord? FindRun(string id) => _state.RunAttempts.FirstOrDefault(x => Same(x.AttemptId, id));
    private ReviewAttemptRecord? FindReview(string id) => _state.ReviewAttempts.FirstOrDefault(x => Same(x.AttemptId, id));
    private RunAttemptRecord? FindIdempotentRun(string taskKey, string key) => _state.RunAttempts.FirstOrDefault(
        x => Same(x.TaskKey, taskKey) && x.IdempotencyKeys.Contains(key));
    private ReviewAttemptRecord? FindIdempotentReview(string taskKey, string key) => _state.ReviewAttempts.FirstOrDefault(
        x => Same(x.TaskKey, taskKey) && x.IdempotencyKeys.Contains(key));

    private AuthorityState Load()
    {
        if (_path is null || !File.Exists(_path)) return new AuthorityState();
        try
        {
            return JsonSerializer.Deserialize<AuthorityState>(File.ReadAllText(_path), JsonOptions) ?? new AuthorityState();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Attempt authority store '{_path}' could not be loaded; refusing to reset fences.", ex);
        }
    }

    private void NormalizeLoadedState()
    {
        var migrateUnscopedIdempotency = _state.SchemaVersion < 2;
        _state.LastFenceByTask = new Dictionary<string, long>(
            _state.LastFenceByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.CurrentRunByTask = new Dictionary<string, string>(
            _state.CurrentRunByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.CurrentReviewByTask = new Dictionary<string, string>(
            _state.CurrentReviewByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.CurrentSubjectByTask = new Dictionary<string, ReviewSubjectRecord>(
            _state.CurrentSubjectByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.RunAttempts ??= [];
        _state.ReviewAttempts ??= [];
        NormalizeRecords(_state.RunAttempts, _state.ReviewAttempts, migrateUnscopedIdempotency);
        foreach (var review in _state.ReviewAttempts)
        {
            if (Blank(review.CurrentClaimDeliveryKey)
                && review.State == AttemptLifecycleState.Leased)
            {
                var historicalClaims = review.IdempotencyKeys
                    .Where(key => key.StartsWith("claim:", StringComparison.Ordinal))
                    .Take(2)
                    .ToList();
                // A single historical claim is unambiguous and can retain
                // restart idempotency. Multiple claims imply a takeover chain;
                // leave it unset so any old replay fails closed.
                if (historicalClaims.Count == 1)
                    review.CurrentClaimDeliveryKey = historicalClaims[0];
            }
        }
        _state.SchemaVersion = CurrentSchemaVersion;
    }

    private static HashSet<string> ExpandLegacyDeliveryKeys(
        IEnumerable<string> keys,
        IReadOnlyList<string> scopes) => keys
        .SelectMany(key => scopes.Select(scope => DeliveryKey(scope, key)))
        .ToHashSet(StringComparer.Ordinal);

    private void PersistLocked(bool forceCompaction = false)
    {
        if (_path is null) return;
        try
        {
            CompactTerminalAttemptsLocked(forceCompaction);
            _writer.Write(_path, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch
        {
            // No failed disk write may leave this process with authority that a
            // restarted Task Server would not recognize. Restore the last
            // durable snapshot before surfacing the persistence failure.
            _state = Load();
            NormalizeLoadedState();
            if (_state.AuthorityEpoch <= 0) _state.AuthorityEpoch = 1;
            throw;
        }
    }

    private void CompactTerminalAttemptsLocked(bool force)
    {
        var now = _utcNow();
        var newestTerminalAttempts = _state.RunAttempts
            .Where(run => Terminal(run.State))
            .Select(run => new TerminalAttemptReference(
                run.AttemptId,
                IsReview: false,
                run.TerminalAt ?? run.CreatedAt,
                run.CreatedAt))
            .Concat(_state.ReviewAttempts
                .Where(review => Terminal(review.State))
                .Select(review => new TerminalAttemptReference(
                    review.AttemptId,
                    IsReview: true,
                    review.TerminalAt ?? review.CreatedAt,
                    review.CreatedAt)))
            .OrderByDescending(attempt => attempt.TerminalAt)
            .ThenByDescending(attempt => attempt.CreatedAt)
            .ThenByDescending(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .Take(_terminalRetentionCount)
            .ToList();
        var terminalAttemptCount = _state.RunAttempts.Count(run => Terminal(run.State))
            + _state.ReviewAttempts.Count(review => Terminal(review.State));
        if (terminalAttemptCount <= _terminalRetentionCount)
        {
            if (force)
                _state.LastCompactedAt = now;
            return;
        }
        if (!force && _state.LastCompactedAt?.Date >= now.Date)
            return;

        var retainedRunIds = newestTerminalAttempts
            .Where(attempt => !attempt.IsReview)
            .Select(attempt => attempt.AttemptId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedReviewIds = newestTerminalAttempts
            .Where(attempt => attempt.IsReview)
            .Select(attempt => attempt.AttemptId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedReviewIds = new HashSet<string>(
            _state.CurrentReviewByTask.Values,
            StringComparer.OrdinalIgnoreCase);
        foreach (var review in _state.ReviewAttempts.Where(review => !Terminal(review.State)))
            protectedReviewIds.Add(review.AttemptId);

        var pendingReviewIds = new Stack<(string AttemptId, int Depth)>(
            protectedReviewIds.Select(id => (id, 0)));
        while (pendingReviewIds.TryPop(out var pending))
        {
            var review = FindReview(pending.AttemptId);
            if (review is null
                || Blank(review.SourceReviewAttemptId)
                || pending.Depth >= ReviewInfrastructureRetryBudget)
                continue;
            if (protectedReviewIds.Add(review.SourceReviewAttemptId!))
                pendingReviewIds.Push((review.SourceReviewAttemptId!, pending.Depth + 1));
        }

        var archivedReviews = _state.ReviewAttempts
            .Where(review => EligibleForArchive(
                review.State,
                retainedReviewIds.Contains(review.AttemptId),
                protectedReviewIds.Contains(review.AttemptId)))
            .ToList();
        var archivedReviewIds = archivedReviews
            .Select(review => review.AttemptId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var retainedReviews = _state.ReviewAttempts
            .Where(review => !archivedReviewIds.Contains(review.AttemptId))
            .ToList();
        var protectedRunIds = new HashSet<string>(
            _state.CurrentRunByTask.Values,
            StringComparer.OrdinalIgnoreCase);
        foreach (var run in _state.RunAttempts.Where(run => !Terminal(run.State)))
            protectedRunIds.Add(run.AttemptId);
        foreach (var review in retainedReviews)
            protectedRunIds.Add(review.SourceRunAttemptId);
        foreach (var subject in _state.CurrentSubjectByTask.Values)
            protectedRunIds.Add(subject.SourceRunAttemptId);

        var archivedRuns = _state.RunAttempts
            .Where(run => EligibleForArchive(
                run.State,
                retainedRunIds.Contains(run.AttemptId),
                protectedRunIds.Contains(run.AttemptId)))
            .ToList();

        if (archivedRuns.Count > 0 || archivedReviews.Count > 0)
        {
            var archivePath = ArchivePath(now);
            var archive = new AuthorityArchive
            {
                SchemaVersion = ArchiveSchemaVersion,
                ArchivedAt = now,
                RunAttempts = archivedRuns
                .OrderBy(run => run.CreatedAt)
                .ToList(),
                ReviewAttempts = archivedReviews
                .OrderBy(review => review.CreatedAt)
                .ToList(),
            };
            // A same-day file can only precede the durable live-file update
            // when an earlier rotation was interrupted. The live file still
            // contains the complete retry set in that case, so atomically
            // replacing the archive is both recoverable and avoids archive
            // reads while a lease or report mutation holds the authority gate.
            _writer.Write(archivePath, JsonSerializer.Serialize(archive, JsonOptions));

            var archivedRunIds = archivedRuns
                .Select(run => run.AttemptId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _state.RunAttempts.RemoveAll(run => archivedRunIds.Contains(run.AttemptId));
            _state.ReviewAttempts.RemoveAll(review => archivedReviewIds.Contains(review.AttemptId));
            _logger.LogInformation(
                "attempt-authority-compacted archive={ArchivePath} runs={RunCount} reviews={ReviewCount} terminalRetentionCount={TerminalRetentionCount}",
                archivePath,
                archivedRuns.Count,
                archivedReviews.Count,
                _terminalRetentionCount);
        }

        _state.LastCompactedAt = now;
    }

    private static bool EligibleForArchive(
        AttemptLifecycleState state,
        bool retainedByCount,
        bool protectedRecord)
        => !protectedRecord
           && Terminal(state)
           && !retainedByCount;

    private string ArchivePath(DateTime archivedAt)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Attempt authority path has no parent directory.");
        return Path.Combine(
            directory,
            $"attempt-authority.archive-{archivedAt:yyyy-MM-dd}.json");
    }

    private IReadOnlyList<AuthorityArchive> LoadArchivesForHistory()
    {
        if (_path is null)
            return [];
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, ArchiveFilePattern)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(LoadArchive)
            .ToList();
    }

    private static AuthorityArchive LoadArchive(string path)
    {
        try
        {
            var archive = JsonSerializer.Deserialize<AuthorityArchive>(
                File.ReadAllText(path),
                JsonOptions) ?? new AuthorityArchive();
            archive.RunAttempts ??= [];
            archive.ReviewAttempts ??= [];
            NormalizeRecords(archive.RunAttempts, archive.ReviewAttempts, migrateUnscopedIdempotency: false);
            return archive;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Attempt authority archive '{path}' could not be loaded.",
                ex);
        }
    }

    private static void NormalizeRecords(
        IEnumerable<RunAttemptRecord> runs,
        IEnumerable<ReviewAttemptRecord> reviews,
        bool migrateUnscopedIdempotency)
    {
        foreach (var run in runs)
        {
            run.IdempotencyKeys ??= [];
            if (migrateUnscopedIdempotency)
                run.IdempotencyKeys = ExpandLegacyDeliveryKeys(
                    run.IdempotencyKeys, ["acquire", "renew", "release", "write", "evidence", "settle"]);
            run.EvidenceDigests ??= [];
        }
        foreach (var review in reviews)
        {
            review.IdempotencyKeys ??= [];
            if (migrateUnscopedIdempotency)
                review.IdempotencyKeys = ExpandLegacyDeliveryKeys(
                    review.IdempotencyKeys, ["create", "renew", "claim", "settle"]);
            review.Reports ??= [];
            review.Subject ??= new ReviewSubjectRecord();
            review.Subject.EvidenceDigestInputs ??= [];
        }
    }

    private static string SubjectId(string repositoryId, string sha, string runId, string requirementsHash, string policyHash, IReadOnlyList<string> evidence)
        => "subject_" + Hash(string.Join("\n", [Normalize(repositoryId), sha, Normalize(runId), Normalize(requirementsHash), Normalize(policyHash), .. evidence]))[..24];

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
    private static string DeliveryKey(string scope, string key) => $"{scope}:{Normalize(key)}";
    private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
    private bool KnownAttemptEpoch(long attemptEpoch)
        => attemptEpoch > 0 && attemptEpoch <= _state.AuthorityEpoch;
    private bool MatchesAttemptEpoch(long writeEpoch, long attemptEpoch)
        => writeEpoch == attemptEpoch && KnownAttemptEpoch(attemptEpoch);
    private static bool Terminal(AttemptLifecycleState state) => state is AttemptLifecycleState.Completed or AttemptLifecycleState.Failed or AttemptLifecycleState.Cancelled or AttemptLifecycleState.Superseded;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
    private static string? NormalizeNull(string? value) => Blank(value) ? null : value!.Trim();
    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    private static TimeSpan NormalizeTtl(int? seconds)
    {
        var ttl = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : TimeSpan.FromMinutes(2);
        return ttl < MinTtl ? MinTtl : ttl > MaxTtl ? MaxTtl : ttl;
    }

    private static AttemptWriteResult InvalidRun(string message) => new(AttemptWriteStatus.Invalid, string.Empty, message);
    private static AttemptLeaseDto? ToDto(AttemptLeaseRecord? lease) => lease is null ? null : new(
        LeaseId: lease.LeaseId,
        Fence: lease.Fence,
        AuthorityEpoch: lease.AuthorityEpoch,
        ExecutorId: lease.ExecutorId,
        HostId: lease.HostId,
        AcquiredAt: lease.AcquiredAt,
        ExpiresAt: lease.ExpiresAt,
        LastHeartbeat: lease.LastHeartbeat,
        ExecutorDisplayName: lease.ExecutorDisplayName,
        BackendName: lease.BackendName,
        ProcessId: lease.ProcessId,
        ClientId: lease.ClientId,
        LeaseInstanceId: lease.LeaseInstanceId);
    private static ReviewSubjectDto ToDto(ReviewSubjectRecord subject) => new(
        SubjectId: subject.SubjectId,
        RepositoryId: subject.RepositoryId,
        ExpectedResultSha: subject.ExpectedResultSha,
        SourceRunAttemptId: subject.SourceRunAttemptId,
        TaskRequirementsHash: subject.TaskRequirementsHash,
        ReviewPolicyHash: subject.ReviewPolicyHash,
        EvidenceDigestInputs: subject.EvidenceDigestInputs,
        CreatedAt: subject.CreatedAt,
        RepositoryUrl: subject.RepositoryUrl,
        ResultRef: subject.ResultRef,
        Plan: subject.Plan);

    private static ReviewSubjectRecord CopySubjectWithPlan(
        ReviewSubjectRecord subject,
        AgentStudio.TaskServer.Contracts.ReviewPlanDto plan)
        => new()
        {
            SubjectId = subject.SubjectId,
            RepositoryId = subject.RepositoryId,
            ExpectedResultSha = subject.ExpectedResultSha,
            SourceRunAttemptId = subject.SourceRunAttemptId,
            TaskRequirementsHash = subject.TaskRequirementsHash,
            ReviewPolicyHash = subject.ReviewPolicyHash,
            EvidenceDigestInputs = [.. subject.EvidenceDigestInputs],
            RepositoryUrl = subject.RepositoryUrl,
            ResultRef = subject.ResultRef,
            Plan = plan,
            CreatedAt = subject.CreatedAt,
        };
    private static RunAttemptDto ToDto(RunAttemptRecord run) => new(
        AttemptId: run.AttemptId,
        TaskKey: run.TaskKey,
        RepositoryId: run.RepositoryId,
        SourceAttemptId: run.SourceAttemptId,
        State: run.State,
        Lease: ToDto(run.Lease),
        LastFence: run.LastFence,
        AuthorityEpoch: run.AuthorityEpoch,
        CreatedAt: run.CreatedAt,
        TerminalAt: run.TerminalAt,
        ResultSha: run.ResultSha,
        TerminalOutcome: run.TerminalOutcome,
        TerminalReason: run.TerminalReason,
        EvidenceDigests: run.EvidenceDigests,
        ResultEnvelope: run.ResultEnvelope,
        ResultEnvelopeDigest: run.ResultEnvelopeDigest);
    private static ReviewAttemptDto ToDto(ReviewAttemptRecord review) => new(
        AttemptId: review.AttemptId,
        TaskKey: review.TaskKey,
        RepositoryId: review.RepositoryId,
        SourceRunAttemptId: review.SourceRunAttemptId,
        SourceReviewAttemptId: review.SourceReviewAttemptId,
        Subject: ToDto(review.Subject),
        State: review.State,
        Lease: ToDto(review.Lease),
        LastFence: review.LastFence,
        AuthorityEpoch: review.AuthorityEpoch,
        CreatedAt: review.CreatedAt,
        TerminalAt: review.TerminalAt,
        Outcome: review.Outcome,
        FailureClassification: review.FailureClassification,
        TestedResultSha: review.TestedResultSha,
        TerminalReason: review.TerminalReason,
        Reports: review.Reports.Select(ToDto).ToList());
    private static ReviewReportDeliveryDto ToDto(ReviewReportDeliveryRecord report) => new(
        IdempotencyKey: report.IdempotencyKey,
        Fence: report.Fence,
        AuthorityEpoch: report.AuthorityEpoch,
        MaterializedResultSha: report.MaterializedResultSha,
        Outcome: report.Outcome,
        FailureClassification: report.FailureClassification,
        Reason: report.Reason,
        AuthorityStatus: report.AuthorityStatus,
        ReceivedAt: report.ReceivedAt);
    private static ReviewReportDeliveryRecord ToReport(
        SettleReviewAttemptRequest request,
        AttemptWriteStatus authorityStatus,
        DateTime receivedAt) => new()
    {
        IdempotencyKey = request.Write.IdempotencyKey,
        Fence = request.Write.Fence,
        AuthorityEpoch = request.Write.AuthorityEpoch,
        MaterializedResultSha = Normalize(request.MaterializedResultSha).ToLowerInvariant(),
        Outcome = request.Outcome,
        FailureClassification = NormalizeNull(request.FailureClassification),
        Reason = NormalizeNull(request.Reason),
        AuthorityStatus = authorityStatus,
        ReceivedAt = receivedAt,
    };

    private sealed class AuthorityState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public long AuthorityEpoch { get; set; } = 1;
        public DateTime? LastCompactedAt { get; set; }
        public Dictionary<string, long> LastFenceByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RunAttemptRecord> RunAttempts { get; set; } = [];
        public List<ReviewAttemptRecord> ReviewAttempts { get; set; } = [];
        public Dictionary<string, string> CurrentRunByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CurrentReviewByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReviewSubjectRecord> CurrentSubjectByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AuthorityArchive
    {
        public int SchemaVersion { get; set; } = ArchiveSchemaVersion;
        public DateTime ArchivedAt { get; set; }
        public List<RunAttemptRecord> RunAttempts { get; set; } = [];
        public List<ReviewAttemptRecord> ReviewAttempts { get; set; } = [];
    }

    private sealed record TerminalAttemptReference(
        string AttemptId,
        bool IsReview,
        DateTime TerminalAt,
        DateTime CreatedAt);

    private sealed class AttemptLeaseRecord
    {
        public string LeaseId { get; set; } = string.Empty;
        public long Fence { get; set; }
        public long AuthorityEpoch { get; set; }
        public string ExecutorId { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
        public DateTime AcquiredAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public string? ExecutorDisplayName { get; set; }
        public string? BackendName { get; set; }
        public int ProcessId { get; set; }
        public string? ClientId { get; set; }
        public string? LeaseInstanceId { get; set; }
    }

    private sealed class RunAttemptRecord
    {
        public string AttemptId { get; set; } = string.Empty;
        public string TaskKey { get; set; } = string.Empty;
        public string RepositoryId { get; set; } = string.Empty;
        public string? SourceAttemptId { get; set; }
        public AttemptLifecycleState State { get; set; }
        public AttemptLeaseRecord? Lease { get; set; }
        public long LastFence { get; set; }
        public long AuthorityEpoch { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? TerminalAt { get; set; }
        public string? ResultSha { get; set; }
        public string? TerminalOutcome { get; set; }
        public string? TerminalReason { get; set; }
        public List<string> EvidenceDigests { get; set; } = [];
        public AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope? ResultEnvelope { get; set; }
        public string? ResultEnvelopeDigest { get; set; }
        public HashSet<string> IdempotencyKeys { get; set; } = [];
    }

    private sealed class ReviewAttemptRecord
    {
        public string AttemptId { get; set; } = string.Empty;
        public string TaskKey { get; set; } = string.Empty;
        public string RepositoryId { get; set; } = string.Empty;
        public string SourceRunAttemptId { get; set; } = string.Empty;
        public string? SourceReviewAttemptId { get; set; }
        public ReviewSubjectRecord Subject { get; set; } = new();
        public AttemptLifecycleState State { get; set; }
        public AttemptLeaseRecord? Lease { get; set; }
        public long LastFence { get; set; }
        public long AuthorityEpoch { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? TerminalAt { get; set; }
        public ReviewTerminalOutcome? Outcome { get; set; }
        public string? FailureClassification { get; set; }
        public string? TestedResultSha { get; set; }
        public string? TerminalReason { get; set; }
        public string? CurrentClaimDeliveryKey { get; set; }
        public HashSet<string> IdempotencyKeys { get; set; } = [];
        public List<ReviewReportDeliveryRecord> Reports { get; set; } = [];
    }

    private sealed class ReviewReportDeliveryRecord
    {
        public string IdempotencyKey { get; set; } = string.Empty;
        public long Fence { get; set; }
        public long AuthorityEpoch { get; set; }
        public string MaterializedResultSha { get; set; } = string.Empty;
        public ReviewTerminalOutcome Outcome { get; set; }
        public string? FailureClassification { get; set; }
        public string? Reason { get; set; }
        public AttemptWriteStatus AuthorityStatus { get; set; }
        public DateTime ReceivedAt { get; set; }
    }

    private sealed class ReviewSubjectRecord
    {
        public string SubjectId { get; set; } = string.Empty;
        public string RepositoryId { get; set; } = string.Empty;
        public string ExpectedResultSha { get; set; } = string.Empty;
        public string SourceRunAttemptId { get; set; } = string.Empty;
        public string TaskRequirementsHash { get; set; } = string.Empty;
        public string ReviewPolicyHash { get; set; } = string.Empty;
        public List<string> EvidenceDigestInputs { get; set; } = [];
        public string? RepositoryUrl { get; set; }
        public string? ResultRef { get; set; }
        public AgentStudio.TaskServer.Contracts.ReviewPlanDto? Plan { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
