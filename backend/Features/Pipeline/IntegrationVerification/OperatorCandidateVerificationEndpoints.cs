using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

public sealed record OperatorCandidateSuite(string Command, int Passed, int Failed, int Skipped);

public sealed record RecordOperatorCandidateRequest(
    string CandidateSha,
    IReadOnlyList<string> Cards,
    string VerificationHost,
    IReadOnlyList<OperatorCandidateSuite> Suites,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string LogHash);

public sealed record OperatorCandidateCardResult(string Card, bool Recorded, string Reason);

/// <summary>Append-only proof that an operator tested one published tree for named deliveries.</summary>
public static class OperatorCandidateVerificationEndpoints
{
    public static void MapOperatorCandidateVerificationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{project}/operator-candidate-verifications", (
            HttpContext context, string project, RecordOperatorCandidateRequest request,
            ProjectRegistry projects, ProjectSettingsService settings, TaskScannerService scanner,
            TaskMutationService mutations, GitService git, OperatorCandidateAuditRepair audit,
            LaneMutexRegistry lanes, TimeProvider clock) =>
        {
            // The local profile has no human identity. A run or coding-agent token
            // cannot create verification evidence, even if it can call other APIs.
            if (!OperatorCandidateVerificationPolicy.HasOperatorAuthority(context))
                return Results.Json(new { error = "An authenticated Owner or Operator is required." },
                    statusCode: StatusCodes.Status403Forbidden);
            var principal = (HumanPrincipal)context.Items[AccessSecurityMiddleware.HumanPrincipalItem]!;

            var registered = projects.FindByIdOrDisplayName(project) ?? projects.FindByShortCode(project);
            if (registered is null) return Results.NotFound(new { error = "Project not found." });
            if (!ProjectAccessAuthorization.Allows(principal.User, registered.Id, projects))
                return Results.Json(new { error = "The operator cannot access this project." },
                    statusCode: StatusCodes.Status403Forbidden);
            // A project-scoped API token may operate its project, but a candidate
            // proof is a workspace operation. The all-projects grant is its scope.
            if (!OperatorCandidateVerificationPolicy.HasWorkspaceScope(context))
                return Results.Json(new { error = "The token requires workspace.operate scope." },
                    statusCode: StatusCodes.Status403Forbidden);

            var validation = OperatorCandidateVerificationPolicy.Validate(request, clock.GetUtcNow());
            if (validation is not null) return Results.BadRequest(new { error = validation });
            var sha = request.CandidateSha.Trim().ToLowerInvariant();
            var branch = TaskIntegrationBranch.Name(settings.Get(registered.DisplayName).IntegrationBranch);
            var root = git.ResolveRepoRootForWatchPath(registered.StorageLocation);
            if (root is null) return Results.Conflict(new { error = "Project repository is unavailable." });
            var containment = git.CheckPublishedDelivery(root, branch, sha, context.RequestAborted);
            if (containment == PublishedDeliveryContainment.Unavailable)
                return Results.Conflict(new { error = $"Published {branch} could not be refreshed from origin." });
            var published = git.GetBranchTip(root, $"refs/remotes/origin/{branch}");
            if (!ReviewSubjectStore.IsValidResultSha(published) || !git.IsAncestor(root, sha, published!))
                return Results.Conflict(new { error = $"Candidate {sha} is not on published {branch}." });

            var summary = string.Join("; ", request.Suites.Select(suite =>
                $"{suite.Command}: {suite.Passed} passed, {suite.Failed} failed, {suite.Skipped} skipped"));
            var now = clock.GetUtcNow().UtcDateTime;
            var results = new List<OperatorCandidateCardResult>();
            foreach (var address in request.Cards.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Legacy lane folders move under this project mutex. Keep the
                // record and its audit event in the same folder across a move.
                using var lane = lanes.Acquire(registered.StorageLocation, Timeout.InfiniteTimeSpan);
                scanner.InvalidateCache();
                var resolution = TaskIntegrationRecordAddressResolver.Resolve(scanner, address, registered.StorageLocation);
                if (resolution.Ambiguous)
                {
                    results.Add(new(address, false, "Card address is ambiguous."));
                    continue;
                }
                var card = resolution.Task;
                if (card is null)
                {
                    results.Add(new(address, false, "Card was not found in the project."));
                    continue;
                }
                var subject = ReviewSubjectStore.Read(card.FolderPath);
                var delivery = subject?.ResultSha;
                if (!ReviewSubjectStore.IsValidResultSha(delivery))
                {
                    results.Add(new(address, false, "Card has no full reviewed delivery SHA."));
                    continue;
                }
                if (!git.IsDeliveryInPublished(root, delivery!, sha))
                {
                    results.Add(new(address, false, $"Delivery {delivery} is not contained in candidate {sha} by ancestry or patch equivalence."));
                    continue;
                }

                var record = new TaskIntegrationRecord
                {
                    Id = $"operator-candidate-{sha[..12]}-{delivery![..12]}",
                    Classification = IntegrationRecordClasses.IntegratedVerified,
                    RecordedAtUtc = now,
                    IntegrationBranch = branch,
                    IntegrationSha = sha,
                    SourceSha = delivery,
                    DeliveryEpoch = subject!.RunAttemptId,
                    CommitShas = card.Commits.Where(commit => ReviewSubjectStore.IsValidResultSha(commit.Sha)
                            && git.IsAncestor(root, commit.Sha, delivery!))
                        .Select(commit => commit.Sha).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Actor = TimelineActors.Human(principal.User.Id),
                    VerificationHost = request.VerificationHost.Trim(),
                    SuiteSummary = summary,
                    VerificationSuites = request.Suites.Select(suite =>
                        new TaskIntegrationSuite(suite.Command.Trim(), suite.Passed, suite.Failed, suite.Skipped)).ToList(),
                    LogHash = request.LogHash.Trim().ToLowerInvariant(),
                    VerificationStartedAtUtc = request.StartedAtUtc.UtcDateTime,
                    VerificationEndedAtUtc = request.EndedAtUtc.UtcDateTime,
                    Evidence = $"Verified by operator candidate {sha}: {summary}. Log SHA-256 {request.LogHash.Trim().ToLowerInvariant()}.",
                };
                IntegrationRecordWriteResult write;
                lock (TaskJsonFile.SyncRoot(card.FolderPath))
                {
                    write = mutations.AppendIntegrationRecordOnFolder(card.FolderPath, record);
                    if (write.Succeeded && !write.Conflict)
                    {
                        if (!audit.EnsureAudit(card.FolderPath, write.PersistedRecord ?? record))
                        {
                            results.Add(new(address, false, "The record was persisted but its audit event failed; retry this post."));
                            continue;
                        }
                    }
                }
                if (write.Conflict)
                {
                    results.Add(new(address, false, "An immutable record with this candidate and delivery already has different evidence."));
                    continue;
                }
                if (!write.Succeeded)
                {
                    results.Add(new(address, false, "The integration record could not be persisted."));
                    continue;
                }
                results.Add(new(address, true, write.Appended ? "Recorded." : "Already recorded."));
            }
            return Results.Ok(new { candidateSha = sha, integrationBranch = branch, results });
        }).RequireStudioRole(StudioRoles.Operator)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
    }
}

public static class OperatorCandidateVerificationPolicy
{
    public static string? Validate(RecordOperatorCandidateRequest? request, DateTimeOffset now)
    {
        if (request is null) return "A verification summary is required.";
        if (!ReviewSubjectStore.IsValidResultSha(request.CandidateSha?.Trim()))
            return "A full candidate SHA is required.";
        if (request.Cards is not { Count: > 0 and <= 100 }
            || request.Cards.Any(card => string.IsNullOrWhiteSpace(card) || card.Length > 128))
            return "Provide 1-100 card addresses.";
        if (string.IsNullOrWhiteSpace(request.VerificationHost) || request.VerificationHost.Length > 256)
            return "A verification host of at most 256 characters is required.";
        if (request.Suites is not { Count: > 0 and <= 32 }
            || request.Suites.Any(suite => suite is null || string.IsNullOrWhiteSpace(suite.Command)
                || suite.Command.Length > 512 || suite.Passed < 0 || suite.Failed != 0
                || suite.Skipped < 0 || suite.Passed + suite.Skipped == 0))
            return "Provide 1-32 passing suites with commands and nonnegative test totals.";
        if (request.Suites.Sum(suite => suite.Command?.Length ?? 0) > 3000)
            return "Suite commands exceed the evidence limit.";
        if (request.StartedAtUtc == default || request.EndedAtUtc < request.StartedAtUtc
            || request.EndedAtUtc > now.AddMinutes(5))
            return "Verification start and end times are invalid.";
        var hash = request.LogHash?.Trim() ?? "";
        return hash.Length == 64 && hash.All(Uri.IsHexDigit)
            ? null : "A SHA-256 verification log digest is required.";
    }

    public static bool HasOperatorAuthority(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal
           && context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is null
           && context.Items[AccessSecurityMiddleware.RequestAccessItem] is not RequestAccess
               { Subject.Kind: IamPrincipalKind.ServiceAccount }
           && ProjectAccessAuthorization.HasOperatorAuthority(context)
           && context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is null;

    public static bool HasWorkspaceScope(HttpContext context)
        => context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access
           && access.Allows(IamPermissions.WorkspaceOperate, PermissionTarget.WorkspaceWide);
}
