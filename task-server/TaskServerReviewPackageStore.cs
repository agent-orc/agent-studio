using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Review subject kind <c>package</c> (AGT-3400 S4): the members of one
/// Dossier wave reviewed together in shadow. Package subjects and their
/// members live in their own tables; <c>review_subjects</c> keeps exactly one
/// task per subject and is never touched by a package. A package attempt
/// follows <see cref="ReviewPackageLeasePolicy"/>, and its report never
/// changes a task state, review attempt or settlement.
/// </summary>
public sealed partial class TaskServerStore
{
    internal static async Task ApplyReviewPackageMigrationAsync(SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS review_package_subjects(
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                project TEXT NOT NULL,
                dossier_path TEXT NOT NULL,
                membership_digest TEXT NOT NULL UNIQUE,
                mode TEXT NOT NULL,
                plan_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                status TEXT NOT NULL,
                state_json TEXT NOT NULL,
                report_json TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS review_package_members(
                package_id TEXT NOT NULL REFERENCES review_package_subjects(id),
                position INTEGER NOT NULL,
                task_key TEXT NOT NULL,
                run_attempt_id TEXT NOT NULL,
                result_sha TEXT NOT NULL,
                member_json TEXT NOT NULL,
                PRIMARY KEY(package_id, position),
                UNIQUE(task_key, run_attempt_id)
            );
            CREATE INDEX IF NOT EXISTS ix_review_package_subjects_status
                ON review_package_subjects(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_review_package_subjects_project
                ON review_package_subjects(project, created_at);
            """, ct);

    public async Task<ReviewPackageSubjectDto> CreateReviewPackageSubjectAsync(
        CreateReviewPackageSubjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ReviewPackageSubjectValidation.Validate(request);
        var digest = ReviewPackageMembershipDigest.Compute(request.DossierPath, request.Members);
        ReviewPackageSubjectDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var replayId = Convert.ToString(await ScalarAsync(connection,
                "SELECT id FROM review_package_subjects WHERE idempotency_key = $key OR membership_digest = $digest;",
                ct, transaction, ("$key", request.IdempotencyKey), ("$digest", digest)));
            if (!string.IsNullOrEmpty(replayId))
            {
                var replay = await RequireReviewPackageAsync(connection, transaction, replayId, ct);
                if (replay.MembershipDigest != digest)
                    throw new TaskServerConflictException("idempotency-conflict",
                        "The review package key is bound to a different membership.");
                result = replay;
                return;
            }
            foreach (var member in request.Members)
            {
                var other = Convert.ToString(await ScalarAsync(connection,
                    "SELECT package_id FROM review_package_members WHERE task_key = $task AND run_attempt_id = $run;",
                    ct, transaction, ("$task", member.TaskKey), ("$run", member.RunAttemptId)));
                if (!string.IsNullOrEmpty(other))
                    throw new TaskServerConflictException("member-already-packaged",
                        $"{member.TaskKey} run {member.RunAttemptId} is already a member of {other}.");
            }

            var subject = new ReviewPackageSubjectDto("rpkg_" + digest[..24], ReviewSubjectKinds.Package,
                request.Project, request.DossierPath, digest, ReviewPackageModes.Shadow,
                request.Members.OrderBy(member => member.Order).ToArray(), request.Plan, UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO review_package_subjects(
                    id, kind, project, dossier_path, membership_digest, mode, plan_json,
                    idempotency_key, status, state_json, created_at)
                VALUES ($id, $kind, $project, $dossier, $digest, $mode, $plan, $key, $status, $state, $now);
                """, ct, transaction,
                ("$id", subject.PackageId), ("$kind", subject.Kind), ("$project", subject.Project),
                ("$dossier", subject.DossierPath), ("$digest", digest), ("$mode", subject.Mode),
                ("$plan", JsonSerializer.Serialize(subject.Plan, ReviewJson)), ("$key", request.IdempotencyKey),
                ("$status", ReviewPackageAttemptStatuses.Queued),
                ("$state", JsonSerializer.Serialize(ReviewPackageAttemptState.Queued, ReviewJson)),
                ("$now", Iso(subject.CreatedAt)));
            foreach (var member in subject.Members)
                await ExecuteAsync(connection, """
                    INSERT INTO review_package_members(
                        package_id, position, task_key, run_attempt_id, result_sha, member_json)
                    VALUES ($package, $position, $task, $run, $sha, $json);
                    """, ct, transaction,
                    ("$package", subject.PackageId), ("$position", member.Order), ("$task", member.TaskKey),
                    ("$run", member.RunAttemptId), ("$sha", member.ResultSha.ToLowerInvariant()),
                    ("$json", JsonSerializer.Serialize(member, ReviewJson)));
            await AuditAsync(connection, transaction, actorId, "review.package-created", "review-package",
                subject.PackageId, JsonSerializer.Serialize(new
                {
                    subject.Project,
                    subject.DossierPath,
                    subject.MembershipDigest,
                    Members = subject.Members.Select(member => member.TaskKey).ToArray(),
                }), ct);
            result = subject;
        }, ct);
        return result!;
    }

    public async Task<ReviewPackageMembershipDto?> GetReviewPackageMembershipAsync(
        string taskKey, string runAttemptId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var packageId = Convert.ToString(await ScalarAsync(connection,
            "SELECT package_id FROM review_package_members WHERE task_key = $task AND run_attempt_id = $run;",
            ct, ("$task", taskKey), ("$run", runAttemptId)));
        return string.IsNullOrEmpty(packageId) ? null : new ReviewPackageMembershipDto(packageId, taskKey, runAttemptId);
    }

    public async Task<IReadOnlyList<ReviewPackageRecordDto>> ListReviewPackagesAsync(string project, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadReviewPackagesAsync(connection, null, "p.project = $project COLLATE NOCASE", ct,
            ("$project", project));
    }

    public async Task<ReviewPackageClaimResponse> ClaimReviewPackageAsync(
        ReviewPackageClaimRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ReviewPackageClaimResponse? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = UtcNow;
            var open = await ReadReviewPackagesAsync(connection, transaction,
                "p.status IN ('queued', 'leased')", ct);
            foreach (var record in open)
            {
                var state = await ReadPackageStateAsync(connection, transaction, record.Subject.PackageId, ct);
                var current = ReviewPackageLeasePolicy.Expire(state, now);
                if (current != state)
                    await WritePackageStateAsync(connection, transaction, record.Subject.PackageId, current, null, ct);
                if (current.Status != ReviewPackageAttemptStatuses.Queued) continue;
                var (leased, lease) = ReviewPackageLeasePolicy.Claim(record.Subject.PackageId, current, request, now);
                await WritePackageStateAsync(connection, transaction, record.Subject.PackageId, leased, null, ct);
                await AuditAsync(connection, transaction, actorId, "review.package-claimed", "review-package",
                    record.Subject.PackageId, JsonSerializer.Serialize(new
                    {
                        lease.AttemptId,
                        request.ExecutorId,
                        request.InstanceId,
                        lease.Fence,
                    }), ct);
                result = new ReviewPackageClaimResponse("claimed", record.Subject, lease);
                return;
            }
            result = new ReviewPackageClaimResponse("empty", null, null, "No review package is queued.");
        }, ct);
        return result!;
    }

    public async Task<ReviewPackageLeaseDto> RenewReviewPackageAsync(
        string attemptId, ReviewPackageRenewRequest request, CancellationToken ct)
    {
        RequireWritable();
        ReviewPackageLeaseDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var packageId = PackageOfAttempt(attemptId);
            var state = await ReadPackageStateAsync(connection, transaction, packageId, ct);
            var (renewed, lease) = ReviewPackageLeasePolicy.Renew(packageId, attemptId, state, request, UtcNow);
            await WritePackageStateAsync(connection, transaction, packageId, renewed, null, ct);
            result = lease;
        }, ct);
        return result!;
    }

    public async Task<ReviewPackageReportDto> ReportReviewPackageAsync(
        string attemptId, ReviewPackageReportRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ReviewPackageReportDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var packageId = PackageOfAttempt(attemptId);
            var subject = await RequireReviewPackageAsync(connection, transaction, packageId, ct);
            var state = await ReadPackageStateAsync(connection, transaction, packageId, ct);
            var hash = ReviewPackageLeasePolicy.PayloadSha256(request);
            var now = UtcNow;
            if (ReviewPackageLeasePolicy.IsDuplicateReport(attemptId, state, request, hash, now))
            {
                result = new ReviewPackageReportDto(state.ReportId!, attemptId, packageId, state.Outcome!, hash,
                    state.ReportedAt!.Value, true);
                return;
            }
            var normalized = ReviewPackageReportPolicy.Normalize(subject, request);
            var reportId = "rprp_" + Guid.NewGuid().ToString("N");
            await WritePackageStateAsync(connection, transaction, packageId,
                ReviewPackageLeasePolicy.Reported(state, request, hash, reportId, now), normalized, ct);
            await AuditAsync(connection, transaction, actorId, "review.package-reported", "review-package",
                packageId, JsonSerializer.Serialize(new { attemptId, request.Outcome, reportId, hash }), ct);
            result = new ReviewPackageReportDto(reportId, attemptId, packageId, request.Outcome, hash, now, false);
        }, ct);
        return result!;
    }

    private static string PackageOfAttempt(string attemptId)
    {
        var separator = attemptId.LastIndexOf("-a", StringComparison.Ordinal);
        if (separator <= 0) throw new KeyNotFoundException($"Review package attempt '{attemptId}' was not found.");
        return attemptId[..separator];
    }

    private async Task<ReviewPackageSubjectDto> RequireReviewPackageAsync(
        SqliteConnection connection, SqliteTransaction transaction, string packageId, CancellationToken ct)
        => (await ReadReviewPackagesAsync(connection, transaction, "p.id = $id", ct, ("$id", packageId)))
            .SingleOrDefault()?.Subject
            ?? throw new KeyNotFoundException($"Review package '{packageId}' was not found.");

    private async Task<IReadOnlyList<ReviewPackageRecordDto>> ReadReviewPackagesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string filter, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var rows = new List<(string Id, string Kind, string Project, string Dossier, string Digest, string Mode,
            string Plan, string State, string? Report, DateTime CreatedAt)>();
        await using (var command = Command(connection, $"""
            SELECT p.id, p.kind, p.project, p.dossier_path, p.membership_digest, p.mode, p.plan_json,
                   p.state_json, p.report_json, p.created_at
              FROM review_package_subjects p
             WHERE {filter}
             ORDER BY p.created_at, p.id;
            """, transaction, parameters))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), Parse(reader.GetString(9))));
        }

        var records = new List<ReviewPackageRecordDto>();
        foreach (var row in rows)
        {
            var members = new List<ReviewPackageMemberDto>();
            await using (var command = Command(connection,
                "SELECT member_json FROM review_package_members WHERE package_id = $id ORDER BY position;",
                transaction, ("$id", row.Id)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    members.Add(JsonSerializer.Deserialize<ReviewPackageMemberDto>(reader.GetString(0), ReviewJson)!);
            }
            var state = JsonSerializer.Deserialize<ReviewPackageAttemptState>(row.State, ReviewJson)!;
            records.Add(new ReviewPackageRecordDto(
                new ReviewPackageSubjectDto(row.Id, row.Kind, row.Project, row.Dossier, row.Digest, row.Mode, members,
                    JsonSerializer.Deserialize<ReviewPackagePlanDto>(row.Plan, ReviewJson)!, row.CreatedAt),
                state.Status, state.Attempts,
                row.Report is null ? null : JsonSerializer.Deserialize<ReviewPackageReportRequest>(row.Report, ReviewJson),
                state.ReportedAt));
        }
        return records;
    }

    private static async Task<ReviewPackageAttemptState> ReadPackageStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, string packageId, CancellationToken ct)
    {
        var json = Convert.ToString(await ScalarAsync(connection,
            "SELECT state_json FROM review_package_subjects WHERE id = $id;", ct, transaction, ("$id", packageId)));
        if (string.IsNullOrEmpty(json)) throw new KeyNotFoundException($"Review package '{packageId}' was not found.");
        return JsonSerializer.Deserialize<ReviewPackageAttemptState>(json, ReviewJson)!;
    }

    private static async Task WritePackageStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, string packageId,
        ReviewPackageAttemptState state, ReviewPackageReportRequest? report, CancellationToken ct)
        => await ExecuteAsync(connection, """
            UPDATE review_package_subjects
               SET status = $status, state_json = $state, report_json = COALESCE($report, report_json)
             WHERE id = $id;
            """, ct, transaction,
            ("$status", state.Status), ("$state", JsonSerializer.Serialize(state, ReviewJson)),
            ("$report", report is null ? null : JsonSerializer.Serialize(report, ReviewJson)), ("$id", packageId));
}
