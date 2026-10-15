using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

public sealed record AttachExternalPublishedVerificationRequest(
    string CandidateSha, string VerificationHost, string SuiteSummary, string LogHash,
    string? IntegrationBranch = null);

public static class ExternalPublishedVerificationEndpoints
{
    public static void MapExternalPublishedVerificationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{project}/external-verifications", (
            HttpContext context, string project, AttachExternalPublishedVerificationRequest request,
            ProjectRegistry projects, ProjectSettingsService settings, GitService git) =>
        {
            var registered = projects.FindByIdOrDisplayName(project)
                ?? projects.FindByShortCode(project);
            if (registered is null) return Results.NotFound(new { error = "Project not found." });
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                && !ProjectAccessAuthorization.Allows(human.User, registered.Id, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var sha = request.CandidateSha?.Trim().ToLowerInvariant() ?? "";
            var host = request.VerificationHost?.Trim() ?? "";
            var summary = request.SuiteSummary?.Trim() ?? "";
            var logHash = request.LogHash?.Trim().ToLowerInvariant() ?? "";
            if (!ReviewSubjectStore.IsValidResultSha(sha)
                || host.Length is < 1 or > 256
                || summary.Length is < 8 or > 4000
                || logHash.Length != 64 || !logHash.All(Uri.IsHexDigit))
                return Results.BadRequest(new { error = "Provide a full candidate SHA, verification host, suite summary (8-4000 characters), and SHA-256 log hash." });
            var configuredBranch = TaskIntegrationBranch.Name(settings.Get(registered.DisplayName).IntegrationBranch);
            var branch = TaskIntegrationBranch.Name(request.IntegrationBranch ?? configuredBranch);
            if (!string.Equals(branch, configuredBranch, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Verification must name the project's published integration branch." });
            var root = git.ResolveRepoRootForWatchPath(registered.StorageLocation);
            if (root is null) return Results.Conflict(new { error = "Project repository is unavailable." });
            var containment = git.CheckPublishedDelivery(root, branch, sha);
            var published = git.GetBranchTip(root, $"refs/remotes/origin/{branch}");
            if (containment != PublishedDeliveryContainment.Contained
                || published is null || !git.IsAncestor(root, sha, published))
                return Results.Conflict(new { error = $"Candidate {sha} is not contained in published {branch} ({containment})." });
            var record = new ExternalPublishedVerification(sha, branch, host, summary, logHash, DateTime.UtcNow);
            var appended = ExternalPublishedVerificationStore.Append(registered.StorageLocation, record);
            return Results.Ok(new { appended, record });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
    }
}
