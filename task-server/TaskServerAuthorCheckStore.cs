using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed class AuthorCheckPilotOptions
{
    public bool Enabled { get; set; }
    public string ProjectId { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string SourceHostId { get; set; } = "agent-runner-01";
    public string ExecutorHostId { get; set; } = "";
    public string ExecutorId { get; set; } = "";
    public string ProtectedWorkPrincipalId { get; set; } = "";
    public string Base { get; set; } = "";
    public string Solution { get; set; } = "";
    public string Image { get; set; } = "";
    public int HalfCores { get; set; } = 2;
    public int MemoryMiB { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 1200;

    internal AuthorCheckProfile? Profile()
    {
        var pin = Image.Split("@sha256:", StringSplitOptions.None);
        return Enabled && new[] { ProjectId, RepositoryId, RunId, SourceHostId, ExecutorHostId, ExecutorId,
                ProtectedWorkPrincipalId }.All(value => !string.IsNullOrWhiteSpace(value))
            && SourceHostId != ExecutorHostId
            && AuthorCheckProtocol.IsHex(Base, 40)
            && Solution.EndsWith(".sln", StringComparison.Ordinal)
            && Solution.Split('/').All(part => part.Length > 0 && part is not ("." or "..")
                && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            && pin.Length == 2 && pin[0].Length > 0 && AuthorCheckProtocol.IsHex(pin[1], 64)
            && HalfCores is >= 1 and <= 8 && MemoryMiB is >= 512 and <= 8192
            && TimeoutSeconds is >= 1 and <= 1200
            ? new(RepositoryId, Base, Solution, Image, TimeoutSeconds, HalfCores, MemoryMiB) : null;
    }
}

public sealed partial class TaskServerStore
{
    private readonly SemaphoreSlim _authorCheckUpload = new(1);
    private readonly IOptionsMonitor<TaskServerOptions>? _authorCheckOptions;
    private AuthorCheckPilotOptions Pilot => _authorCheckOptions?.CurrentValue.AuthorCheck ?? _options.AuthorCheck;
    private string AuthorCheckSourceRoot => Path.Combine(DataDirectory, "author-check-source");
    private string SourcePath(string checkId) => Path.Combine(AuthorCheckSourceRoot, checkId + ".bundle");
    private readonly Queue<DateTime> _authorCheckUploads = new();
    private readonly Queue<DateTime> _authorCheckSubmissions = new();

    private sealed record CheckRow(
        string Id, AuthorCheckSubmission Submission, AuthorCheckProfile Profile, string PlanDigest,
        DateTime Enqueued, string? AttemptId, DateTime? Deadline, string? Grant,
        AuthorCheckReceipt? Receipt, DateTime? SettledAt);

    private static Task ApplyAuthorCheckMigrationAsync(SqliteConnection connection, CancellationToken ct)
        => ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS author_checks(
                id TEXT PRIMARY KEY REFERENCES post_work_subjects(id),
                submission_json TEXT NOT NULL,
                profile_json TEXT NOT NULL,
                plan_digest TEXT NOT NULL,
                enqueued_at TEXT NOT NULL,
                attempt_id TEXT,
                deadline TEXT,
                source_grant TEXT,
                receipt_json TEXT,
                settled_at TEXT
            );
            CREATE TABLE IF NOT EXISTS author_check_requests(
                run_id TEXT NOT NULL,
                request_id TEXT NOT NULL,
                submission_json TEXT NOT NULL,
                check_id TEXT NOT NULL REFERENCES author_checks(id),
                PRIMARY KEY(run_id, request_id)
            );
            CREATE TABLE IF NOT EXISTS author_check_protected_work(
                host_id TEXT PRIMARY KEY,
                active INTEGER NOT NULL,
                queued INTEGER NOT NULL,
                observed_at TEXT NOT NULL
            );
            """, ct);

    public async Task<AuthorCheckProfile> GetAuthorCheckProfileAsync(
        AuthorCheckParent parent, string principalId, CancellationToken ct)
    {
        RequireAdmission();
        AuthorCheckProfile? profile = null;
        await InWriteTransactionAsync(async (connection, tx) =>
        {
            await CheckParentAsync(connection, tx, parent, principalId, ct);
            profile = RequirePilot(parent);
        }, ct);
        return profile!;
    }

    public async Task<AuthorCheckReceipt> SubmitAuthorCheckAsync(
        AuthorCheckSubmission submission, string principalId, Stream body, CancellationToken ct)
    {
        RequireAdmission();
        var profile = await GetAuthorCheckProfileAsync(submission.Parent, principalId, ct);
        var source = submission.Snapshot;
        if (submission.RequestId == Guid.Empty || submission.Name != AuthorCheckProtocol.Name
            || source.Base != profile.Base || !AuthorCheckProtocol.IsHex(source.Commit, 40)
            || !AuthorCheckProtocol.IsHex(source.Tree, 40) || !AuthorCheckProtocol.IsHex(source.Sha256, 64)
            || source.Bytes is < 1 or > AuthorCheckProtocol.BundleLimit)
            throw new ArgumentException("The author-check snapshot or named build is invalid.");
        if (!await _authorCheckUpload.WaitAsync(TimeSpan.FromSeconds(5), ct))
            throw new TaskServerConflictException("author-check-upload-busy", "One author-check upload is already staged.");
        try
        {
            LimitAuthorCheckRate(_authorCheckUploads, 12);
            using var upload = CancellationTokenSource.CreateLinkedTokenSource(ct);
            upload.CancelAfter(TimeSpan.FromMinutes(10));
            var bytes = new byte[source.Bytes];
            await body.ReadExactlyAsync(bytes, upload.Token);
            if (await body.ReadAsync(new byte[1], upload.Token) != 0 || Hash(bytes) != source.Sha256)
                throw new ArgumentException("The author-check bundle digest or size differs from its declaration.");
            LimitAuthorCheckRate(_authorCheckSubmissions, 8);
            AuthorCheckReceipt? receipt = null;
            await InWriteTransactionAsync(async (connection, tx) =>
            {
                var parent = await CheckParentAsync(connection, tx, submission.Parent, principalId, ct);
                if (RequirePilot(submission.Parent) != profile)
                    throw new TaskServerConflictException("author-check-profile-changed", "The approved build profile changed.");
                var now = UtcNow;
                var plan = Hash(Encoding.UTF8.GetBytes(
                    "dotnet-solution-build-v1\n" + JsonSerializer.Serialize(profile, PostWorkJson)));
                var requestJson = JsonSerializer.Serialize(submission, PostWorkJson);
                var requestId = submission.RequestId.ToString("N");
                var replay = await ScalarAsync(connection, """
                    SELECT check_id FROM author_check_requests WHERE run_id = $run AND request_id = $request;
                    """, ct, tx, ("$run", parent.RunId), ("$request", requestId)) as string;
                if (replay is not null)
                {
                    var prior = await ScalarAsync(connection, """
                        SELECT submission_json FROM author_check_requests WHERE run_id = $run AND request_id = $request;
                        """, ct, tx, ("$run", parent.RunId), ("$request", requestId)) as string;
                    if (prior != requestJson)
                        throw new TaskServerConflictException("author-check-replay-conflict", "This request ID already names another submission.");
                    receipt = ReceiptFor((await ReadCheckAsync(connection, tx, replay, ct))!);
                    return;
                }
                var recent = new List<CheckRow>();
                await using (var command = Command(connection, """
                    SELECT a.id FROM author_checks a JOIN post_work_subjects p ON p.id = a.id
                     WHERE p.project_id = $project AND (a.enqueued_at > $hour OR a.receipt_json IS NULL)
                     ORDER BY a.enqueued_at;
                    """, tx, ("$project", Pilot.ProjectId), ("$hour", Iso(now.AddHours(-1)))))
                {
                    var ids = new List<string>();
                    await using (var reader = await command.ExecuteReaderAsync(ct))
                        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
                    foreach (var id in ids)
                        recent.Add((await ReadCheckAsync(connection, tx, id, ct))!);
                }

                var matching = recent.LastOrDefault(item => item.Submission.Parent == submission.Parent
                    && item.Submission.Snapshot.Tree == source.Tree && item.PlanDigest == plan
                    && (item.Receipt is null
                        || (item.Receipt.Status is "passed" or "failed" && item.SettledAt > now.AddMinutes(-5))));
                var checkId = matching?.Id ?? $"ac_{Guid.NewGuid():N}";
                if (matching is null)
                {
                    var cardCount = Convert.ToInt32(await ScalarAsync(connection, """
                        SELECT count(*) FROM author_checks a JOIN post_work_subjects p ON p.id = a.id
                         WHERE p.task_id = $card AND (a.enqueued_at > $hour OR a.receipt_json IS NULL);
                        """, ct, tx, ("$card", parent.TaskId), ("$hour", Iso(now.AddHours(-1)))));
                    if (recent.Count >= 4 || cardCount >= 2 || recent.Any(item => item.Receipt is null))
                        throw new TaskServerConflictException("author-check-quota", "A check is open or the pilot's rolling hourly limit is reached.");
                    await PersistAuthorCheckSourceAsync(checkId, bytes, ct);
                    await ExecuteAsync(connection, """
                        INSERT INTO post_work_subjects(id, idempotency_key, project_id, task_id, lane_entry_id,
                            source_run_id, subject_json, policy_digest, owner, state, cohort_phase,
                            shadow_status, shadow_reasons_json, created_at, updated_at)
                        VALUES ($id, $id, $project, $task, '', $run, $json, $plan, 'host', 'retired',
                            'author-check', 'excluded', '[]', $now, $now);
                        INSERT INTO author_checks(id, submission_json, profile_json, plan_digest, enqueued_at)
                        VALUES ($id, $submission, $profile, $plan, $now);
                        """, ct, tx, ("$id", checkId), ("$project", Pilot.ProjectId), ("$task", parent.TaskId),
                        ("$run", parent.RunId), ("$json", JsonSerializer.Serialize(
                            new PostWorkImmutable([AuthorCheckProtocol.Kind], [], []), PostWorkJson)),
                        ("$plan", plan), ("$now", Iso(now)), ("$submission", requestJson),
                        ("$profile", JsonSerializer.Serialize(profile, PostWorkJson)));
                    await AuditAsync(connection, tx, principalId, "author-check.enqueued", AuthorCheckProtocol.Kind,
                        checkId, JsonSerializer.Serialize(source, PostWorkJson), ct);
                }
                await ExecuteAsync(connection, """
                    INSERT INTO author_check_requests(run_id, request_id, submission_json, check_id)
                    VALUES ($run, $request, $submission, $id);
                    """, ct, tx, ("$run", parent.RunId), ("$request", requestId),
                    ("$submission", requestJson), ("$id", checkId));
                receipt = ReceiptFor(matching ?? (await ReadCheckAsync(connection, tx, checkId, ct))!);
            }, ct);
            return receipt!;
        }
        finally
        {
            _authorCheckUpload.Release();
        }
    }

    public async Task<AuthorCheckReceipt> ReadAuthorCheckAsync(
        string id, AuthorCheckParent parent, string principalId, CancellationToken ct)
    {
        AuthorCheckReceipt? receipt = null;
        await InWriteTransactionAsync(async (connection, tx) =>
        {
            await CheckParentAsync(connection, tx, parent, principalId, ct);
            if (Guid.TryParseExact(id, "N", out _))
                id = await ScalarAsync(connection, """
                    SELECT check_id FROM author_check_requests WHERE run_id = $run AND request_id = $id;
                    """, ct, tx, ("$run", parent.RunId), ("$id", id)) as string ?? "";
            var check = await ReadCheckAsync(connection, tx, id, ct);
            if (check is null || check.Submission.Parent != parent)
                throw new KeyNotFoundException("Author check was not found.");
            receipt = ReceiptFor(await ReapCheckAsync(connection, tx, check, ct));
        }, ct);
        return receipt!;
    }

    public async Task<bool> ObserveAuthorCheckProtectedWorkAsync(
        AuthorCheckProtectedWork facts, string principalId, CancellationToken ct)
    {
        if (Pilot.Profile() is null || principalId != Pilot.ProtectedWorkPrincipalId || facts.HostId != Pilot.ExecutorHostId)
            throw new TaskServerConflictException("author-check-protected-work-host", "Protected work facts are outside the approved pilot.");
        if (facts.Active is < 0 or > 256 || facts.Queued < 0)
            throw new ArgumentException("Protected work counters are invalid.");
        await InWriteTransactionAsync(async (connection, tx) =>
            await ExecuteAsync(connection, """
                INSERT INTO author_check_protected_work(host_id, active, queued, observed_at)
                VALUES ($host, $active, $queued, $now)
                ON CONFLICT(host_id) DO UPDATE SET active = excluded.active, queued = excluded.queued,
                    observed_at = excluded.observed_at;
                """, ct, tx, ("$host", facts.HostId), ("$active", facts.Active),
                ("$queued", facts.Queued), ("$now", Iso(UtcNow))), ct);
        return true;
    }

    private async Task<PostWorkClaimResponse?> ClaimAuthorCheckAsync(
        SqliteConnection connection, SqliteTransaction tx, PostWorkClaimRequest request, string hostId,
        HostPlacementAdvertisement? host, RuntimeCapacitySettingsDto? capacity, CancellationToken ct)
    {
        var profile = Pilot.Profile();
        var now = UtcNow;
        if (profile is null || request.ExecutorId != Pilot.ExecutorId || hostId != Pilot.ExecutorHostId
            || request.AuthorCheckImage != profile.Image || request.AuthorCheckDiskFreeBytes is null or < 8L * 1024 * 1024 * 1024
            || host is null || capacity is null)
            return null;
        if (!await AuthorCheckProjectGrantedAsync(connection, tx, hostId, ct)) return null;
        var sshActive = await ScalarAsync(connection, """
            SELECT active FROM author_check_protected_work
             WHERE host_id = $host AND observed_at > $fresh AND queued = 0;
            """, ct, tx, ("$host", hostId), ("$fresh", Iso(now.AddSeconds(-25))));
        if (sshActive is null) return null;
        var protectedQueued = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT (SELECT count(*) FROM review_attempts WHERE status = 'queued')
                 + (SELECT count(*) FROM gate_attempts WHERE state = 'queued');
            """, ct, tx));
        if (protectedQueued != 0) return null;
        var occupied = await CountOccupiedHostGrantsAsync(connection, tx, hostId, ct);
        occupied += Convert.ToInt32(await ScalarAsync(connection, """
            SELECT (SELECT count(*) FROM review_attempts WHERE host_id = $host
                     AND status IN ('leased', 'process-unknown', 'reported'))
                 + (SELECT count(*) FROM gate_attempts WHERE host_id = $host AND grant_half_cores = 0
                     AND state IN ('claimed','materializing','running','reporting','cleaning'));
            """, ct, tx, ("$host", hostId)));
        occupied += Convert.ToInt32(sshActive);
        var placement = HostPlacementPolicy.Decide(host, "post", Pilot.ProjectId,
            ["os:linux", "executor:container", "toolchain:" + profile.Image], now, now,
            TimeSpan.Zero, false, capacity.AllocationVersion == request.RuntimeCapacityAppliedVersion,
            occupied + 1 < capacity.MaxParallelism);
        if (!placement.Eligible) return null;
        var id = await ScalarAsync(connection, """
            SELECT id FROM author_checks WHERE receipt_json IS NULL AND attempt_id IS NULL
               AND NOT EXISTS (SELECT 1 FROM post_work_attempts p JOIN author_checks a ON a.id = p.subject_id
                                WHERE p.state = 'live')
             ORDER BY enqueued_at LIMIT 1;
            """, ct, tx) as string;
        if (id is null) return null;
        var check = (await ReadCheckAsync(connection, tx, id, ct))!;
        if ((await ReapCheckAsync(connection, tx, check, ct)).Receipt is not null) return null;
        if (!File.Exists(SourcePath(id)))
        {
            await CloseCheckAsync(connection, tx, check,
                ReceiptFor(check) with { Status = "unavailable", Reason = "The staged source is unavailable." }, ct);
            return null;
        }
        var subject = (await ReadPostWorkSubjectAsync(connection, tx, "id", id, ct))!;
        var lease = await AcquirePostWorkAttemptAsync(connection, tx, subject, PostWorkExecutorKinds.Host,
            request.ExecutorId, request.InstanceId, hostId, AuthorCheckProtocol.LeaseSeconds,
            capacity.AllocationVersion, profile.HalfCores, now, ct);
        var grant = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var deadline = now.AddSeconds(AuthorCheckProtocol.WorkSeconds);
        await ExecuteAsync(connection, """
            UPDATE author_checks SET attempt_id = $attempt, deadline = $deadline, source_grant = $grant
             WHERE id = $id;
            """, ct, tx, ("$attempt", lease.AttemptId), ("$deadline", Iso(deadline)), ("$grant", grant), ("$id", id));
        await AuditAsync(connection, tx, request.ExecutorId, "author-check.claimed", AuthorCheckProtocol.Kind,
            id, JsonSerializer.Serialize(new { lease.AttemptId, lease.Fence, hostId }), ct);
        return new("claimed", subject, lease,
            AuthorCheck: new(check.Submission.Snapshot, profile, check.PlanDigest, deadline), SourceGrant: grant);
    }

    public async Task<byte[]> ReadAuthorCheckSourceAsync(
        string attemptId, AuthorCheckSourceGrant request, CancellationToken ct)
    {
        byte[]? bundle = null;
        await InWriteTransactionAsync(async (connection, tx) =>
        {
            var (check, attempt) = await FencedCheckAsync(connection, tx, attemptId, request.Authority, ct);
            if (attempt.AcquiredAt.AddSeconds(AuthorCheckProtocol.LeaseSeconds) <= UtcNow
                || check.Grant is null || !AuthorCheckProtocol.IsHex(request.Grant, 64) || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(check.Grant), Encoding.ASCII.GetBytes(request.Grant)))
                throw new TaskServerConflictException("author-check-source-grant", "The source grant is invalid or expired.");
            var file = SourcePath(check.Id);
            if (File.Exists(file))
                bundle = await File.ReadAllBytesAsync(file, ct);
        }, ct);
        return bundle ?? throw new KeyNotFoundException("Author-check source is unavailable.");
    }

    public async Task<AuthorCheckReceipt> SettleAuthorCheckAsync(
        string attemptId, AuthorCheckSettlement report, CancellationToken ct)
    {
        RequireWritable();
        if (report.Log is null || report.Log.Length > AuthorCheckProtocol.LogLimit || report.OmittedLogBytes < 0
            || report.ElapsedMs is < 0 or > AuthorCheckProtocol.WorkSeconds * 1000L
            || report.Status is not ("passed" or "failed" or "timed-out" or "unavailable")
            || (report.Status == "passed" && report.ExitCode != 0)
            || (report.Status == "failed" && report.ExitCode is null or 0))
            throw new ArgumentException("Author-check result is invalid.");
        AuthorCheckReceipt? result = null;
        await InWriteTransactionAsync(async (connection, tx) =>
        {
            var (check, attempt) = await FencedCheckAsync(connection, tx, attemptId, report.Authority, ct);
            var parent = check.Submission.Parent;
            var status = report.Status;
            var name = $"results/agent-check/{check.Id}/full.log";
            var artifactId = check.Id + "_log";
            var digest = Hash(report.Log);
            var now = UtcNow;
            // This card artifact and the receipt commit in the same fenced transaction.
            await ExecuteAsync(connection, """
                INSERT INTO artifacts(id, run_id, name, media_type, sha256, content, size_bytes, idempotency_key,
                    fence, created_at)
                VALUES ($artifact, $run, $name, 'text/plain', $digest, $log, $bytes, $artifact, $fence, $now);
                """, ct, tx, ("$artifact", artifactId), ("$run", parent.RunId), ("$name", name),
                ("$digest", digest), ("$log", report.Log), ("$bytes", report.Log.Length),
                ("$fence", parent.Fence), ("$now", Iso(now)));
            var lines = Encoding.UTF8.GetString(report.Log).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var diagnostics = lines.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
                .Concat(lines.TakeLast(4)).Distinct().Take(12).Select(line => Bound(line.Trim(), 300)).ToArray();
            result = ReceiptFor(check) with
            {
                Status = status, HostId = attempt.HostId, ExitCode = report.ExitCode, ElapsedMs = report.ElapsedMs,
                QueueMs = (long)(attempt.AcquiredAt - check.Enqueued).TotalMilliseconds, LogPath = name,
                LogBytes = report.Log.Length, OmittedLogBytes = report.OmittedLogBytes, Diagnostics = diagnostics,
                LogRedacted = report.LogRedacted,
            };
            await CloseCheckAsync(connection, tx, check, result, ct);
            await ExpirePostWorkAttemptsAsync(connection, tx, check.Id, "reported", ct);
            await AuditAsync(connection, tx, report.Authority.ExecutorId, "author-check.settled",
                AuthorCheckProtocol.Kind, check.Id, JsonSerializer.Serialize(result, PostWorkJson), ct);
        }, ct);
        return result!;
    }

    public async Task<bool> ReleaseContainedAuthorCheckAsync(
        string attemptId, PostWorkAuthority authority, CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync(async (connection, tx) =>
        {
            var (attempt, subject) = await ReadFencedPostWorkAttemptAsync(connection, tx, attemptId, ct);
            var check = await ReadCheckAsync(connection, tx, subject.SubjectId, ct);
            if (check is null || check.AttemptId != attemptId || attempt.LeaseId != authority.LeaseId
                || attempt.ExecutorId != authority.ExecutorId || attempt.InstanceId != authority.InstanceId
                || attempt.Fence != authority.Fence || attempt.AuthorityEpoch != authority.AuthorityEpoch)
                throw new TaskServerConflictException("author-check-claim", "The cleanup belongs to another author-check claim.");
            if (attempt.State != "live") return;
            check = await ReapCheckAsync(connection, tx, check, ct);
            if (check.Receipt is null)
                await CloseCheckAsync(connection, tx, check, ReceiptFor(check) with
                {
                    Status = "unavailable", Reason = "The host contained the interrupted check.",
                }, ct);
            await ExpirePostWorkAttemptsAsync(connection, tx, check.Id, "lost", ct);
        }, ct);
        return true;
    }

    private async Task<(CheckRow Check, PostWorkLiveAttempt Attempt)> FencedCheckAsync(
        SqliteConnection connection, SqliteTransaction tx, string attemptId,
        PostWorkAuthority authority, CancellationToken ct)
    {
        var (attempt, subject) = await ReadFencedPostWorkAttemptAsync(connection, tx, attemptId, ct);
        var check = await ReadCheckAsync(connection, tx, subject.SubjectId, ct);
        if (check is null || check.AttemptId != attemptId || check.Receipt is not null
            || authority.ExecutorId != Pilot.ExecutorId || attempt.HostId != Pilot.ExecutorHostId)
            throw new TaskServerConflictException("author-check-claim", "The author-check claim is no longer current.");
        var refusal = PostWorkAuthorityPolicy.RefuseFencedCall(authority, attempt.State,
            attempt.ExecutorId, attempt.InstanceId, attempt.LeaseId, attempt.Fence, subject.LastFence,
            attempt.AuthorityEpoch, attempt.ExpiresAt, UtcNow);
        if (refusal is not null) throw new TaskServerConflictException(refusal, "The author-check fence is no longer current.");
        await AuthorCheckRenewalExpiryAsync(connection, tx, check.Id, ct);
        return (check, attempt);
    }

    private async Task<DateTime> AuthorCheckRenewalExpiryAsync(
        SqliteConnection connection, SqliteTransaction tx, string id, CancellationToken ct)
    {
        var check = (await ReadCheckAsync(connection, tx, id, ct))!;
        if (check.Receipt is not null || check.Deadline is not { } deadline || deadline <= UtcNow
            || RequirePilot(check.Submission.Parent) != check.Profile)
            throw new TaskServerConflictException("author-check-inactive", "The author check was canceled or expired.");
        await CheckParentAsync(connection, tx, check.Submission.Parent, null, ct);
        var currentExecutor = await ScalarAsync(connection, """
            SELECT 1 FROM post_work_attempts a JOIN runners r ON r.id = a.executor_id
             WHERE a.id = $attempt AND r.id = $executor AND r.host_id = $host
               AND r.instance_id = a.instance_id AND r.status = 'active'
               AND EXISTS (SELECT 1 FROM json_each(r.capabilities_json) WHERE value = 'author-check');
            """, ct, tx, ("$attempt", check.AttemptId), ("$executor", Pilot.ExecutorId), ("$host", Pilot.ExecutorHostId));
        if (currentExecutor is null || !await AuthorCheckProjectGrantedAsync(connection, tx, Pilot.ExecutorHostId, ct))
            throw new TaskServerConflictException("author-check-executor-stale", "The approved executor or project grant is no longer current.");
        var renewal = UtcNow.AddSeconds(AuthorCheckProtocol.LeaseSeconds);
        return renewal < deadline ? renewal : deadline;
    }

    private async Task<bool> AuthorCheckProjectGrantedAsync(
        SqliteConnection connection, SqliteTransaction tx, string hostId, CancellationToken ct)
    {
        var policy = await ReadHostProjectPolicyAsync(connection, tx, hostId, ct);
        return policy is not null && (policy.AllowAllProjects || policy.AllowedProjectIds.Contains(Pilot.ProjectId, StringComparer.Ordinal));
    }

    internal async Task SweepAuthorChecksAsync(CancellationToken ct)
    {
        if (!AuthorityReady || Mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance) return;
        await InWriteTransactionAsync(async (connection, tx) =>
        {
            var ids = new List<string>();
            await using (var command = Command(connection, """
                SELECT a.id FROM author_checks a
                 WHERE a.receipt_json IS NULL OR EXISTS (
                   SELECT 1 FROM post_work_attempts p WHERE p.id = a.attempt_id AND p.state = 'live');
                """, tx))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
            foreach (var id in ids)
                await ReapCheckAsync(connection, tx, (await ReadCheckAsync(connection, tx, id, ct))!, ct);
        }, ct);

        if (!Directory.Exists(AuthorCheckSourceRoot)) return;
        foreach (var file in Directory.EnumerateFiles(AuthorCheckSourceRoot, "*.bundle"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            await InWriteTransactionAsync(async (connection, tx) =>
            {
                var check = await ReadCheckAsync(connection, tx, id, ct);
                if (check is null || check.Receipt is not null)
                    File.Delete(file);
            }, ct);
        }
    }

    private async Task PersistAuthorCheckSourceAsync(string id, byte[] bytes, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(AuthorCheckSourceRoot);
        else
            Directory.CreateDirectory(AuthorCheckSourceRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var file = new FileStream(SourcePath(id), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await file.WriteAsync(bytes, ct);
    }

    // These limits govern input traffic. Durable execution quota is enforced
    // separately inside the registration transaction after request deduplication.
    private void LimitAuthorCheckRate(Queue<DateTime> traffic, int limit)
    {
        var now = UtcNow;
        while (traffic.TryPeek(out var at) && at <= now.AddMinutes(-1))
            traffic.Dequeue();
        if (traffic.Count >= limit)
            throw new TaskServerConflictException("author-check-rate-limit", "The author-check input rate limit is reached.");
        traffic.Enqueue(now);
    }

    private async Task<CheckRow> ReapCheckAsync(
        SqliteConnection connection, SqliteTransaction tx, CheckRow check, CancellationToken ct)
    {
        var now = UtcNow;
        if (check.Deadline <= now)
            await ExpirePostWorkAttemptsAsync(connection, tx, check.Id, "lost", ct);
        if (check.Receipt is not null) return check;
        var status = "canceled";
        string? reason;
        try
        {
            if (RequirePilot(check.Submission.Parent) != check.Profile)
                throw new TaskServerConflictException("author-check-profile-changed", "The approved build profile changed.");
            await CheckParentAsync(connection, tx, check.Submission.Parent, null, ct);
            status = "unavailable";
            var attempt = await ReadLivePostWorkAttemptAsync(connection, tx, check.Id, ct);
            reason = check.AttemptId is null
                ? now >= check.Enqueued.AddSeconds(AuthorCheckProtocol.QueueSeconds) ? "No protected-capacity grant within 90 seconds." : null
                : attempt is null || attempt.ExpiresAt <= now ? "The check lease expired. Execution will not be retried." : null;
        }
        catch (TaskServerConflictException conflict) { reason = conflict.Message; }
        catch (KeyNotFoundException) { reason = "The parent attempt no longer exists."; }
        if (reason is null) return check;
        var receipt = ReceiptFor(check) with { Status = status, Reason = reason };
        await CloseCheckAsync(connection, tx, check, receipt, ct);
        return check with { Receipt = receipt, SettledAt = now };
    }

    private Task CloseCheckAsync(
        SqliteConnection connection, SqliteTransaction tx, CheckRow check, AuthorCheckReceipt receipt, CancellationToken ct)
        => ExecuteAsync(connection, """
            UPDATE author_checks SET receipt_json = $receipt, settled_at = $now, source_grant = NULL
             WHERE id = $id;
            """, ct, tx, ("$receipt", JsonSerializer.Serialize(receipt, PostWorkJson)),
            ("$now", Iso(UtcNow)), ("$id", check.Id));

    private AuthorCheckProfile RequirePilot(AuthorCheckParent parent)
        => parent.RunId == Pilot.RunId && Pilot.Profile() is { } profile
            ? profile : throw new TaskServerConflictException("author-check-disabled", "Remote author checks are disabled for this attempt.");

    private async Task<LeaseDto> CheckParentAsync(
        SqliteConnection connection, SqliteTransaction tx, AuthorCheckParent parent, string? principalId, CancellationToken ct)
    {
        var lease = await ReadLeaseAsync(connection, tx, parent.RunId, ct)
            ?? throw new KeyNotFoundException("Parent coding attempt was not found.");
        ValidateLeaseReference(lease, parent.RunnerId, parent.InstanceId, parent.LeaseId, parent.Fence);
        await EnsureLeaseCurrentAsync(connection, tx, lease, ct);
        var valid = await ScalarAsync(connection, """
            SELECT 1 FROM runs r JOIN tasks t ON t.id = r.task_id JOIN runners h ON h.id = r.runner_id
             WHERE r.id = $run AND r.status = 'running' AND t.project_id = $project
               AND EXISTS (SELECT 1 FROM project_repositories p
                            WHERE p.project_id = t.project_id AND p.repository_id = $repository)
               AND h.host_id = $host AND h.instance_id = $instance
               AND h.status = 'active'
               AND r.claim_principal_id IS NOT NULL
               AND ($principal IS NULL OR r.claim_principal_id = $principal);
            """, ct, tx, ("$run", parent.RunId), ("$project", Pilot.ProjectId), ("$repository", Pilot.RepositoryId),
            ("$host", Pilot.SourceHostId), ("$instance", parent.InstanceId), ("$principal", principalId));
        if (valid is null)
            throw new TaskServerConflictException("author-check-parent-authority", "The parent coding attempt does not match the pilot scope.");
        return lease;
    }

    private static async Task<CheckRow?> ReadCheckAsync(
        SqliteConnection connection, SqliteTransaction tx, string id, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT submission_json, profile_json, plan_digest, enqueued_at, attempt_id, deadline,
                source_grant, receipt_json, settled_at FROM author_checks WHERE id = $id;
            """, tx, ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        string? Read(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        return new(id, JsonSerializer.Deserialize<AuthorCheckSubmission>(reader.GetString(0), PostWorkJson)!,
            JsonSerializer.Deserialize<AuthorCheckProfile>(reader.GetString(1), PostWorkJson)!, reader.GetString(2),
            Parse(reader.GetString(3)), Read(4), Read(5) is { } deadline ? Parse(deadline) : null,
            Read(6), Read(7) is { } receipt ? JsonSerializer.Deserialize<AuthorCheckReceipt>(receipt, PostWorkJson) : null,
            Read(8) is { } settled ? Parse(settled) : null);
    }

    private static AuthorCheckReceipt ReceiptFor(CheckRow check)
        => check.Receipt ?? new(check.Id, check.AttemptId is null ? "queued" : "running",
            check.Submission.Snapshot.Tree, check.PlanDigest, check.Submission.Snapshot.Bytes);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
