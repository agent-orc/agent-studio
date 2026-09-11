using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Small, mostly stand-alone P2 routes that do not share a table with any
/// other domain: component-routing resolution (a pure match against the
/// durable ownership mappings), the prompt/title text helpers, skill
/// readiness, and wiki grading, all of which dispatch through the shared
/// fenced-operation ledger.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<ComponentRoutingResolveResponse> ResolveComponentRoutingAsync(ComponentRoutingResolveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) throw new ArgumentException("A path is required.");
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, "SELECT pattern, owner FROM studio_ownership_mappings;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        string? bestPattern = null;
        string? bestOwner = null;
        while (await reader.ReadAsync(ct))
        {
            var pattern = reader.GetString(0);
            var owner = reader.GetString(1);
            if (!MatchesOwnershipPattern(request.Path, pattern)) continue;
            if (bestPattern is null || pattern.Length > bestPattern.Length)
            {
                bestPattern = pattern;
                bestOwner = owner;
            }
        }
        return new ComponentRoutingResolveResponse(request.Path, bestOwner, bestPattern);
    }

    private static bool MatchesOwnershipPattern(string path, string pattern)
    {
        if (pattern.EndsWith('*'))
            return path.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return string.Equals(path, pattern, StringComparison.Ordinal);
    }

    public Task<PromptEnhanceResponse> EnhancePromptAsync(PromptEnhanceRequest request, string actorId, CancellationToken ct)
        => DispatchTextHelperAsync(
            "prompt", request.Text, actorId, ct,
            operation => new PromptEnhanceResponse(request.Text, operation.Id));

    public Task<TitleGenerateResponse> GenerateTitleAsync(TitleGenerateRequest request, string actorId, CancellationToken ct)
        => DispatchTextHelperAsync(
            "title", request.Text, actorId, ct,
            operation => new TitleGenerateResponse(request.Text.Length <= 72 ? request.Text : request.Text[..72]));

    /// <summary>
    /// Both prompt/enhance and title/generate are instance-wide, single-turn
    /// text transforms; they still go through the fenced dispatch ledger
    /// (recorded as an instance-wide operation) because generating text with
    /// a CLI-backed model is the same "needs a Runner" case as any other
    /// generation route in this bundle - only the response shape differs.
    /// </summary>
    private async Task<TResponse> DispatchTextHelperAsync<TResponse>(
        string kind, string text, string actorId, CancellationToken ct, Func<StudioOperationDto, TResponse> project)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required.");
        var operation = await RecordCompletedStudioOperationAsync(
            null, "text-helpers", kind, $"Text helper: {kind}", new { text }, new { text }, actorId, ct);
        return project(operation);
    }

    public Task<StudioOperationDto> DispatchSkillReadinessFixAsync(
        string projectIdentity, SkillReadinessFixTaskRequest request, string actorId, CancellationToken ct)
    {
        var prompt = request.Prompt ?? $"Fix task '{request.TaskKey}' so it is ready for automated execution.";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.SkillReadiness, "fix-task",
            $"Skill readiness fix: {request.TaskKey}", prompt, request, actorId, ct);
    }

    public Task<StudioOperationDto> RunWikiGradingAsync(
        string projectIdentity, WikiGradingRunRequest request, string actorId, CancellationToken ct)
    {
        var prompt = string.IsNullOrWhiteSpace(request.Scope)
            ? "Grade the project wiki for accuracy and completeness."
            : $"Grade the project wiki, scoped to: {request.Scope}";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.WikiGrading, "run", "Wiki grading run", prompt, request, actorId, ct);
    }

    public async Task<StudioOperationDto> AbortWikiGradingAsync(
        string projectIdentity, WikiGradingAbortRequest request, string actorId, CancellationToken ct)
    {
        var operation = await GetStudioOperationAsync(projectIdentity, StudioOperationDomains.WikiGrading, request.OperationId, ct);
        if (operation.Status == StudioOperationStatuses.Dispatched && operation.TaskId is not null)
        {
            try
            {
                await StopTaskAsync(operation.ProjectId!, operation.TaskId, new StopTaskRequest("wiki-grading-abort"), actorId, ct);
            }
            catch (KeyNotFoundException)
            {
                // No active run to stop; fall through to marking the operation aborted.
            }
        }
        return await DecideStudioOperationAsync(
            projectIdentity, StudioOperationDomains.WikiGrading, request.OperationId, StudioOperationStatuses.Aborted, actorId, ct);
    }
}
