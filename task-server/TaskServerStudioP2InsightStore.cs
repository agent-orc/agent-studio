using System.Globalization;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Backing store for the Studio P2 "operations and insight" bus, token
/// usage, runtime event, and token pricing bundle.
///
/// Bus messages, token usage, and runtime events are durable, append-only
/// projections: a fenced Runner/engine principal reports them through the
/// three <c>.../ingest</c> routes (the same authentication shape as
/// <c>/api/v1/runs/{runId}/events</c>), and every frontend-facing GET route
/// in this file only ever reads rows that have already been persisted. On a
/// fresh system with nothing ingested yet, the GET routes return the
/// correctly-shaped empty/zero response rather than throwing or inventing
/// data.
///
/// Token pricing is pure in-process computation against a small static rate
/// table and has no backing table.
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioP2InsightMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_bus_messages(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                kind TEXT,
                tag TEXT,
                severity TEXT,
                participant_id TEXT,
                correlation_id TEXT,
                job_id TEXT,
                run_id TEXT,
                cli TEXT,
                skill TEXT,
                text TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_bus_messages_project_occurred
                ON studio_bus_messages(project_id, occurred_at);
            CREATE TABLE IF NOT EXISTS studio_token_usage(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                task_id TEXT,
                run_id TEXT,
                occurred_at TEXT NOT NULL,
                model TEXT,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_studio_token_usage_project_occurred
                ON studio_token_usage(project_id, occurred_at);
            CREATE INDEX IF NOT EXISTS ix_studio_token_usage_project_task
                ON studio_token_usage(project_id, task_id);
            CREATE TABLE IF NOT EXISTS studio_runtime_events(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                kind TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_studio_runtime_events_project_occurred
                ON studio_runtime_events(project_id, occurred_at);
            """, ct);
    }

    // ---- Bus message ingestion and projections ---------------------------

    public async Task<BusMessageDto> IngestBusMessageAsync(
        string projectIdentity, BusMessageIngestRequest request, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Bus message text is required.");
        var id = $"bmsg_{Guid.NewGuid():N}";
        var occurredAt = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_bus_messages(
                    id, project_id, occurred_at, kind, tag, severity, participant_id,
                    correlation_id, job_id, run_id, cli, skill, text)
                VALUES (
                    $id, $project, $occurred, $kind, $tag, $severity, $participant,
                    $correlation, $job, $run, $cli, $skill, $text);
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$occurred", occurredAt),
                ("$kind", request.Kind), ("$tag", request.Tag), ("$severity", request.Severity),
                ("$participant", request.ParticipantId), ("$correlation", request.CorrelationId),
                ("$job", request.JobId), ("$run", request.RunId), ("$cli", request.Cli),
                ("$skill", request.Skill), ("$text", request.Text));
        }, ct);
        return new BusMessageDto(
            id, project.ProjectId, Parse(occurredAt), request.Kind, request.Tag, request.Severity,
            request.ParticipantId, request.CorrelationId, request.JobId, request.RunId,
            request.Cli, request.Skill, request.Text);
    }

    public async Task<IReadOnlyList<BusMessageDto>> ListBusMessagesAsync(
        string projectIdentity,
        string? cli,
        string? correlationId,
        string? jobId,
        string? kind,
        int? limit,
        string? participantId,
        string? runId,
        string? severity,
        string? since,
        string? tag,
        string? until,
        CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var clauses = new List<string> { "project_id = $project" };
        var parameters = new List<(string Name, object? Value)> { ("$project", project.ProjectId) };

        void AddEquals(string column, string parameterName, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            clauses.Add($"{column} = {parameterName}");
            parameters.Add((parameterName, value));
        }

        AddEquals("cli", "$cli", cli);
        AddEquals("correlation_id", "$correlationId", correlationId);
        AddEquals("job_id", "$jobId", jobId);
        AddEquals("kind", "$kind", kind);
        AddEquals("participant_id", "$participantId", participantId);
        AddEquals("run_id", "$runId", runId);
        AddEquals("severity", "$severity", severity);
        AddEquals("tag", "$tag", tag);
        if (!string.IsNullOrWhiteSpace(since))
        {
            clauses.Add("occurred_at >= $since");
            parameters.Add(("$since", Iso(Parse(since))));
        }
        if (!string.IsNullOrWhiteSpace(until))
        {
            clauses.Add("occurred_at <= $until");
            parameters.Add(("$until", Iso(Parse(until))));
        }
        var take = Math.Clamp(limit ?? 200, 1, 1000);
        parameters.Add(("$limit", take));

        await using var connection = await OpenReadyAsync(ct);
        var sql = $"""
            SELECT id, project_id, occurred_at, kind, tag, severity, participant_id,
                   correlation_id, job_id, run_id, cli, skill, text
              FROM studio_bus_messages
             WHERE {string.Join(" AND ", clauses)}
             ORDER BY occurred_at DESC, rowid DESC
             LIMIT $limit;
            """;
        await using var command = Command(connection, sql, parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<BusMessageDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadBusMessage(reader));
        return result;
    }

    public async Task<BusMessageDto?> GetBusMessageAsync(string projectIdentity, string id, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, occurred_at, kind, tag, severity, participant_id,
                   correlation_id, job_id, run_id, cli, skill, text
              FROM studio_bus_messages
             WHERE project_id = $project AND id = $id;
            """, ("$project", project.ProjectId), ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadBusMessage(reader) : null;
    }

    public async Task<IReadOnlyList<BusMessageDto>> ListRecentBusMessagesAsync(
        string projectIdentity, int? limit, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var take = Math.Clamp(limit ?? 50, 1, 500);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, occurred_at, kind, tag, severity, participant_id,
                   correlation_id, job_id, run_id, cli, skill, text
              FROM studio_bus_messages
             WHERE project_id = $project
             ORDER BY occurred_at DESC, rowid DESC
             LIMIT $limit;
            """, ("$project", project.ProjectId), ("$limit", take));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<BusMessageDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadBusMessage(reader));
        return result;
    }

    public async Task<BusMessageSummaryResponse> GetBusSummaryAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        const int windowHours = 24;
        var cutoff = Iso(UtcNow.AddHours(-windowHours));
        await using var connection = await OpenReadyAsync(ct);
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySeverity = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        await using (var command = Command(connection, """
            SELECT COALESCE(kind, 'unspecified'), COALESCE(severity, 'unspecified'), count(*)
              FROM studio_bus_messages
             WHERE project_id = $project AND occurred_at >= $cutoff
             GROUP BY kind, severity;
            """, ("$project", project.ProjectId), ("$cutoff", cutoff)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var kind = reader.GetString(0);
                var severity = reader.GetString(1);
                var count = Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture);
                byKind[kind] = byKind.GetValueOrDefault(kind) + count;
                bySeverity[severity] = bySeverity.GetValueOrDefault(severity) + count;
                total += count;
            }
        }
        return new BusMessageSummaryResponse(project.ProjectId, windowHours, total, byKind, bySeverity, UtcNow);
    }

    public async Task<BusTokenAggregateResponse> GetBusTokenAggregateAsync(
        string projectIdentity, string? since, string? until, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        DateTime? sinceValue = string.IsNullOrWhiteSpace(since) ? null : Parse(since);
        DateTime? untilValue = string.IsNullOrWhiteSpace(until) ? null : Parse(until);
        var clauses = new List<string> { "project_id = $project" };
        var parameters = new List<(string Name, object? Value)> { ("$project", project.ProjectId) };
        if (sinceValue is not null)
        {
            clauses.Add("occurred_at >= $since");
            parameters.Add(("$since", Iso(sinceValue.Value)));
        }
        if (untilValue is not null)
        {
            clauses.Add("occurred_at <= $until");
            parameters.Add(("$until", Iso(untilValue.Value)));
        }
        await using var connection = await OpenReadyAsync(ct);
        var sql = $"""
            SELECT COALESCE(sum(input_tokens), 0), COALESCE(sum(output_tokens), 0),
                   COALESCE(sum(cache_read_tokens), 0), COALESCE(sum(cache_creation_tokens), 0), count(*)
              FROM studio_token_usage
             WHERE {string.Join(" AND ", clauses)};
            """;
        await using var command = Command(connection, sql, parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var input = reader.GetInt64(0);
        var output = reader.GetInt64(1);
        var cacheRead = reader.GetInt64(2);
        var cacheCreation = reader.GetInt64(3);
        var count = Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture);
        return new BusTokenAggregateResponse(
            project.ProjectId, sinceValue, untilValue, input, output, cacheRead, cacheCreation,
            input + output + cacheRead + cacheCreation, count, UtcNow);
    }

    private static BusMessageDto ReadBusMessage(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetString(12));

    // ---- Token usage ingestion and projections ---------------------------

    public async Task<TokenUsageDto> IngestTokenUsageAsync(
        string projectIdentity, TokenUsageIngestRequest request, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        if (request.InputTokens < 0 || request.OutputTokens < 0
            || request.CacheReadTokens < 0 || request.CacheCreationTokens < 0)
            throw new ArgumentException("Token counts cannot be negative.");
        var id = $"tku_{Guid.NewGuid():N}";
        var occurredAt = Iso(request.OccurredAt);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_token_usage(
                    id, project_id, task_id, run_id, occurred_at, model,
                    input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens)
                VALUES (
                    $id, $project, $task, $run, $occurred, $model,
                    $input, $output, $cacheRead, $cacheCreation);
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$task", request.TaskId),
                ("$run", request.RunId), ("$occurred", occurredAt), ("$model", request.Model),
                ("$input", request.InputTokens), ("$output", request.OutputTokens),
                ("$cacheRead", request.CacheReadTokens), ("$cacheCreation", request.CacheCreationTokens));
        }, ct);
        return new TokenUsageDto(
            id, project.ProjectId, request.TaskId, request.RunId, Parse(occurredAt), request.Model,
            request.InputTokens, request.OutputTokens, request.CacheReadTokens, request.CacheCreationTokens);
    }

    public async Task<ExpensiveTaskTokenUsageResponse> GetExpensiveTaskTokenUsageAsync(
        string projectIdentity, int? limit, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var take = Math.Clamp(limit ?? 10, 1, 200);
        await using var connection = await OpenReadyAsync(ct);
        var tasks = new List<ExpensiveTaskTokenUsageDto>();
        await using (var command = Command(connection, """
            SELECT task_id,
                   sum(input_tokens), sum(output_tokens), sum(cache_read_tokens), sum(cache_creation_tokens),
                   sum(input_tokens + output_tokens + cache_read_tokens + cache_creation_tokens) AS total,
                   count(*)
              FROM studio_token_usage
             WHERE project_id = $project AND task_id IS NOT NULL
             GROUP BY task_id
             ORDER BY total DESC
             LIMIT $limit;
            """, ("$project", project.ProjectId), ("$limit", take)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                tasks.Add(new ExpensiveTaskTokenUsageDto(
                    reader.GetString(0),
                    reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture)));
            }
        }
        return new ExpensiveTaskTokenUsageResponse(project.ProjectId, tasks, UtcNow);
    }

    public async Task<TokenUsageHeatmapResponse> GetTokenUsageHeatmapAsync(
        string projectIdentity, int? days, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var clampedDays = Math.Clamp(days ?? 30, 1, 365);
        await using var connection = await OpenReadyAsync(ct);
        var points = await ReadDailyTokenTotalsAsync(connection, project.ProjectId, clampedDays, ct);
        return new TokenUsageHeatmapResponse(project.ProjectId, clampedDays, points, UtcNow);
    }

    public async Task<IReadOnlyList<TokenUsageDto>> GetTokenUsageForTaskAsync(
        string projectIdentity, string taskId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_id, run_id, occurred_at, model,
                   input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens
              FROM studio_token_usage
             WHERE project_id = $project AND task_id = $task
             ORDER BY occurred_at, rowid;
            """, ("$project", project.ProjectId), ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<TokenUsageDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadTokenUsage(reader));
        return result;
    }

    public async Task<TokenUsagePipelineCostResponse> GetTokenUsagePipelineCostAsync(
        string projectIdentity, int? days, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var clampedDays = Math.Clamp(days ?? 30, 1, 365);
        await using var connection = await OpenReadyAsync(ct);
        // A true "pipeline" grouping has no durable representation in this
        // projection; a daily total is the documented acceptable simplification.
        var points = await ReadDailyTokenTotalsAsync(connection, project.ProjectId, clampedDays, ct);
        return new TokenUsagePipelineCostResponse(project.ProjectId, clampedDays, points, UtcNow);
    }

    public async Task<TokenUsageSummaryResponse> GetTokenUsageSummaryAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT COALESCE(sum(input_tokens), 0), COALESCE(sum(output_tokens), 0),
                   COALESCE(sum(cache_read_tokens), 0), COALESCE(sum(cache_creation_tokens), 0), count(*)
              FROM studio_token_usage
             WHERE project_id = $project;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var input = reader.GetInt64(0);
        var output = reader.GetInt64(1);
        var cacheRead = reader.GetInt64(2);
        var cacheCreation = reader.GetInt64(3);
        var count = Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture);
        return new TokenUsageSummaryResponse(
            project.ProjectId, input, output, cacheRead, cacheCreation,
            input + output + cacheRead + cacheCreation, count, UtcNow);
    }

    private async Task<IReadOnlyList<TokenUsageDailyPointDto>> ReadDailyTokenTotalsAsync(
        SqliteConnection connection, string projectId, int days, CancellationToken ct)
    {
        var start = UtcNow.AddDays(-(days - 1)).Date;
        var cutoff = Iso(start);
        var totals = new Dictionary<string, (long Input, long Output, long CacheRead, long CacheCreation)>(
            StringComparer.Ordinal);
        await using (var command = Command(connection, """
            SELECT substr(occurred_at, 1, 10) AS day,
                   sum(input_tokens), sum(output_tokens), sum(cache_read_tokens), sum(cache_creation_tokens)
              FROM studio_token_usage
             WHERE project_id = $project AND occurred_at >= $cutoff
             GROUP BY day;
            """, ("$project", projectId), ("$cutoff", cutoff)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                totals[reader.GetString(0)] = (
                    reader.IsDBNull(1) ? 0L : reader.GetInt64(1),
                    reader.IsDBNull(2) ? 0L : reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0L : reader.GetInt64(3),
                    reader.IsDBNull(4) ? 0L : reader.GetInt64(4));
            }
        }
        var points = new List<TokenUsageDailyPointDto>(days);
        for (var i = 0; i < days; i++)
        {
            var day = start.AddDays(i);
            var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var value = totals.TryGetValue(key, out var found)
                ? found
                : (Input: 0L, Output: 0L, CacheRead: 0L, CacheCreation: 0L);
            points.Add(new TokenUsageDailyPointDto(
                key, value.Input, value.Output, value.CacheRead, value.CacheCreation,
                value.Input + value.Output + value.CacheRead + value.CacheCreation));
        }
        return points;
    }

    private static TokenUsageDto ReadTokenUsage(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9));

    // ---- Runtime events ingestion and projections -------------------------

    public async Task<RuntimeEventDto> IngestRuntimeEventAsync(
        string projectIdentity, RuntimeEventIngestRequest request, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        if (string.IsNullOrWhiteSpace(request.Kind))
            throw new ArgumentException("Runtime event kind is required.");
        if (string.IsNullOrWhiteSpace(request.PayloadJson))
            throw new ArgumentException("Runtime event payload is required.");
        var id = $"rtev_{Guid.NewGuid():N}";
        var occurredAt = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_runtime_events(id, project_id, occurred_at, kind, payload_json)
                VALUES ($id, $project, $occurred, $kind, $payload);
                """, ct, transaction,
                ("$id", id), ("$project", project.ProjectId), ("$occurred", occurredAt),
                ("$kind", request.Kind), ("$payload", request.PayloadJson));
        }, ct);
        return new RuntimeEventDto(id, project.ProjectId, Parse(occurredAt), request.Kind, request.PayloadJson);
    }

    public async Task<RuntimeEventListResponse> ListRuntimeEventsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        const int limit = 200;
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, occurred_at, kind, payload_json
              FROM studio_runtime_events
             WHERE project_id = $project
             ORDER BY occurred_at DESC, rowid DESC
             LIMIT $limit;
            """, ("$project", project.ProjectId), ("$limit", limit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<RuntimeEventDto>();
        while (await reader.ReadAsync(ct))
        {
            events.Add(new RuntimeEventDto(
                reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4)));
        }
        return new RuntimeEventListResponse(project.ProjectId, events, UtcNow);
    }

    // ---- Token pricing ------------------------------------------------------

    /// <summary>
    /// PLACEHOLDER rates. The real Studio pricing surface goes through the
    /// exactly-pinned <c>TokenEconomy</c> package behind
    /// <c>backend/Features/Runner/TokenPricing.cs</c>
    /// (see <c>docs/system/domains/token-pricing.md</c>), which
    /// <c>task-server</c> cannot reference. Rates are per one million
    /// tokens, in USD, and are not sourced from any live catalog: they exist
    /// only so this calculator has something deterministic to compute
    /// against for the current model-routing tiers named in
    /// <c>docs/system/domains/model-routing-policy.md</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, TokenPricingRate> PlaceholderPricing =
        new Dictionary<string, TokenPricingRate>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-5"] = new TokenPricingRate(3.00m, 15.00m, 0.30m, 3.75m),
            ["claude-opus-4-8"] = new TokenPricingRate(15.00m, 75.00m, 1.50m, 18.75m),
            ["claude-haiku-4-5"] = new TokenPricingRate(0.80m, 4.00m, 0.08m, 1.00m),
        };

    private sealed record TokenPricingRate(
        decimal InputPerMillion,
        decimal OutputPerMillion,
        decimal CacheReadPerMillion,
        decimal CacheCreationPerMillion);

    public TokenPricingBreakdownDto CalculateTokenPricing(CalculateTokenPricingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("A model id is required.");
        if (request.InputTokens < 0 || request.OutputTokens < 0
            || request.CacheReadTokens < 0 || request.CacheCreationTokens < 0)
            throw new ArgumentException("Token counts cannot be negative.");
        if (!PlaceholderPricing.TryGetValue(request.Model.Trim(), out var rate))
            throw new ArgumentException(
                $"Model '{request.Model}' has no placeholder price entry. Known models: " +
                string.Join(", ", PlaceholderPricing.Keys));

        var inputCost = request.InputTokens / 1_000_000m * rate.InputPerMillion;
        var outputCost = request.OutputTokens / 1_000_000m * rate.OutputPerMillion;
        var cacheReadCost = request.CacheReadTokens / 1_000_000m * rate.CacheReadPerMillion;
        var cacheCreationCost = request.CacheCreationTokens / 1_000_000m * rate.CacheCreationPerMillion;
        return new TokenPricingBreakdownDto(
            request.Model.Trim(),
            request.InputTokens,
            request.OutputTokens,
            request.CacheReadTokens,
            request.CacheCreationTokens,
            Math.Round(inputCost, 6),
            Math.Round(outputCost, 6),
            Math.Round(cacheReadCost, 6),
            Math.Round(cacheCreationCost, 6),
            Math.Round(inputCost + outputCost + cacheReadCost + cacheCreationCost, 6),
            "USD",
            rate.InputPerMillion,
            rate.OutputPerMillion,
            rate.CacheReadPerMillion,
            rate.CacheCreationPerMillion,
            "task-server-placeholder");
    }
}
