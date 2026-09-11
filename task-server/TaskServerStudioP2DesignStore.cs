using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Storage for the P2 "design council, proposals, visual evidence, skill
/// readiness, project snapshot" route bundle.
/// <para>
/// Design actions are dispatch-backed: <see cref="StudioOperationKinds.DesignAction"/>
/// operations are created as fenced <c>studio_operations</c> rows (see
/// <c>TaskServerStudioOperationsStore.cs</c>) and this file only projects
/// their durable results into the council/overview/references shapes. The
/// one piece of design state this file owns directly is
/// <c>studio_design_council_acceptance</c>, a per-file acceptance mark a
/// user sets without spawning a new design action.
/// </para>
/// <para>
/// Proposals (<c>studio_proposals</c>) are also dispatch-backed, but unlike
/// design's "latest result wins" shape, a proposal is an independently
/// actionable row: <see cref="StudioOperationKinds.ProposalsGenerate"/> and
/// <see cref="StudioOperationKinds.ProposalsRefineFeedback"/> operations are
/// materialized into rows by <c>ProposalsCompletionProjector</c>
/// (<c>StudioP2DesignEndpoints.cs</c>) once they succeed, via
/// <see cref="ApplyGeneratedProposalsAsync"/>.
/// </para>
/// <para>
/// Skill readiness fix-task is a pure dispatch with no local projection: the
/// route only ever creates the operation.
/// </para>
/// <para>
/// Visual evidence (<c>studio_visual_evidence</c>) is a plain
/// Task-Server-owned table. This P2 bundle owns only the list and
/// acknowledge routes; nothing in it creates rows over HTTP yet, so
/// <see cref="RecordVisualEvidenceAsync"/> exists for tests and future
/// producers (for example a design action result) to seed rows.
/// </para>
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioP2DesignMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_design_council_acceptance(
                project_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                accepted_at TEXT NOT NULL,
                accepted_by TEXT NOT NULL,
                PRIMARY KEY(project_id, file_name)
            );
            CREATE TABLE IF NOT EXISTS studio_proposals(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                generation INTEGER NOT NULL DEFAULT 1,
                status TEXT NOT NULL DEFAULT 'pending',
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                decided_at TEXT
            );
            CREATE TABLE IF NOT EXISTS studio_visual_evidence(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                acknowledged_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_studio_proposals_project_generation
                ON studio_proposals(project_id, generation);
            CREATE INDEX IF NOT EXISTS ix_studio_visual_evidence_project_captured
                ON studio_visual_evidence(project_id, captured_at);
            """, ct);
    }

    // ------------------------------------------------------------------
    // Design (dispatch-backed reads, plus the council-accept local table).
    // ------------------------------------------------------------------

    public async Task<StudioOperationAcceptedResponse> DispatchDesignActionAsync(
        string projectIdentity, string action, DesignActionDispatchRequest request, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("A design action is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await CreateStudioOperationAsync(
            StudioOperationKinds.DesignAction,
            project.ProjectId,
            taskId: null,
            request: new DesignDispatchRequest(project.ProjectId, action, request.Payload),
            trigger: action,
            severity: null,
            topic: null,
            ct: ct);
        return new StudioOperationAcceptedResponse(operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
    }

    public async Task<DesignCouncilListResponse> GetDesignCouncilAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.DesignAction, project.ProjectId, ct);
        var council = ExtractJsonArray(operation?.ResultJson, "council");
        return new DesignCouncilListResponse(operation?.OperationId, operation?.CompletedAt, council);
    }

    public async Task<JsonElement> GetDesignCouncilEntryAsync(string projectIdentity, string fileName, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.DesignAction, project.ProjectId, ct);
        foreach (var entry in ExtractJsonArray(operation?.ResultJson, "council"))
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("fileName", out var name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), fileName, StringComparison.Ordinal))
                return entry;
        }
        throw new KeyNotFoundException($"Design council entry '{fileName}' was not found.");
    }

    public async Task<DesignCouncilAcceptanceDto> AcceptDesignCouncilEntryAsync(
        string projectIdentity, string fileName, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_design_council_acceptance(project_id, file_name, accepted_at, accepted_by)
            VALUES ($project, $file, $now, $actor)
            ON CONFLICT(project_id, file_name)
            DO UPDATE SET accepted_at = excluded.accepted_at, accepted_by = excluded.accepted_by;
            """, ct,
            ("$project", project.ProjectId), ("$file", fileName), ("$now", now), ("$actor", actorId));
        return new DesignCouncilAcceptanceDto(project.ProjectId, fileName, Parse(now), actorId);
    }

    public async Task<DesignOverviewResponse> GetDesignOverviewAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.DesignAction, project.ProjectId, ct);
        return new DesignOverviewResponse(
            operation?.OperationId, operation?.CompletedAt, ExtractJsonProperty(operation?.ResultJson, "overview"));
    }

    public async Task<DesignReferencesResponse> GetDesignReferencesAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.DesignAction, project.ProjectId, ct);
        return new DesignReferencesResponse(
            operation?.OperationId, operation?.CompletedAt, ExtractJsonArray(operation?.ResultJson, "references"));
    }

    // ------------------------------------------------------------------
    // Proposals.
    // ------------------------------------------------------------------

    public async Task<int> DeleteProposalsAsync(string projectIdentity, long? keepGeneration, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        return keepGeneration is null
            ? await ExecuteAsync(
                connection, "DELETE FROM studio_proposals WHERE project_id = $project;", ct, ("$project", project.ProjectId))
            : await ExecuteAsync(
                connection,
                "DELETE FROM studio_proposals WHERE project_id = $project AND generation <> $keep;",
                ct, ("$project", project.ProjectId), ("$keep", keepGeneration.Value));
    }

    public async Task DeleteProposalAsync(string projectIdentity, string proposalId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var deleted = await ExecuteAsync(
            connection, "DELETE FROM studio_proposals WHERE id = $id AND project_id = $project;",
            ct, ("$id", proposalId), ("$project", project.ProjectId));
        if (deleted == 0)
            throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");
    }

    public async Task<StudioProposalDto> DecideProposalAsync(
        string projectIdentity, string proposalId, DecideProposalRequest request, CancellationToken ct)
    {
        RequireWritable();
        if (request.Decision is not (StudioProposalStatuses.Accepted or StudioProposalStatuses.Rejected))
            throw new ArgumentException($"Unsupported proposal decision '{request.Decision}'.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        var updated = await ExecuteAsync(connection, """
            UPDATE studio_proposals SET status = $status, decided_at = $now WHERE id = $id AND project_id = $project;
            """, ct,
            ("$status", request.Decision), ("$now", now), ("$id", proposalId), ("$project", project.ProjectId));
        if (updated == 0)
            throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");
        return (await GetStudioProposalAsync(project.ProjectId, proposalId, ct))!;
    }

    public async Task<StudioOperationAcceptedResponse> GenerateProposalsAsync(string projectIdentity, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await CreateStudioOperationAsync(
            StudioOperationKinds.ProposalsGenerate,
            project.ProjectId,
            taskId: null,
            request: new ProposalsDispatchRequest(project.ProjectId),
            trigger: "generate",
            severity: null,
            topic: null,
            ct: ct);
        return new StudioOperationAcceptedResponse(operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
    }

    public async Task<StudioOperationAcceptedResponse> RefineProposalsFeedbackAsync(
        string projectIdentity, RefineProposalsFeedbackRequest request, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Feedback))
            throw new ArgumentException("Feedback is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await CreateStudioOperationAsync(
            StudioOperationKinds.ProposalsRefineFeedback,
            project.ProjectId,
            taskId: null,
            request: request,
            trigger: "refine-feedback",
            severity: null,
            topic: null,
            ct: ct);
        return new StudioOperationAcceptedResponse(operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
    }

    /// <summary>
    /// Materializes the <c>proposals</c> array from a succeeded
    /// <see cref="StudioOperationKinds.ProposalsGenerate"/> or
    /// <see cref="StudioOperationKinds.ProposalsRefineFeedback"/> operation
    /// into <c>studio_proposals</c> rows. Called by
    /// <c>ProposalsCompletionProjector.OnCompletedAsync</c>. A generate
    /// operation always starts a new generation; a refine-feedback operation
    /// appends to the latest existing generation (or starts generation 1 if
    /// none exists yet). Defensive: does nothing if the operation has no
    /// project, no result, or a result that does not parse to the expected
    /// <c>{ "proposals": [ ... ] }</c> shape.
    /// </summary>
    public async Task ApplyGeneratedProposalsAsync(StudioOperationDto operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation.ProjectId) || string.IsNullOrWhiteSpace(operation.ResultJson))
            return;
        List<JsonElement> proposals;
        try
        {
            using var document = JsonDocument.Parse(operation.ResultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("proposals", out var array)
                || array.ValueKind != JsonValueKind.Array)
                return;
            proposals = array.EnumerateArray().Select(item => item.Clone()).ToList();
        }
        catch (JsonException)
        {
            return;
        }
        if (proposals.Count == 0) return;

        var projectId = operation.ProjectId;
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var currentMax = Convert.ToInt64(
                await ScalarAsync(
                    connection, "SELECT COALESCE(MAX(generation), 0) FROM studio_proposals WHERE project_id = $project;",
                    ct, transaction, ("$project", projectId)) ?? 0L,
                CultureInfo.InvariantCulture);
            var generation = string.Equals(operation.Kind, StudioOperationKinds.ProposalsGenerate, StringComparison.Ordinal)
                || currentMax == 0
                ? currentMax + 1
                : currentMax;
            foreach (var proposal in proposals)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO studio_proposals(id, project_id, generation, status, payload_json, created_at)
                    VALUES ($id, $project, $generation, $status, $payload, $now);
                    """, ct, transaction,
                    ("$id", $"prop_{Guid.NewGuid():N}"), ("$project", projectId), ("$generation", generation),
                    ("$status", StudioProposalStatuses.Pending), ("$payload", JsonSerializer.Serialize(proposal)), ("$now", now));
            }
        }, ct);
    }

    public async Task<StudioProposalDto?> GetStudioProposalAsync(string projectId, string proposalId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, generation, status, payload_json, created_at, decided_at
              FROM studio_proposals WHERE id = $id AND project_id = $project;
            """, ("$id", proposalId), ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProposal(reader) : null;
    }

    /// <summary>
    /// Lists proposals for a project, newest generation first. Not mapped to
    /// an HTTP route in this bundle (the list route is a separate, lower
    /// priority ownership item) — used directly by callers that already hold
    /// a <see cref="TaskServerStore"/> reference, including tests.
    /// </summary>
    public async Task<StudioProposalListResponse> ListStudioProposalsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, generation, status, payload_json, created_at, decided_at
              FROM studio_proposals WHERE project_id = $project ORDER BY generation DESC, created_at, id;
            """, ("$project", project.ProjectId));
        var result = new List<StudioProposalDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadProposal(reader));
        return new StudioProposalListResponse(result);
    }

    private static StudioProposalDto ReadProposal(SqliteDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(4));
        return new StudioProposalDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            document.RootElement.Clone(),
            Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Parse(reader.GetString(6)));
    }

    // ------------------------------------------------------------------
    // Skill readiness (pure dispatch, no local projection).
    // ------------------------------------------------------------------

    public async Task<StudioOperationAcceptedResponse> FixSkillReadinessTaskAsync(
        string projectIdentity, FixSkillReadinessTaskRequest request, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.TaskId))
            throw new ArgumentException("A task id is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await CreateStudioOperationAsync(
            StudioOperationKinds.SkillReadinessFixTask,
            project.ProjectId,
            request.TaskId,
            request,
            trigger: "fix-task",
            severity: null,
            topic: null,
            ct: ct);
        return new StudioOperationAcceptedResponse(operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
    }

    // ------------------------------------------------------------------
    // Project snapshot.
    // ------------------------------------------------------------------

    public async Task<StudioProjectSnapshotResponse> GetProjectSnapshotAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        var counts = StudioTaskLanes.All.ToDictionary(lane => lane, _ => 0, StringComparer.Ordinal);
        await using var command = Command(connection, """
            SELECT state, COUNT(*) FROM tasks WHERE project_id = $project GROUP BY state;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var state = reader.GetString(0);
            if (counts.ContainsKey(state)) counts[state] = (int)reader.GetInt64(1);
        }
        var queue = new StudioProjectQueueSummaryDto(
            counts[StudioTaskLanes.Backlog],
            counts[StudioTaskLanes.Ready],
            counts[StudioTaskLanes.Progress],
            counts[StudioTaskLanes.AutoReview],
            counts[StudioTaskLanes.HumanReview],
            counts[StudioTaskLanes.Escalated],
            counts[StudioTaskLanes.Completed],
            counts[StudioTaskLanes.Archive],
            counts.Values.Sum());
        return new StudioProjectSnapshotResponse(project, queue, UtcNow);
    }

    // ------------------------------------------------------------------
    // Visual evidence.
    // ------------------------------------------------------------------

    public async Task<StudioVisualEvidenceListResponse> ListVisualEvidenceAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, captured_at, payload_json, acknowledged_at
              FROM studio_visual_evidence WHERE project_id = $project ORDER BY captured_at DESC, id DESC;
            """, ("$project", project.ProjectId));
        var result = new List<StudioVisualEvidenceItemDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadVisualEvidence(reader));
        return new StudioVisualEvidenceListResponse(result);
    }

    public async Task<StudioVisualEvidenceItemDto> AcknowledgeVisualEvidenceAsync(
        string projectIdentity, string itemId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        var updated = await ExecuteAsync(connection, """
            UPDATE studio_visual_evidence SET acknowledged_at = $now WHERE id = $id AND project_id = $project;
            """, ct, ("$now", now), ("$id", itemId), ("$project", project.ProjectId));
        if (updated == 0)
            throw new KeyNotFoundException($"Visual evidence item '{itemId}' was not found.");
        return (await GetVisualEvidenceAsync(project.ProjectId, itemId, ct))!;
    }

    public async Task<StudioVisualEvidenceItemDto?> GetVisualEvidenceAsync(string projectId, string itemId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, captured_at, payload_json, acknowledged_at
              FROM studio_visual_evidence WHERE id = $id AND project_id = $project;
            """, ("$id", itemId), ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVisualEvidence(reader) : null;
    }

    /// <summary>
    /// Inserts a visual-evidence row directly. No route in this bundle
    /// creates evidence over HTTP yet (see the type-level remarks); this is
    /// the seam tests and future producers use until one does.
    /// </summary>
    public async Task<StudioVisualEvidenceItemDto> RecordVisualEvidenceAsync(
        string projectIdentity, JsonElement payload, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var id = $"sve_{Guid.NewGuid():N}";
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_visual_evidence(id, project_id, captured_at, payload_json, acknowledged_at)
            VALUES ($id, $project, $now, $payload, NULL);
            """, ct,
            ("$id", id), ("$project", project.ProjectId), ("$now", now), ("$payload", JsonSerializer.Serialize(payload)));
        return (await GetVisualEvidenceAsync(project.ProjectId, id, ct))!;
    }

    private static StudioVisualEvidenceItemDto ReadVisualEvidence(SqliteDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(3));
        return new StudioVisualEvidenceItemDto(
            reader.GetString(0),
            reader.GetString(1),
            Parse(reader.GetString(2)),
            document.RootElement.Clone(),
            reader.IsDBNull(4) ? null : Parse(reader.GetString(4)));
    }

    // ------------------------------------------------------------------
    // Shared opaque-JSON helpers.
    // ------------------------------------------------------------------

    private static IReadOnlyList<JsonElement> ExtractJsonArray(string? resultJson, string property)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!document.RootElement.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
                return [];
            return value.EnumerateArray().Select(item => item.Clone()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonElement? ExtractJsonProperty(string? resultJson, string property)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty(property, out var value) ? value.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
