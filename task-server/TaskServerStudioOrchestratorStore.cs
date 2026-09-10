using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio-facing orchestrator context digest and session listing. Both are
/// built from the durable orchestrator context/turn tables that already
/// back <c>/api/v1/orchestrator-contexts/**</c> - there is no separate quota
/// probe subsystem in the standalone Task Server, so the digest summarizes
/// what is actually persisted rather than a live model-provider probe.
/// </summary>
public sealed partial class TaskServerStore
{
    private const string GlobalOrchestratorContextKey = "global";

    public async Task<ProjectDto> RequireProjectAsync(string identity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("A project identity is required.");
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, workspace_id, name, task_key_prefix, version, created_at, updated_at
              FROM projects
             WHERE id = $identity OR name = $identity COLLATE NOCASE
             ORDER BY CASE WHEN id = $identity THEN 0 ELSE 1 END
             LIMIT 1;
            """, ("$identity", identity.Trim()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new KeyNotFoundException($"Project '{identity}' was not found.");
        return new ProjectDto(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), Parse(reader.GetString(5)), Parse(reader.GetString(6)));
    }

    public async Task<OrchestratorChatTranscriptResponse> GetOrchestratorChatAsync(
        string projectIdentity, string actorId, CancellationToken ct)
    {
        var transcript = await ReadOrchestratorContextAsync(projectIdentity, null, 200, actorId, ct);
        return new OrchestratorChatTranscriptResponse(transcript.Context.ProjectName, transcript.Turns);
    }

    public async Task<OrchestratorChatResponse> SendOrchestratorChatMessageAsync(
        string projectIdentity, StudioOrchestratorChatMessageRequest request, string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("text is required");
        var turn = await AppendOrchestratorContextTurnAsync(
            projectIdentity,
            null,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                $"chat_{Guid.NewGuid():N}", UtcNow, "user", request.Text, request.Model,
                Attachments: request.Attachments)),
            actorId,
            ct);
        var transcript = await ReadOrchestratorContextAsync(projectIdentity, null, 200, actorId, ct);
        return new OrchestratorChatResponse(transcript.Context.ProjectName, turn, transcript.Turns);
    }

    public async Task<StudioOrchestratorContextDigestResponse> BuildStudioOrchestratorDigestAsync(
        string rawContextKey, string actorId, CancellationToken ct)
    {
        if (string.Equals(rawContextKey, GlobalOrchestratorContextKey, StringComparison.Ordinal))
            return await BuildGlobalOrchestratorDigestAsync(ct);

        var (projectIdentity, taskIdentity) = ParseStudioOrchestratorContextKey(rawContextKey);
        var transcript = await ReadOrchestratorContextAsync(projectIdentity, taskIdentity, 50, actorId, ct);
        var digest = BuildOrchestratorDigestMarkdown(transcript.Context, transcript.Turns);
        var sources = new List<OrchestratorContextDigestSourceDto>
        {
            new("turns", "ready", transcript.Context.UpdatedAt, $"{transcript.Context.TurnCount} turn(s) captured."),
        };
        return new StudioOrchestratorContextDigestResponse(transcript.Context.ContextKey, UtcNow, digest, sources);
    }

    public async Task<OrchestratorSessionListResponse> ListStudioOrchestratorSessionsAsync(CancellationToken ct)
    {
        var contexts = await ListOrchestratorContextsAsync(includeHidden: false, ct);
        var sessions = contexts
            .Select(context => new OrchestratorSessionDto(
                context.ContextKey,
                context.Kind,
                context.ProjectId,
                context.TaskKey,
                context.CreatedAt,
                context.UpdatedAt,
                context.Model,
                context.CumulativeInputTokens,
                context.CumulativeOutputTokens,
                context.CumulativeCacheReadTokens,
                context.CumulativeCacheCreationTokens,
                context.TurnCount,
                context.UpdatedAt,
                context.Summary,
                context.HiddenAt,
                "idle",
                0))
            .ToList();
        return new OrchestratorSessionListResponse(sessions);
    }

    private async Task<StudioOrchestratorContextDigestResponse> BuildGlobalOrchestratorDigestAsync(CancellationToken ct)
    {
        var contexts = await ListOrchestratorContextsAsync(includeHidden: false, ct);
        var digest = contexts.Count == 0
            ? "No orchestrator activity yet."
            : string.Join('\n', contexts
                .OrderByDescending(context => context.UpdatedAt)
                .Take(20)
                .Select(context =>
                    $"- {context.ContextKey}: {context.Summary} ({context.TurnCount} turn(s), updated {context.UpdatedAt:O})"));
        var sources = new List<OrchestratorContextDigestSourceDto>
        {
            new("contexts", "ready", UtcNow, $"{contexts.Count} active context(s)."),
        };
        return new StudioOrchestratorContextDigestResponse(GlobalOrchestratorContextKey, UtcNow, digest, sources);
    }

    private static (string ProjectIdentity, string? TaskIdentity) ParseStudioOrchestratorContextKey(string rawContextKey)
    {
        if (string.IsNullOrWhiteSpace(rawContextKey))
            throw new ArgumentException("Invalid orchestrator context key.");
        var decoded = Uri.UnescapeDataString(rawContextKey.Trim());
        if (decoded.StartsWith("project:", StringComparison.Ordinal))
        {
            var identity = decoded["project:".Length..];
            if (string.IsNullOrWhiteSpace(identity))
                throw new ArgumentException("Invalid orchestrator context key.");
            return (identity, null);
        }
        if (decoded.StartsWith("task:", StringComparison.Ordinal))
        {
            var rest = decoded["task:".Length..];
            var separator = rest.IndexOf('/');
            if (separator <= 0 || separator == rest.Length - 1)
                throw new ArgumentException("Invalid orchestrator context key.");
            return (rest[..separator], rest[(separator + 1)..]);
        }
        throw new ArgumentException("Invalid orchestrator context key.");
    }

    private static string BuildOrchestratorDigestMarkdown(
        OrchestratorContextDto context, IReadOnlyList<OrchestratorContextTurnDto> turns)
    {
        var title = context.TaskKey is null ? context.ProjectName : $"{context.ProjectName} / {context.TaskKey}";
        var header = $"# {title}\n\n{context.Summary}";
        if (turns.Count == 0) return header + "\n\nNo turns yet.";
        var recent = turns.TakeLast(10)
            .Select(turn => $"- **{turn.Role}** ({turn.CreatedAt:O}): {Truncate(turn.Body, 240)}");
        return header + "\n\n" + string.Join('\n', recent);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
