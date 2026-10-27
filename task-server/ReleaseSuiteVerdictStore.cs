using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static Task ApplyReleaseSuiteVerdictMigrationAsync(SqliteConnection connection, CancellationToken ct)
        => ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS release_suite_verdicts(
                project TEXT NOT NULL,
                commit_sha TEXT NOT NULL,
                command_digest TEXT NOT NULL,
                status TEXT NOT NULL,
                finished_at TEXT NOT NULL,
                PRIMARY KEY(project, commit_sha, command_digest)
            );
            """, ct);

    public async Task<ReleaseSuiteVerdict> PutReleaseSuiteVerdictAsync(ReleaseSuiteVerdict verdict,
        CancellationToken ct = default)
    {
        ValidateReleaseSuiteVerdict(verdict);
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO release_suite_verdicts(project, commit_sha, command_digest, status, finished_at)
                VALUES ($project, $sha, $digest, $status, $at)
                ON CONFLICT(project, commit_sha, command_digest) DO UPDATE SET
                    status = excluded.status, finished_at = excluded.finished_at
                WHERE release_suite_verdicts.finished_at <= excluded.finished_at;
                """, ct, transaction, ("$project", verdict.Project), ("$sha", verdict.CommitSha),
                ("$digest", verdict.CommandDigest), ("$status", verdict.Status),
                ("$at", Iso(verdict.FinishedAtUtc.UtcDateTime)));
        }, ct);
        return await GetReleaseSuiteVerdictAsync(verdict.Project, verdict.CommitSha, verdict.CommandDigest, ct)
            ?? throw new InvalidOperationException("The release suite verdict was not stored.");
    }

    public async Task<ReleaseSuiteVerdict?> GetReleaseSuiteVerdictAsync(string project, string sha,
        string digest, CancellationToken ct = default)
    {
        ValidateReleaseSuiteKey(project, sha, digest);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT status, finished_at FROM release_suite_verdicts
             WHERE project = $project AND commit_sha = $sha AND command_digest = $digest;
            """, ("$project", project), ("$sha", sha), ("$digest", digest));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(project, sha, digest, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)))
            : null;
    }

    private static void ValidateReleaseSuiteVerdict(ReleaseSuiteVerdict verdict)
    {
        ValidateReleaseSuiteKey(verdict.Project, verdict.CommitSha, verdict.CommandDigest);
        if (verdict.Status is not ("green" or "red"))
            throw new ArgumentException("Release suite status must be green or red.");
        if (verdict.FinishedAtUtc == default)
            throw new ArgumentException("Release suite completion time is required.");
    }

    private static void ValidateReleaseSuiteKey(string project, string sha, string digest)
    {
        if (string.IsNullOrWhiteSpace(project) || project.Length > 200
            || project.Contains('/') || project.Contains('\\'))
            throw new ArgumentException("A project name is required.");
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 40 || !sha.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(digest) || digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new ArgumentException("A full candidate SHA and command digest are required.");
    }
}
