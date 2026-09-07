

namespace AgentStudio.Host;

using System.Reflection;
using AgentStudio.Registry;
using AgentStudio.Security;

/// <summary>
/// Cross-cutting routes that don't fit any of the resource-scoped
/// groups: workspace enumeration, environment flags consumed by the
/// frontend bootstrap, the agent-rules overlay descriptor, the global
/// git-summary cache for board pills, and the health probe.
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/watch-paths", (HttpContext context, TaskScannerService scanner, ProjectRegistry projects) =>
        {
            var entries = scanner.GetWatchPaths().AsEnumerable();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
                entries = entries.Where(entry => ProjectAccessAuthorization.Allows(human.User, entry.Name, projects)
                                                 || ProjectAccessAuthorization.Allows(human.User, entry.Path, projects));
            return Results.Ok(entries);
        });

        // Returns flags that vary by per-checkout local config (appsettings.Local.json).
        // The frontend reads this before bootstrap to decide whether to show the DEV
        // banner and swap the PWA icon / favicon to the dev variants. It is also the
        // first request a public-demo browser makes, so the edge allowlists it
        // unconditionally - see PublicDemoReadAllowlist.
        app.MapGet("/api/environment", (IConfiguration config) =>
        {
            var isDev = config.GetValue<bool>("Environment:IsDev");
            var devTools = new
            {
                updateStableEnabled = config.GetValue<bool>("DevTools:UpdateStableEnabled"),
                deleteE2EJobsEnabled = config.GetValue<bool>("DevTools:DeleteE2EJobsEnabled")
            };
            var publicDemo = new
            {
                active = SecurityProfiles.IsPublicDemo(config),
                profile = SecurityProfiles.ActiveProfile(config),
            };
            return Results.Ok(new { isDev, devTools, publicDemo });
        });

        // Runtime identity comes only from the immutable manifest copied beside
        // the running backend. Checkout state and folder timestamps are not
        // evidence of what this process loaded.
        // Capture once at process start. Re-reading a mutable file on every
        // request would report the checkout/cache identity rather than the
        // code this process actually loaded.
        var buildIdentity = BuildIdentity.Load(app.Configuration);
        BuildIdentity ReadBuildIdentity() => buildIdentity;
        object About() {
            var identity = ReadBuildIdentity();
            return new {
                identity.SchemaVersion, identity.Application, identity.Tag,
                identity.Version, identity.Commit, identity.Dirty,
                identity.BuiltAt, deployedAt = identity.BuiltAt,
                identity.Integrity, identity.CodingAgentRunner,
                identity.CodingAgentChat, identity.Legacy
            };
        }
        app.MapGet("/api/system/version", () => Results.Ok(About()));
        app.MapGet("/api/system/about", () => Results.Ok(About()));

        // Lists the centrally-managed agent-rule files that are appended as a
        // system-prompt overlay to every Claude job. Used by the Job Detail
        // header to show "Active rules" so the user can verify what's in scope.
        app.MapGet("/api/agent-rules", (IConfiguration config) =>
        {
            var configured = config["AgentRules:CorePath"];
            if (string.IsNullOrWhiteSpace(configured))
                return Results.Ok(Array.Empty<object>());

            var candidates = new List<string>();
            if (Path.IsPathRooted(configured))
            {
                candidates.Add(configured);
            }
            else
            {
                candidates.Add(Path.GetFullPath(configured));
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    candidates.Add(Path.Combine(dir.FullName, configured));
                    dir = dir.Parent;
                }
            }

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                var fi = new FileInfo(candidate);
                return Results.Ok(new[]
                {
                    new
                    {
                        name = Path.GetFileName(candidate),
                        path = candidate,
                        sizeBytes = fi.Length,
                        modifiedAt = fi.LastWriteTimeUtc
                    }
                });
            }
            return Results.Ok(Array.Empty<object>());
        });

        // Per-project git summary, used by board tile pills. Cached server-side
        // for ~3 s so the board can call freely without forking N git processes.
        app.MapGet("/api/git/summary", (HttpContext context, GitService git, ProjectRegistry projects) =>
        {
            var summaries = git.GetSummaries().AsEnumerable();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
                summaries = summaries.Where(summary => ProjectAccessAuthorization.Allows(human.User, summary.ProjectName, projects));
            return Results.Ok(summaries);
        });

        // Repository hygiene snapshot for the project header badge: dirty
        // working tree, ahead-of-upstream, last commit, etc. Cached for ~3 s
        // and deliberately separate from /api/git/summary because the
        // hygiene shape carries upstream + last-commit info the board pills
        // do not need.
        app.MapGet("/api/git/hygiene", (string project, GitService git) =>
            Results.Ok(git.GetProjectHygiene(project)));

        // Project Hub Git View: read-only branch / worktree / first graph-page
        // inventory for one project. Requests return the last ref-signature
        // keyed background snapshot and never start Git. Deliberately
        // project-scoped (never a global git client): it lists the project's
        // local branches, tags, protected origin lines, indexed delivery refs,
        // active checkouts, on-disk worktrees, and
        // enriched commits so the tree can distinguish integration / feature /
        // task / runner state and hand a browsed SHA to the shared diff renderer.
        app.MapGet("/api/git/inventory", (string project, ProjectGitGraphService graph) =>
            Results.Ok(graph.BuildInventory(project)));

        // Older graph rows are paged from the same bounded background snapshot.
        // Page size is clamped in GitService and every row is enriched through the same
        // cached develop/main presence resolver as the initial inventory page.
        app.MapGet("/api/git/history", (
            string project,
            int? offset,
            int? limit,
            ProjectGitGraphService graph) =>
            Results.Ok(graph.BuildHistory(project, offset ?? 0, limit ?? 50)));

        // First-class integration object: accepted-card queue, curated publisher
        // merges on origin/develop, and the card-granular develop -> main
        // promotion delta. This is deliberately separate from the transient
        // per-card mergeSignal projection.
        app.MapGet("/api/git/integration", (
            string project,
            HttpContext context,
            ProjectIntegrationViewService integration,
            ProjectRegistry projects) =>
        {
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                && !ProjectAccessAuthorization.Allows(human.User, project, projects))
                return Results.Forbid();
            return Results.Ok(integration.Build(project));
        });

        // Changed-file list for a commit browsed in the project Git View,
        // resolved through the project's configured repository (no job context).
        app.MapGet("/api/git/project-commit/files", (string project, string sha, GitService git) =>
            Results.Ok(new { sha, files = git.GetProjectCommitFiles(project, sha) }));

        // Unified diff for a commit browsed in the project Git View, optionally
        // scoped to one path. Returns the same { diff, hasDiff, emptyReason }
        // envelope the per-task commit-diff endpoints use so the frontend diff
        // surfaces share one payload contract.
        app.MapGet("/api/git/project-commit/diff", (string project, string sha, string? path, GitService git) =>
        {
            var result = git.GetProjectCommitDiffResult(project, sha, path);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error ?? "Could not load diff." });
            var hasDiff = !string.IsNullOrWhiteSpace(result.Diff);
            return Results.Ok(new
            {
                diff = result.Diff,
                hasDiff,
                emptyReason = hasDiff ? null : "No diff for this path in the selected commit."
            });
        });

        // Git-Management cleanup (AGT-2009). Dry-run analysis of which merged
        // task/* branches (local + remote), refs/backups/* refs and stale
        // worktree registrations can be pruned against the integration branch.
        // Read-only: nothing is deleted here.
        app.MapGet("/api/git/cleanup/plan", (string project, GitCleanupService cleanup) =>
            Results.Ok(cleanup.BuildPlan(project)));

        // Executes an operator-confirmed subset of the cleanup plan. The service
        // re-derives eligibility from a fresh plan and re-checks merge ancestry
        // immediately before each delete, so only GEMERGTES is ever removed
        // (AGT-1945). Returns the n-deleted / m-kept report.
        app.MapPost("/api/git/cleanup/execute", (string project, GitCleanupRequest req, GitCleanupService cleanup) =>
        {
            var result = cleanup.Execute(project, req ?? new GitCleanupRequest([]));
            return result.IsRepo
                ? Results.Ok(result)
                : Results.BadRequest(new { error = result.Error ?? "Could not run cleanup." });
        });

        app.MapGet("/healthz", (HttpContext context) =>
        {
            var identity = ReadBuildIdentity();
            context.Response.Headers["X-Agent-Studio-Tag"] = identity.Tag;
            context.Response.Headers["X-Agent-Studio-Commit"] = identity.Commit;
            return Results.Ok("ok");
        });

        // External stable-process watchdogs consult this immediately before a
        // hard restart. The merge gate is one consistency boundary that must
        // finish its build decision and possible rollback before process exit.
        // Plain text keeps the shell probe dependency-free.
        app.MapGet("/healthz/drain", (MergeIntoDevelopRunner merge) =>
            Results.Text(
                merge.IsMergeGateBusy ? "gate-busy" : "idle",
                "text/plain"));
    }
}
