using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// Review package routes (AGT-3400 S4). The runner routes serve the shadow
/// package plane where the monolith mounts the local v1 review plane; with a
/// standalone Task Server the runners claim there instead. The project routes
/// read packages, formation decisions and the shadow report in both profiles.
/// </summary>
public static class ReviewPackageEndpoints
{
    public static void MapReviewPackagePlaneEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapPost("/runners/{runnerId}/review-package-claims", (
            HttpContext context, string runnerId, ReviewPackageClaimRequest request,
            V1ReviewExecutorRegistry registry, ReviewPackageStore store) =>
        {
            if (!RunnerMatches(context, runnerId) || !string.Equals(runnerId, request.ExecutorId, StringComparison.Ordinal))
                return Results.Unauthorized();
            if (!registry.TryGetReviewExecutor(runnerId, request.InstanceId, out _))
                return Results.Conflict(new ApiError("review-executor-not-registered",
                    "Register the review executor before claiming review packages."));
            return Invoke(() => store.Claim(request));
        });
        api.MapPost("/review-packages/attempts/{attemptId}/lease/renew", (
            HttpContext context, string attemptId, ReviewPackageRenewRequest request, ReviewPackageStore store) =>
            RunnerMatches(context, request.ExecutorId)
                ? Invoke(() => store.Renew(attemptId, request))
                : Results.Unauthorized());
        api.MapPost("/review-packages/attempts/{attemptId}/report", (
            HttpContext context, string attemptId, ReviewPackageReportRequest request, ReviewPackageStore store,
            TaskScannerService scanner, ILoggerFactory loggers) =>
        {
            if (!RunnerMatches(context, request.ExecutorId)) return Results.Unauthorized();
            return Invoke(() =>
            {
                var (receipt, subject, report) = store.Report(attemptId, request);
                Project(subject, report, receipt, scanner, loggers.CreateLogger("ReviewPackageObservations"));
                return receipt;
            });
        });
    }

    public static void MapReviewPackageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/review-packages", async (
            string projectName, IReviewPackageAuthority packages, CancellationToken ct)
            => Results.Ok(await packages.ListAsync(projectName, ct).ConfigureAwait(false)));
        app.MapGet("/api/projects/{projectName}/review-packages/formation", (
            string projectName, ReviewPackageStore store)
            => Results.Ok(store.ReadFormationDecisions(projectName)));
        app.MapGet("/api/projects/{projectName}/review-packages/shadow-report", async (
            string projectName, int? days, ReviewPackageShadowReportService reports, CancellationToken ct) =>
        {
            var window = days ?? ReviewPackageShadowReportService.DefaultDays;
            if (window is < 1 or > 90) return Results.BadRequest(new { error = "days must be 1 to 90." });
            return Results.Ok(await reports.BuildAsync(projectName, window, ct).ConfigureAwait(false));
        });
    }

    /// <summary>
    /// Writes the member observation rows. A member whose task folder is not
    /// found keeps its rows in the package report only.
    /// </summary>
    private static void Project(ReviewPackageSubjectDto subject, ReviewPackageReportRequest report,
        ReviewPackageReportDto receipt, TaskScannerService scanner, ILogger logger)
    {
        var rows = ReviewPackageObservations.Rows(subject, report, receipt.AttemptId, receipt.ReceivedAt);
        foreach (var member in rows.GroupBy(row => row.Review.JobId, StringComparer.OrdinalIgnoreCase))
        {
            var key = subject.Members.First(item => (item.TaskId ?? item.TaskKey) == member.Key).TaskKey;
            if ((scanner.FindJob(member.Key) ?? scanner.FindJob(key)) is not { } task)
            {
                logger.LogWarning("review-package-member-folder-missing package={Package} member={Member}",
                    subject.PackageId, key);
                continue;
            }
            ReviewPackageObservations.Append(task.FolderPath, member);
        }
    }

    private static IResult Invoke<T>(Func<T> action)
    {
        try
        {
            return Results.Ok(action());
        }
        catch (ReviewPackageConflictException conflict)
        {
            return Results.Conflict(new ApiError(conflict.Code, conflict.Message));
        }
        catch (KeyNotFoundException missing)
        {
            return Results.NotFound(new ApiError("not-found", missing.Message));
        }
        catch (ArgumentException invalid)
        {
            return Results.BadRequest(new ApiError("invalid-request", invalid.Message));
        }
    }

    private static bool RunnerMatches(HttpContext context, string runnerId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
           || string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal);
}
