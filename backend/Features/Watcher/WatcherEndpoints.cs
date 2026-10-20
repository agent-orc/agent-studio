using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Security;
using AgentStudio.Registry;

namespace AgentStudio.Watcher;

/// <summary>Body for <c>POST /api/watcher/proposals/{id}/decision</c> (§10.4 review mode).</summary>
public sealed record WatcherProposalDecisionRequest(string Outcome, string? Reason, string? MergedIntoJobId);

/// <summary>
/// Review-mode API (§10.4): list cases/proposals for the Watcher inbox and
/// record the operator's decision. Proposals never enter Ready by
/// themselves - every lane change here is the direct result of an explicit
/// operator decision recorded in the same call.
/// </summary>
public static class WatcherEndpoints
{
    public static void MapWatcherEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/watcher");

        group.MapGet("/status", (
            IConfiguration configuration,
            WatcherHostedService sweep,
            WatcherContingentService contingent) =>
        {
            var workspaceRoot = configuration["TaskRepository"] ?? "";
            var options = WatcherOptions.FromConfiguration(configuration);
            var budgets = WatcherContingentBudgets.FromConfiguration(configuration);
            var snapshot = string.IsNullOrWhiteSpace(workspaceRoot)
                ? null
                : contingent.GetSnapshot(workspaceRoot, budgets, DateTime.UtcNow);
            return Results.Ok(new { options, lastRun = sweep.Current, contingent = snapshot });
        });

        group.MapGet("/cases", (IConfiguration configuration, WatcherCaseStore cases, string? state) =>
        {
            var workspaceRoot = configuration["TaskRepository"] ?? "";
            if (string.IsNullOrWhiteSpace(workspaceRoot)) return Results.Ok(new { cases = Array.Empty<WatcherCase>() });
            var all = cases.All(workspaceRoot)
                .Where(c => string.IsNullOrWhiteSpace(state) || string.Equals(c.State, state, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.LastSeenUtc)
                .ToList();
            return Results.Ok(new { cases = all });
        });

        group.MapGet("/stalls", (HttpContext context, IConfiguration configuration,
            WatcherCaseStore cases, ProjectRegistry projects) =>
        {
            var workspaceRoot = configuration["TaskRepository"] ?? "";
            var open = string.IsNullOrWhiteSpace(workspaceRoot)
                ? Array.Empty<WatcherCase>()
                : cases.All(workspaceRoot)
                    .Where(c => c.Subject is not null
                        && c.State is WatcherCaseStates.Open or WatcherCaseStates.DecisionRequired)
                    .Where(c => c.Project == QuotaProbeSignalProbe.WorkspaceProject
                        || context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
                        || ProjectAccessAuthorization.Allows(human.User, c.Project, projects))
                    .OrderByDescending(c => c.SinceUtc).ToArray();
            return Results.Ok(new { count = open.Length, cases = open });
        });

        // AGT-3207: AI Operator interventions on stall events, newest first.
        // The host view, the stall list and the card detail read the receipts here.
        group.MapGet("/interventions", (StallInterventionStore store, StallInterventionService service,
            string? subject, string? card, int? limit) =>
        {
            var records = store.All()
                .Where(item => string.IsNullOrWhiteSpace(subject) || item.Subject == subject
                    || item.Host == subject)
                .Where(item => string.IsNullOrWhiteSpace(card) || item.AffectedCards.Contains(card, StringComparer.OrdinalIgnoreCase)
                    || item.Members.Contains(card, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(item => item.DecidedAtUtc)
                .Take(Math.Clamp(limit ?? 50, 1, 200))
                .Select(item => new
                {
                    receipt = StallInterventionReceipts.View(item),
                    item.Kind,
                    item.Host,
                    item.Project,
                    item.CaseId,
                    item.DecidedBy,
                    item.DecidedAtUtc,
                    item.FollowUpDueUtc,
                    item.ResolvedAtUtc,
                    item.AffectedCards,
                    item.Members,
                    item.UndoneBy,
                    headline = StallInterventionReceipts.Headline(item),
                })
                .ToArray();
            return Results.Ok(new { count = records.Length, interventions = records, decisions = service.LatestDecisions() });
        });

        group.MapPost("/interventions/{id}/undo", (string id, HttpContext context, StallInterventionService service) =>
        {
            var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? human.User.Username : context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "operator";
            var result = service.Undo(id, actor);
            return result switch
            {
                { Undone: true } => Results.Ok(new { result.Message, receipt = StallInterventionReceipts.View(result.Record!) }),
                { Record: null } => Results.NotFound(new { error = result.Message }),
                _ => Results.Conflict(new { error = result.Message }),
            };
        });

        group.MapGet("/proposals", (IConfiguration configuration, WatcherProposalStore proposals, bool? pending) =>
        {
            var workspaceRoot = configuration["TaskRepository"] ?? "";
            if (string.IsNullOrWhiteSpace(workspaceRoot)) return Results.Ok(new { proposals = Array.Empty<WatcherProposal>() });
            var all = proposals.All(workspaceRoot)
                .Where(p => pending != true || p.Decision == null)
                .OrderByDescending(p => p.CreatedAtUtc)
                .ToList();
            return Results.Ok(new { proposals = all });
        });

        group.MapPost("/proposals/{id}/decision", async (
            string id,
            WatcherProposalDecisionRequest req,
            HttpContext context,
            IConfiguration configuration,
            WatcherProposalStore proposals,
            WatcherCaseStore cases,
            WatcherSuppressionStore suppressions,
            TaskMutationService mutations,
            TaskTransitionService transitions,
            TaskScannerService scanner) =>
        {
            var workspaceRoot = configuration["TaskRepository"] ?? "";
            if (string.IsNullOrWhiteSpace(workspaceRoot)) return Results.NotFound();

            if (!WatcherProposalOutcomes.All.Contains(req.Outcome, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unknown outcome '{req.Outcome}'." });

            var proposal = proposals.FindById(workspaceRoot, id);
            if (proposal == null) return Results.NotFound();
            if (proposal.Decision != null) return Results.Conflict(new { error = "This proposal already has a decision." });

            if (string.Equals(req.Outcome, WatcherProposalOutcomes.Rejected, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "A rejection needs a reason." });

            var decidedBy = context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "operator";
            var nowUtc = DateTime.UtcNow;
            var options = WatcherOptions.FromConfiguration(configuration);
            var jobId = proposal.JobId ?? proposal.CommentedJobId;
            var job = jobId == null ? null : scanner.FindJob(jobId);

            switch (req.Outcome.ToLowerInvariant())
            {
                case "approved":
                case "edited":
                    if (job == null) return Results.NotFound(new { error = $"Proposal card '{jobId}' no longer exists." });
                    if (!string.IsNullOrWhiteSpace(proposal.RecommendedModel) && proposal.RecommendedModel != "(unavailable)")
                        mutations.SetJobModel(job.Id, proposal.RecommendedModel, job.WatchPath);
                    await transitions.MoveAsync(job.Id, TaskStates.Ready, job.WatchPath, cause: "watcher-proposal-decided", reason: $"Watcher proposal {proposal.Id} {req.Outcome}.");
                    break;

                case "merged":
                    if (string.IsNullOrWhiteSpace(req.MergedIntoJobId))
                        return Results.BadRequest(new { error = "merged needs mergedIntoJobId." });
                    var target = scanner.FindJob(req.MergedIntoJobId);
                    if (target == null) return Results.BadRequest(new { error = $"Merge target '{req.MergedIntoJobId}' not found." });
                    if (job != null)
                        await transitions.MoveAsync(job.Id, TaskStates.Archive, job.WatchPath, cause: "watcher-proposal-decided", reason: $"Merged into {target.Id}.");
                    break;

                case "rejected":
                    if (job != null)
                        await transitions.MoveAsync(job.Id, TaskStates.Archive, job.WatchPath, cause: "watcher-proposal-decided", reason: req.Reason);
                    suppressions.Suppress(workspaceRoot, proposal.Fingerprint, req.Reason!, TimeSpan.FromDays(options.SuppressionDays), nowUtc);
                    break;
            }

            var decided = proposal with
            {
                Decision = new WatcherProposalDecisionRecord
                {
                    Outcome = req.Outcome.ToLowerInvariant(),
                    Reason = req.Reason,
                    MergedIntoJobId = req.MergedIntoJobId,
                    DecidedAtUtc = nowUtc,
                    DecidedBy = decidedBy,
                },
            };
            proposals.Save(workspaceRoot, decided);

            var watcherCase = cases.FindById(workspaceRoot, proposal.CaseId);
            if (watcherCase != null)
            {
                var newState = string.Equals(req.Outcome, WatcherProposalOutcomes.Rejected, StringComparison.OrdinalIgnoreCase)
                    ? WatcherCaseStates.Suppressed
                    : WatcherCaseStates.Resolved;
                cases.Save(workspaceRoot, watcherCase with { State = newState });
            }

            return Results.Ok(new { proposal = decided });
        });
    }
}
