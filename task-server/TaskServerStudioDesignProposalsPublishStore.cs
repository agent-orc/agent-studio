using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Design council, proposals, publish, and deployment projections and
/// dispatches. Like drift and analysis, every generated item is a row in
/// the shared <c>studio_operations</c> ledger; accept/decision routes are
/// pure status updates on that same row.
/// </summary>
public sealed partial class TaskServerStore
{
    // --- Design ------------------------------------------------------------------

    public Task<StudioOperationDto> DispatchDesignActionAsync(
        string projectIdentity, string action, DesignActionRequest request, string actorId, CancellationToken ct)
    {
        var prompt = request.Prompt ?? $"Perform the design action '{action}' for the current project state.";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Design, "action", $"Design action: {action}", prompt, request, actorId, ct);
    }

    public Task<StudioOperationListResponse> ListDesignCouncilAsync(string projectIdentity, CancellationToken ct) =>
        ListStudioOperationsAsync(projectIdentity, StudioOperationDomains.Design, "council", ct);

    public Task<StudioOperationDto> GetDesignCouncilItemAsync(string projectIdentity, string fileName, CancellationToken ct) =>
        GetStudioOperationAsync(projectIdentity, StudioOperationDomains.Design, fileName, ct);

    public Task<StudioOperationDto> AcceptDesignCouncilItemAsync(
        string projectIdentity, string fileName, string actorId, CancellationToken ct) =>
        DecideStudioOperationAsync(projectIdentity, StudioOperationDomains.Design, fileName, StudioOperationStatuses.Accepted, actorId, ct);

    public async Task<DesignOverviewResponse> GetDesignOverviewAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var council = (await ListDesignCouncilAsync(projectIdentity, ct)).Operations;
        var actions = (await ListStudioOperationsAsync(projectIdentity, StudioOperationDomains.Design, "action", ct)).Operations;
        var last = council.Concat(actions).Select(operation => operation.UpdatedAt).DefaultIfEmpty().Max();
        return new DesignOverviewResponse(project.ProjectId, council.Count, actions.Count, last == default ? null : last);
    }

    public Task<DesignReferencesResponse> GetDesignReferencesAsync(string projectIdentity, CancellationToken ct) =>
        // No writer route exists for references in this bundle; the durable list
        // starts empty rather than inventing content that was never authored.
        Task.FromResult(new DesignReferencesResponse([]));

    // --- Proposals -----------------------------------------------------------

    public Task<StudioOperationListResponse> ListProposalsAsync(string projectIdentity, CancellationToken ct) =>
        ListStudioOperationsAsync(projectIdentity, StudioOperationDomains.Proposals, null, ct);

    public Task<StudioOperationDto> GenerateProposalsAsync(
        string projectIdentity, GenerateProposalsRequest request, string actorId, CancellationToken ct)
    {
        var prompt = request.Prompt ?? "Generate improvement proposals for the current project state.";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Proposals, "generate", "Generated proposal", prompt, request, actorId, ct);
    }

    public Task<StudioOperationDto> RefineProposalsFeedbackAsync(
        string projectIdentity, RefineProposalsFeedbackRequest request, string actorId, CancellationToken ct)
    {
        var prompt = $"Refine the prior proposal using this feedback: {request.Feedback}";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Proposals, "refine-feedback", "Refined proposal", prompt, request, actorId, ct);
    }

    public Task<StudioOperationDto> DecideProposalAsync(
        string projectIdentity, string proposalId, ProposalDecisionRequest request, string actorId, CancellationToken ct) =>
        DecideStudioOperationAsync(projectIdentity, StudioOperationDomains.Proposals, proposalId, request.Decision, actorId, ct);

    public Task DeleteProposalAsync(string projectIdentity, string proposalId, CancellationToken ct) =>
        DeleteStudioOperationAsync(projectIdentity, StudioOperationDomains.Proposals, proposalId, ct);

    public Task DeleteAllProposalsAsync(string projectIdentity, CancellationToken ct) =>
        DeleteAllStudioOperationsAsync(projectIdentity, StudioOperationDomains.Proposals, ct);

    // --- Publish / deployment ------------------------------------------------

    public Task<StudioOperationDto> PublishPackageAsync(
        string projectIdentity, PublishPackageRequest request, string actorId, CancellationToken ct)
    {
        var prompt = $"Build and publish a package for the current project state. Notes: {request.Notes ?? "none"}";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Publish, "package", "Publish package", prompt, request, actorId, ct);
    }

    public Task<StudioOperationDto> PublishWebsiteAsync(
        string projectIdentity, PublishWebsiteRequest request, string actorId, CancellationToken ct)
    {
        var prompt = $"Build and publish the project website. Notes: {request.Notes ?? "none"}";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Publish, "website", "Publish website", prompt, request, actorId, ct);
    }

    public Task<StudioOperationDto> CompileDeploymentAsync(
        string projectIdentity, DeploymentCompileRequest request, string actorId, CancellationToken ct)
    {
        var prompt = $"Compile a deployment for target: {request.Target ?? "default"}";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Deployment, "compile", "Deployment compile", prompt, request, actorId, ct);
    }

    public async Task<DeploymentSummaryResponse> GetDeploymentSummaryAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var last = (await ListStudioOperationsAsync(projectIdentity, StudioOperationDomains.Deployment, "compile", ct)).Operations
            .OrderByDescending(operation => operation.UpdatedAt)
            .FirstOrDefault();
        return new DeploymentSummaryResponse(project.ProjectId, last?.Status, last?.UpdatedAt);
    }
}
