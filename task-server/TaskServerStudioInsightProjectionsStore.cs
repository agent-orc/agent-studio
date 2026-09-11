using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Read-only Studio insight projections for the P2 bundle: agent message
/// bus, runtime events, token usage, cycle time, throughput, regression
/// radar, test runs, and visual evidence. Every projection here folds the
/// durable <c>events</c> and <c>audit</c> tables a Runner already writes
/// through the existing fenced event-ingestion contract
/// (<c>POST /api/v1/runs/{runId}/events</c>); none of these routes read a
/// second, disk-backed copy of the data. A Runner reports bus messages,
/// runtime events, token usage, test runs, regression findings, and visual
/// evidence as ordinary typed events using the <see cref="StudioInsightEventKinds"/>
/// convention, the same way it reports any other lifecycle event.
/// </summary>
public static class StudioInsightEventKinds
{
    public const string BusMessage = "studio.bus.message";
    public const string RuntimeEvent = "studio.runtime.event";
    public const string TokenUsage = "studio.token-usage";
    public const string TestRun = "studio.test-run";
    public const string RegressionFinding = "studio.regression-finding";
    public const string VisualEvidence = "studio.visual-evidence";
    public const string PipelineAlert = "studio.pipeline.accepted-integration-alert";
}

public sealed partial class TaskServerStore
{
    // --- Bus -------------------------------------------------------------------

    public Task<StudioBusMessageListResponse> ListBusMessagesAsync(string projectIdentity, int limit, CancellationToken ct) =>
        ListBusMessagesInternalAsync(projectIdentity, limit, ct);

    public Task<StudioBusMessageListResponse> ListRecentBusMessagesAsync(string projectIdentity, CancellationToken ct) =>
        ListBusMessagesInternalAsync(projectIdentity, 20, ct);

    private async Task<StudioBusMessageListResponse> ListBusMessagesInternalAsync(string projectIdentity, int limit, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT e.cursor, t.project_id, e.task_id, e.payload_json, e.occurred_at
              FROM events e JOIN tasks t ON t.id = e.task_id
             WHERE t.project_id = $project AND e.kind = $kind
             ORDER BY e.cursor DESC LIMIT $limit;
            """, ("$project", project.ProjectId), ("$kind", StudioInsightEventKinds.BusMessage), ("$limit", (long)limit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StudioBusMessageDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadBusMessage(reader));
        return new StudioBusMessageListResponse(result);
    }

    public async Task<StudioBusMessageDto> GetBusMessageAsync(string projectIdentity, long id, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT e.cursor, t.project_id, e.task_id, e.payload_json, e.occurred_at
              FROM events e JOIN tasks t ON t.id = e.task_id
             WHERE t.project_id = $project AND e.kind = $kind AND e.cursor = $id;
            """, ("$project", project.ProjectId), ("$kind", StudioInsightEventKinds.BusMessage), ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Bus message was not found.");
        return ReadBusMessage(reader);
    }

    public async Task<StudioBusSummaryResponse> GetBusSummaryAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT e.payload_json, e.occurred_at
              FROM events e JOIN tasks t ON t.id = e.task_id
             WHERE t.project_id = $project AND e.kind = $kind;
            """, ("$project", project.ProjectId), ("$kind", StudioInsightEventKinds.BusMessage));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var countsByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = 0;
        DateTime? last = null;
        while (await reader.ReadAsync(ct))
        {
            count++;
            var occurred = Parse(reader.GetString(1));
            if (last is null || occurred > last) last = occurred;
            var kind = TryReadPayloadField(reader.GetString(0), "kind") ?? "message";
            countsByKind[kind] = countsByKind.GetValueOrDefault(kind) + 1;
        }
        return new StudioBusSummaryResponse(project.ProjectId, count, last, countsByKind);
    }

    private static StudioBusMessageDto ReadBusMessage(SqliteDataReader reader)
    {
        var payload = reader.GetString(3);
        return new StudioBusMessageDto(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            TryReadPayloadField(payload, "channel") ?? "default",
            TryReadPayloadField(payload, "role") ?? "agent",
            TryReadPayloadField(payload, "kind") ?? "message",
            TryReadPayloadField(payload, "summary") ?? string.Empty,
            Parse(reader.GetString(4)));
    }

    // --- Runtime -----------------------------------------------------------------

    public async Task<StudioRuntimeEventListResponse> ListRuntimeEventsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT e.cursor, t.project_id, e.payload_json, e.occurred_at
              FROM events e JOIN tasks t ON t.id = e.task_id
             WHERE t.project_id = $project AND e.kind = $kind
             ORDER BY e.cursor DESC LIMIT 200;
            """, ("$project", project.ProjectId), ("$kind", StudioInsightEventKinds.RuntimeEvent));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StudioRuntimeEventDto>();
        while (await reader.ReadAsync(ct))
        {
            var payload = reader.GetString(2);
            result.Add(new StudioRuntimeEventDto(
                reader.GetInt64(0), reader.GetString(1), TryReadPayloadField(payload, "kind") ?? "event",
                TryReadPayloadField(payload, "summary") ?? string.Empty, Parse(reader.GetString(3))));
        }
        return new StudioRuntimeEventListResponse(result);
    }

    // --- Pipeline alert (instance-wide) ------------------------------------------

    public async Task<PipelineAcceptedIntegrationAlertResponse> GetPipelineAcceptedIntegrationAlertAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT payload_json, occurred_at FROM events WHERE kind = $kind ORDER BY cursor DESC LIMIT 1;
            """, ("$kind", StudioInsightEventKinds.PipelineAlert));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new PipelineAcceptedIntegrationAlertResponse(false, null, null);
        return new PipelineAcceptedIntegrationAlertResponse(
            true, TryReadPayloadField(reader.GetString(0), "summary"), Parse(reader.GetString(1)));
    }

    // --- Token usage -------------------------------------------------------------

    public async Task<StudioTokenUsageSummaryResponse> GetTokenUsageSummaryAsync(string projectIdentity, CancellationToken ct)
    {
        var samples = await ReadTokenUsageSamplesAsync(projectIdentity, ct);
        return new StudioTokenUsageSummaryResponse(
            samples.ProjectId, samples.Items.Sum(item => item.InputTokens), samples.Items.Sum(item => item.OutputTokens),
            samples.Items.Sum(item => item.CostUsd), samples.Items.Count);
    }

    public async Task<StudioTokenUsageHeatmapResponse> GetTokenUsageHeatmapAsync(string projectIdentity, CancellationToken ct)
    {
        var samples = await ReadTokenUsageSamplesAsync(projectIdentity, ct);
        var days = samples.Items
            .GroupBy(item => DateOnly.FromDateTime(item.OccurredAt))
            .OrderBy(group => group.Key)
            .Select(group => new StudioTokenUsageHeatmapEntry(
                group.Key, group.Sum(item => item.InputTokens), group.Sum(item => item.OutputTokens), group.Sum(item => item.CostUsd)))
            .ToList();
        return new StudioTokenUsageHeatmapResponse(samples.ProjectId, days);
    }

    public async Task<StudioTokenUsageExpensiveResponse> GetExpensiveTokenUsageAsync(string projectIdentity, CancellationToken ct)
    {
        var samples = await ReadTokenUsageSamplesAsync(projectIdentity, ct);
        var byTask = samples.Items
            .GroupBy(item => item.TaskId)
            .Select(group => new StudioTokenUsageExpensiveEntry(
                group.Key, group.First().TaskKey, group.Sum(item => item.CostUsd),
                group.Sum(item => item.InputTokens), group.Sum(item => item.OutputTokens)))
            .OrderByDescending(entry => entry.CostUsd)
            .Take(20)
            .ToList();
        return new StudioTokenUsageExpensiveResponse(samples.ProjectId, byTask);
    }

    public async Task<StudioTokenUsageJobResponse> GetTokenUsageForJobAsync(string projectIdentity, string taskIdentity, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectIdentity, taskIdentity, ct) ?? throw new KeyNotFoundException("Task was not found.");
        var samples = await ReadTokenUsageSamplesAsync(projectIdentity, ct);
        var forTask = samples.Items.Where(item => item.TaskId == task.TaskId).ToList();
        return new StudioTokenUsageJobResponse(
            samples.ProjectId, task.TaskId, forTask.Sum(item => item.InputTokens), forTask.Sum(item => item.OutputTokens),
            forTask.Sum(item => item.CostUsd), forTask.Count);
    }

    public async Task<StudioTokenUsagePipelineCostResponse> GetTokenUsagePipelineCostAsync(string projectIdentity, CancellationToken ct)
    {
        var samples = await ReadTokenUsageSamplesAsync(projectIdentity, ct);
        var stages = samples.Items
            .GroupBy(item => item.Stage)
            .Select(group => new StudioTokenUsagePipelineCostEntry(
                group.Key, group.Sum(item => item.InputTokens), group.Sum(item => item.OutputTokens), group.Sum(item => item.CostUsd)))
            .OrderByDescending(entry => entry.CostUsd)
            .ToList();
        return new StudioTokenUsagePipelineCostResponse(samples.ProjectId, stages);
    }

    public async Task<StudioBusTokenAggregateResponse> GetBusTokenAggregateAsync(string projectIdentity, CancellationToken ct)
    {
        var samples = await ReadTokenUsageSamplesAsync(projectIdentity, ct);
        return new StudioBusTokenAggregateResponse(
            samples.ProjectId, samples.Items.Sum(item => item.InputTokens), samples.Items.Sum(item => item.OutputTokens),
            samples.Items.Sum(item => item.CostUsd));
    }

    private sealed record TokenUsageSample(string TaskId, string? TaskKey, string Stage, long InputTokens, long OutputTokens, decimal CostUsd, DateTime OccurredAt);
    private sealed record TokenUsageSamples(string ProjectId, IReadOnlyList<TokenUsageSample> Items);

    private async Task<TokenUsageSamples> ReadTokenUsageSamplesAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT e.task_id, t.task_key, e.payload_json, e.occurred_at
              FROM events e JOIN tasks t ON t.id = e.task_id
             WHERE t.project_id = $project AND e.kind = $kind;
            """, ("$project", project.ProjectId), ("$kind", StudioInsightEventKinds.TokenUsage));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<TokenUsageSample>();
        while (await reader.ReadAsync(ct))
        {
            var payload = JsonDocument.Parse(reader.GetString(2)).RootElement;
            items.Add(new TokenUsageSample(
                reader.GetString(0), reader.GetString(1),
                payload.TryGetProperty("stage", out var stage) ? stage.GetString() ?? "unspecified" : "unspecified",
                payload.TryGetProperty("inputTokens", out var input) ? input.GetInt64() : 0,
                payload.TryGetProperty("outputTokens", out var output) ? output.GetInt64() : 0,
                payload.TryGetProperty("costUsd", out var cost) ? cost.GetDecimal() : 0m,
                Parse(reader.GetString(3))));
        }
        return new TokenUsageSamples(project.ProjectId, items);
    }

    public Task<TokenPricingCalculateResponse> CalculateTokenPricingAsync(TokenPricingCalculateRequest request, CancellationToken ct)
    {
        var (inputRate, outputRate) = TokenPricingRates.TryGetValue(request.Model, out var rates) ? rates : (0.0000015m, 0.000006m);
        var cost = request.InputTokens * inputRate + request.OutputTokens * outputRate;
        return Task.FromResult(new TokenPricingCalculateResponse(request.Model, request.InputTokens, request.OutputTokens, cost));
    }

    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> TokenPricingRates =
        new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-5"] = (0.000003m, 0.000015m),
            ["claude-opus-4-8"] = (0.000005m, 0.000025m),
            ["claude-haiku-4-5"] = (0.0000008m, 0.000004m),
        };

    // --- Cycle time / throughput --------------------------------------------------

    public async Task<ProjectCycleTimeResponse> GetProjectCycleTimeAsync(string projectIdentity, string window, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var since = ResolveCycleTimeWindow(window, UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT task_key, created_at, updated_at FROM tasks
             WHERE project_id = $project AND state = $completed AND updated_at >= $since;
            """, ("$project", project.ProjectId), ("$completed", StudioTaskLanes.Completed), ("$since", Iso(since)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var hours = new List<double>();
        while (await reader.ReadAsync(ct))
            hours.Add((Parse(reader.GetString(2)) - Parse(reader.GetString(1))).TotalHours);
        return new ProjectCycleTimeResponse(project.ProjectId, window, Summarize(hours));
    }

    public async Task<TaskCycleTimeResponse> GetTaskCycleTimeAsync(string projectIdentity, string taskKey, CancellationToken ct)
    {
        var task = await GetTaskAsync(projectIdentity, taskKey, ct) ?? throw new KeyNotFoundException("Task was not found.");
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT detail_json, occurred_at FROM audit
             WHERE target_type = 'task' AND target_id = $task AND action = 'task.moved'
             ORDER BY sequence;
            """, ("$task", task.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var transitions = new List<CycleTimeTransitionDto>();
        while (await reader.ReadAsync(ct))
        {
            var detail = JsonDocument.Parse(reader.GetString(0)).RootElement;
            transitions.Add(new CycleTimeTransitionDto(
                detail.TryGetProperty("from", out var from) ? from.GetString() ?? "" : "",
                detail.TryGetProperty("to", out var to) ? to.GetString() ?? "" : "",
                Parse(reader.GetString(1))));
        }
        return new TaskCycleTimeResponse(task.ProjectId, task.TaskId, transitions);
    }

    public async Task<ProjectThroughputResponse> GetProjectThroughputAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT updated_at FROM tasks WHERE project_id = $project AND state = $completed
             AND updated_at >= $since;
            """, ("$project", project.ProjectId), ("$completed", StudioTaskLanes.Completed),
            ("$since", Iso(UtcNow.AddDays(-30))));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var byDay = new Dictionary<DateOnly, int>();
        while (await reader.ReadAsync(ct))
        {
            var day = DateOnly.FromDateTime(Parse(reader.GetString(0)));
            byDay[day] = byDay.GetValueOrDefault(day) + 1;
        }
        var days = byDay.OrderBy(pair => pair.Key).Select(pair => new ThroughputEntry(pair.Key, pair.Value)).ToList();
        return new ProjectThroughputResponse(project.ProjectId, days);
    }

    private static DateTime ResolveCycleTimeWindow(string window, DateTime now) => window switch
    {
        "7d" => now.AddDays(-7),
        "30d" => now.AddDays(-30),
        "all" => DateTime.MinValue,
        _ => now.AddDays(-30),
    };

    private static CycleTimeSummaryDto Summarize(List<double> hours)
    {
        if (hours.Count == 0) return new CycleTimeSummaryDto(0, 0, 0);
        var sorted = hours.OrderBy(value => value).ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return new CycleTimeSummaryDto(sorted.Count, sorted.Average(), median);
    }

    // --- Regression radar / test runs / visual evidence -------------------------

    public async Task<RegressionRadarResponse> ListRegressionRadarAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var rows = await ReadInsightEventsAsync(project.ProjectId, StudioInsightEventKinds.RegressionFinding, ct);
        return new RegressionRadarResponse(rows
            .Select(row => new RegressionRadarEntryDto(
                row.Cursor, TryReadPayloadField(row.PayloadJson, "summary") ?? string.Empty,
                TryReadPayloadField(row.PayloadJson, "severity") ?? "info", row.OccurredAt))
            .ToList());
    }

    public async Task<TestRunListResponse> ListTestRunsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var rows = await ReadInsightEventsAsync(project.ProjectId, StudioInsightEventKinds.TestRun, ct);
        return new TestRunListResponse(rows
            .Select(row => new TestRunEntryDto(
                row.Cursor, TryReadPayloadField(row.PayloadJson, "summary") ?? string.Empty,
                TryReadPayloadField(row.PayloadJson, "outcome") ?? "unknown", row.OccurredAt))
            .ToList());
    }

    public async Task<VisualEvidenceListResponse> ListVisualEvidenceAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var rows = await ReadInsightEventsAsync(project.ProjectId, StudioInsightEventKinds.VisualEvidence, ct);
        await using var connection = await OpenReadyAsync(ct);
        var items = new List<VisualEvidenceItemDto>();
        foreach (var row in rows)
        {
            await using var command = Command(connection,
                "SELECT 1 FROM studio_visual_evidence_acks WHERE event_cursor = $cursor;", ("$cursor", row.Cursor));
            var acknowledged = await command.ExecuteScalarAsync(ct) is not null;
            items.Add(new VisualEvidenceItemDto(row.Cursor, TryReadPayloadField(row.PayloadJson, "summary") ?? string.Empty, row.OccurredAt, acknowledged));
        }
        return new VisualEvidenceListResponse(items);
    }

    public async Task<AcknowledgeVisualEvidenceResponse> AcknowledgeVisualEvidenceAsync(
        string projectIdentity, long itemId, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var rows = await ReadInsightEventsAsync(project.ProjectId, StudioInsightEventKinds.VisualEvidence, ct);
        if (rows.All(row => row.Cursor != itemId)) throw new KeyNotFoundException("Visual evidence item was not found.");
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_visual_evidence_acks(event_cursor, acknowledged_by, acknowledged_at)
            VALUES ($cursor, $actor, $now)
            ON CONFLICT(event_cursor) DO UPDATE SET acknowledged_by = excluded.acknowledged_by, acknowledged_at = excluded.acknowledged_at;
            """, ct, ("$cursor", itemId), ("$actor", actorId), ("$now", Iso(now)));
        return new AcknowledgeVisualEvidenceResponse(itemId, now);
    }

    private sealed record InsightEventRow(long Cursor, string PayloadJson, DateTime OccurredAt);

    private async Task<IReadOnlyList<InsightEventRow>> ReadInsightEventsAsync(string projectId, string kind, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT e.cursor, e.payload_json, e.occurred_at
              FROM events e JOIN tasks t ON t.id = e.task_id
             WHERE t.project_id = $project AND e.kind = $kind
             ORDER BY e.cursor DESC LIMIT 200;
            """, ("$project", projectId), ("$kind", kind));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<InsightEventRow>();
        while (await reader.ReadAsync(ct)) result.Add(new InsightEventRow(reader.GetInt64(0), reader.GetString(1), Parse(reader.GetString(2))));
        return result;
    }

    private static string? TryReadPayloadField(string payloadJson, string field)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
