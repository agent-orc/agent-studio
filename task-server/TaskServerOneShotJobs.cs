using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static async Task ApplyOneShotJobMigrationAsync(SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS one_shot_jobs(
                id TEXT PRIMARY KEY, request_json TEXT NOT NULL, status TEXT NOT NULL,
                capability TEXT NOT NULL, created_at TEXT NOT NULL, expires_at TEXT NOT NULL,
                runner_id TEXT, claim_token TEXT, claim_expires_at TEXT,
                text TEXT, error TEXT, usage_json TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_one_shot_jobs_pending
                ON one_shot_jobs(status, created_at);
            CREATE TABLE IF NOT EXISTS one_shot_model_demands(
                cli TEXT NOT NULL, model TEXT NOT NULL, observed_at TEXT NOT NULL,
                PRIMARY KEY(cli, model)
            );
            """, ct);

    public async Task<OneShotJobDto> CreateOneShotJobAsync(OneShotJobRequest request, CancellationToken ct)
    {
        RequireWritable();
        var promptBytes = Encoding.UTF8.GetByteCount(request.Prompt ?? string.Empty);
        var images = request.Images ?? [];
        if (request.Cli is not ("claude" or "codex") || string.IsNullOrWhiteSpace(request.Model)
            || request.Model.Length > 100
            || promptBytes is < 1 or > 220_000 || request.TimeoutSeconds is < 1 or > 300
            || request.MaxOutputBytes is < 1 or > 65_536 || images.Count > 4
            || images.Sum(image => (long)(image.Base64?.Length ?? 0)) > 5_600_000
            || images.Any(image => image.MediaType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")
                || image.Base64 is null || image.Base64.Length > 5_600_000))
            throw new ArgumentException("One-shot job exceeds its bounded request envelope.");
        var capability = CapabilityProtocol.ModelAcceptance(request.Cli, request.Model);
        OneShotJobDto result = new(string.Empty, "waiting", capability,
            Error: $"waiting for a host with {capability}");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection,
                "DELETE FROM one_shot_jobs WHERE expires_at < $cutoff;", ct, transaction,
                ("$cutoff", Iso(UtcNow.AddDays(-7))));
            await ExecuteAsync(connection, """
                DELETE FROM one_shot_model_demands WHERE observed_at < $cutoff;
                INSERT INTO one_shot_model_demands(cli, model, observed_at)
                VALUES ($cli, $model, $now)
                ON CONFLICT(cli, model) DO UPDATE SET observed_at = excluded.observed_at;
                """, ct, transaction, ("$cutoff", Iso(UtcNow.AddDays(-1))),
                ("$cli", request.Cli), ("$model", request.Model), ("$now", Iso(UtcNow)));
            var available = await ScalarAsync(connection, """
                SELECT 1 FROM runner_capabilities c JOIN runners r ON r.id = c.runner_id
                 WHERE c.capability_key = $capability AND c.advertised_status = 'ready'
                   AND c.health_state = 'healthy' AND c.fresh_until > $now
                   AND c.advertised_instance_id = r.instance_id AND r.status = 'active'
                 LIMIT 1;
                """, ct, transaction, ("$capability", capability), ("$now", Iso(UtcNow)));
            if (available is null) return;
            var id = $"osj_{Guid.NewGuid():N}";
            await ExecuteAsync(connection, """
                INSERT INTO one_shot_jobs(id, request_json, status, capability, created_at, expires_at)
                VALUES ($id, $request, 'pending', $capability, $now, $expires);
                """, ct, transaction, ("$id", id), ("$request", JsonSerializer.Serialize(request)),
                ("$capability", capability), ("$now", Iso(UtcNow)),
                ("$expires", Iso(UtcNow.AddSeconds(request.TimeoutSeconds + 30))));
            result = new(id, "pending", capability);
        }, ct);
        return result;
    }

    public async Task<OneShotJobDto?> GetOneShotJobAsync(string id, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT status, capability, text, error, usage_json, expires_at
              FROM one_shot_jobs WHERE id = $id;
            """, ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var status = reader.GetString(0);
        var error = reader.IsDBNull(3) ? null : reader.GetString(3);
        if (status is "pending" or "claimed" && Parse(reader.GetString(5)) <= UtcNow)
        {
            status = "failed";
            error = "one-shot job timed out";
        }
        return new(id, status, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            error, reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<OneShotJobUsage>(reader.GetString(4)));
    }

    public async Task<OneShotJobWork?> ClaimOneShotJobAsync(OneShotJobClaimRequest request, CancellationToken ct)
    {
        RequireWritable();
        OneShotJobWork? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await using var command = Command(connection, """
                SELECT j.id, j.request_json FROM one_shot_jobs j
                JOIN runners r ON r.id = $runner AND r.status = 'active'
                JOIN runner_capabilities c ON c.runner_id = r.id AND c.capability_key = j.capability
                WHERE j.status IN ('pending', 'claimed') AND j.expires_at > $now
                  AND (j.status = 'pending' OR j.claim_expires_at <= $now)
                  AND c.advertised_status = 'ready' AND c.health_state = 'healthy'
                  AND c.fresh_until > $now AND c.advertised_instance_id = r.instance_id
                  AND ($instance IS NULL OR r.instance_id = $instance)
                ORDER BY j.created_at LIMIT 1;
                """, transaction, ("$runner", request.RunnerId), ("$now", Iso(UtcNow)),
                ("$instance", request.InstanceId));
            string? id = null, payload = null;
            await using (var reader = await command.ExecuteReaderAsync(ct))
                if (await reader.ReadAsync(ct)) { id = reader.GetString(0); payload = reader.GetString(1); }
            if (id is null || payload is null) return;
            var token = Guid.NewGuid().ToString("N");
            var jobRequest = JsonSerializer.Deserialize<OneShotJobRequest>(payload)!;
            await ExecuteAsync(connection, """
                UPDATE one_shot_jobs SET status = 'claimed', runner_id = $runner,
                    claim_token = $token, claim_expires_at = $expires,
                    expires_at = $job_expires WHERE id = $id;
                """, ct, transaction, ("$runner", request.RunnerId), ("$token", token),
                ("$expires", Iso(UtcNow.AddSeconds(jobRequest.TimeoutSeconds + 20))),
                ("$job_expires", Iso(UtcNow.AddSeconds(jobRequest.TimeoutSeconds + 30))), ("$id", id));
            result = new(id, token, jobRequest);
        }, ct);
        return result;
    }

    public async Task<bool> CompleteOneShotJobAsync(OneShotJobCompletion completion, CancellationToken ct)
    {
        RequireWritable();
        var accepted = false;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var bytes = Encoding.UTF8.GetByteCount(completion.Text ?? string.Empty);
            var limit = Convert.ToInt32(await ScalarAsync(connection, """
                SELECT json_extract(request_json, '$.MaxOutputBytes') FROM one_shot_jobs
                 WHERE id = $id AND runner_id = $runner AND claim_token = $token
                   AND status = 'claimed' AND claim_expires_at > $now AND expires_at > $now;
                """, ct, transaction, ("$id", completion.JobId), ("$runner", completion.RunnerId),
                ("$token", completion.ClaimToken), ("$now", Iso(UtcNow))) ?? 0);
            if (limit == 0) return;
            var oversized = bytes > limit;
            var successful = completion.Success && !oversized && !string.IsNullOrWhiteSpace(completion.Text);
            await ExecuteAsync(connection, """
                UPDATE one_shot_jobs SET status = $status, text = $text, error = $error,
                    usage_json = $usage WHERE id = $id;
                """, ct, transaction, ("$status", successful ? "completed" : "failed"),
                ("$text", oversized ? null : completion.Text),
                ("$error", oversized ? "one-shot output exceeds the byte cap"
                    : successful ? null : completion.Error ?? "one-shot response was empty"),
                ("$usage", completion.Usage is null ? null : JsonSerializer.Serialize(completion.Usage)),
                ("$id", completion.JobId));
            accepted = true;
        }, ct);
        return accepted;
    }
}
