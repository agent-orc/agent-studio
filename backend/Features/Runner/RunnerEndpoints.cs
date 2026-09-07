using AgentStudio.Orchestrator;

namespace AgentStudio.Runner;

/// <summary>
/// Project-runner control surface under <c>/api/runner</c>: the
/// status snapshot the board polls plus the manual mode / start /
/// stop toggles. Per-job execution lives in
/// <see cref="AgentStudio.Tasks.TaskRunnerEndpoints"/> —
/// these routes operate at project granularity.
/// </summary>
public static class RunnerEndpoints
{
    public static void MapRunnerEndpoints(this WebApplication app)
    {
        var runnerGroup = app.MapGroup("/api/runner");

        runnerGroup.MapGet("/status", (HttpContext context, TaskRunnerService runner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Cli.LocalCliRepairService cliRepair) =>
        {
            var status = runner.GetStatus() with { CliRepairs = cliRepair.Current() };
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
            {
                status = status with
                {
                    Projects = status.Projects
                        .Where(pair => ProjectAccessAuthorization.Allows(human.User, pair.Key, projects))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                };
            }
            return Results.Ok(status);
        });

        // Global orchestrator session: the singleton that lives above the
        // per-project orchestrators. Surfaces the same shape as the per-
        // project session so the frontend can reuse the rendering. Lives
        // at /api/runner/global/orchestrator-session for symmetry with the
        // per-project route.
        runnerGroup.MapGet("/global/orchestrator-session",
            (GlobalOrchestratorSessionStore store) =>
            {
                var session = store.Read();
                return Results.Ok(new { project = "(global)", session });
            });

        // Workspace-wide activity feed. Keep the project on every row so the
        // client can group and navigate without issuing one request per watch
        // path. The result is capped after the merge: a noisy project cannot
        // force every other project out before timestamps are compared.
        runnerGroup.MapGet("/orchestrator-feed",
            (HttpContext context, TaskScannerService scanner, OrchestratorLog log,
                AgentStudio.Registry.ProjectRegistry projects,
                AgentStudio.Watcher.WatcherActivityProjector? watcherActivity) =>
            {
                var perProject = scanner.GetWatchPaths()
                    .Where(project => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
                                      || ProjectAccessAuthorization.Allows(human.User, project.Name, projects))
                    .SelectMany(project => log.Read(project.Path).Select(entry => new
                    {
                        project = (string?)project.Name,
                        watchPath = (string?)project.Path,
                        entry.Ts,
                        entry.Kind,
                        entry.Topic,
                        entry.Summary,
                        entry.Reasoning,
                        entry.JobId,
                        entry.ParticipantId,
                        entry.TokenUsage,
                        entry.UserOverride
                    }));

                // Workspace-wide Watcher findings (e.g. quota probe silence/drift)
                // are not attributable to one project; they merge with
                // project: null, matching the dossier's §4a workspace health
                // event convention. Visible to every authorized viewer since
                // they carry no per-project access boundary of their own.
                var workspacePath = watcherActivity?.WorkspaceWatchPath();
                var workspaceEntries = string.IsNullOrWhiteSpace(workspacePath)
                    ? []
                    : log.Read(workspacePath).Select(entry => new
                    {
                        project = (string?)null,
                        watchPath = (string?)null,
                        entry.Ts,
                        entry.Kind,
                        entry.Topic,
                        entry.Summary,
                        entry.Reasoning,
                        entry.JobId,
                        entry.ParticipantId,
                        entry.TokenUsage,
                        entry.UserOverride
                    });

                var entries = perProject.Concat(workspaceEntries)
                    .OrderByDescending(entry => entry.Ts)
                    .Take(500)
                    .ToList();

                return Results.Ok(new { entries });
            });

        runnerGroup.MapPut("/{projectName}/mode", (string projectName, SetRunnerModeRequest req, TaskRunnerService runner, TaskScannerService scanner) =>
        {
            var result = runner.RequestModeChange(projectName, req.Mode, req.Reason);
            if (result == null)
            {
                // The project can be registered (it has a WatchPaths entry and shows
                // up everywhere else in the UI) while still having no ProjectRunner,
                // because TaskRunnerService only creates one at startup for entries
                // whose RootPath is non-empty and exists on disk. Without this check
                // that case reports "Unknown project", which reads as "this project
                // doesn't exist" and sends the operator looking in the wrong place
                // (observed against the "Agent Studio" WatchPaths entry, which had
                // Path but no RootPath after a lost-and-partially-reconstructed
                // appsettings.Local.json - see its "//WatchPaths" comment).
                var watchEntry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (watchEntry != null && string.IsNullOrWhiteSpace(watchEntry.RootPath))
                {
                    return Results.Conflict(new
                    {
                        error = $"Project '{projectName}' has no RootPath configured, so its runner was never started.",
                        hint = "Set RootPath (and RepositoryPath) on this project's WatchPaths entry in appsettings.Local.json, then restart the backend."
                    });
                }
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            }
            if (result.Outcome == ModeChangeOutcome.Invalid)
                return Results.BadRequest(new
                {
                    error = $"Invalid mode '{req.Mode}'. Allowed: manual, auto-single, auto-continuous, paused.",
                    mode = result.CurrentMode
                });
            // Applied = mode is live now; Deferred = the requested mode is queued
            // behind the active job. Both surface the requested vs. current mode
            // so the frontend can render "MANUAL (after current)" pills without
            // probing the status endpoint a second time.
            return Results.Ok(new SetRunnerModeResponse(
                Applied: result.Outcome == ModeChangeOutcome.Applied,
                Mode: result.CurrentMode,
                PendingMode: result.PendingMode,
                WillApplyAfterJobId: result.WillApplyAfterJobId));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // Live, in-progress decision surface (ADR-0027): unresolved
        // [[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]] sentinels the named
        // project's running job has emitted. Distinct from
        // /api/projects/{name}/review-decisions-pending, which scans the
        // 4-auto-review lane post-run; this surface is the *during-run*
        // banner the project view uses to make decision moments stand out.
        runnerGroup.MapGet("/{projectName}/pending-decisions",
            (string projectName, TaskRunnerService runner) =>
            {
                var entries = runner.GetPendingDecisions(projectName);
                if (entries == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

                var items = entries
                    .Select(e => new RunnerPendingDecisionDto(
                        JobId: e.JobId,
                        Title: e.Title,
                        Kind: e.Decision.Kind switch
                        {
                            PendingDecisionKind.NeedsInput => "needs-input",
                            PendingDecisionKind.Blocked    => "blocked",
                            _                              => "unknown"
                        },
                        Reason: e.Decision.Reason,
                        DetectedAt: e.Decision.DetectedAt))
                    .ToList();

                return Results.Ok(new RunnerPendingDecisionsResponse(projectName, items));
            });

        runnerGroup.MapPost("/{projectName}/start", (string projectName, TaskRunnerService runner) =>
        {
            var success = runner.StartRunner(projectName);
            return success ? Results.Ok() : Results.NotFound();
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        runnerGroup.MapPost("/{projectName}/stop", (string projectName, TaskRunnerService runner) =>
        {
            var success = runner.StopRunner(projectName);
            return success ? Results.Ok() : Results.NotFound();
        });

        // Orchestrator log: chronological feed of decisions / actions /
        // observations / interventions for the named project. Read-only;
        // entries are appended by the runner today and (Phase D+) by a
        // dedicated orchestrator process. The frontend renders this as
        // the "Orchestrator" feed in the project detail view.
        runnerGroup.MapGet("/{projectName}/orchestrator-log",
            (string projectName, TaskScannerService scanner, OrchestratorLog log) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var entries = log.Read(entry.Path);
                return Results.Ok(new { project = projectName, entries });
            });

        // Long-lived orchestrator session for the project. Surfaces the
        // session id (so the user can `claude -r <id>` themselves to
        // inspect or talk directly to it), the boot prompt preview ("what
        // did you read on boot?"), the boot reply preview ("what did you
        // say back?"), and cumulative token totals across the session's
        // lifetime. Read-only.
        runnerGroup.MapGet("/{projectName}/orchestrator-session",
            (string projectName, TaskScannerService scanner, OrchestratorSessionStore sessions) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var session = sessions.Read(entry.Path);
                return Results.Ok(new { project = projectName, session });
            });

        // Token summary: per-project rollup of orchestrator-log token
        // amounts plus a *theoretical* API-cost estimate. The frontend
        // renders amounts prominently, the cost smaller and behind a
        // disclaimer (the user pays via CLI subscriptions, not API).
        runnerGroup.MapGet("/{projectName}/token-summary",
            (string projectName, TaskScannerService scanner, ITokenAggregator tokens) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                return Results.Ok(tokens.LifetimeSummary(projectName, entry.Path));
            });

        // Workspace-wide token aggregate: same shape as TokenSummary but
        // folded across every watched project. Used by the status-bar
        // usage modal so the user sees a single "tokens consumed across
        // the whole workspace" number on hover. Persisted to disk so
        // the modal renders last-known totals immediately on app start.
        runnerGroup.MapGet("/token-summary-aggregate",
            (HttpContext context, TaskScannerService scanner, ITokenAggregator tokens,
                AgentStudio.Registry.ProjectRegistry registry) =>
            {
                var projects = scanner.GetWatchPaths()
                    .Where(project => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
                                      || ProjectAccessAuthorization.Allows(human.User, project.Name, registry))
                    .Select(e => (e.Name, e.Path))
                    .ToList();
                var agg = tokens.WorkspaceAggregate(projects);
                return Results.Ok(agg);
            });

        // Cache-only aggregate: returns the on-disk snapshot without
        // touching the orchestrator logs. The status-bar modal calls
        // this on first paint so the cached value appears even before
        // the live aggregator finishes scanning the JSONL files.
        runnerGroup.MapGet("/token-summary-aggregate/cached",
            (HttpContext context, ITokenAggregator tokens) =>
            {
                if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    && human.User.Role != StudioRoles.Owner
                    && human.User.Projects.Count > 0)
                    return Results.NoContent();
                var snap = tokens.CachedWorkspaceAggregate();
                return snap == null ? Results.NoContent() : Results.Ok(snap);
            });

        // User override on an orchestrator decision (Phase F). Appends an
        // intervention entry to the feed and, when the named job is in a
        // continuable state, routes the new direction through the existing
        // Continue path so the override actually takes effect on the agent.
        runnerGroup.MapPost("/{projectName}/orchestrator-log/override",
            async (string projectName, OrchestratorOverrideRequest req, TaskScannerService scanner, OrchestratorLog log, TaskRunnerService runner, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req?.NewDirection))
                    return Results.BadRequest(new { error = "newDirection is required" });

                var watchEntry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (watchEntry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

                // Always record the intervention in the feed, regardless of
                // whether we can route the follow-up. The audit trail is
                // half the value.
                log.Append(watchEntry.Path, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Intervention,
                    Topic = "user-override",
                    JobId = string.IsNullOrWhiteSpace(req.JobId) ? null : req.JobId,
                    Summary = $"User overrode an orchestrator entry from {req.OriginalTs:u}.",
                    Reasoning = $"New direction: {Truncate(req.NewDirection, 600)}",
                    UserOverride = new OrchestratorIntervention
                    {
                        At = DateTime.UtcNow,
                        NewDirection = req.NewDirection
                    }
                });

                // If the override names a job, treat the new direction as a
                // Continue follow-up. Reuses the busy-project queue path:
                // when the project is currently busy with another job, the
                // intent is saved on the target and runs on next pickup.
                if (!string.IsNullOrWhiteSpace(req.JobId))
                {
                    try
                    {
                        await runner.ContinueJobAsync(req.JobId, req.NewDirection, watchEntry.Path, modelOverride: null, cliTypeOverride: null, thinkingLevelOverride: null, mode: "steer", ct);
                    }
                    catch (Exception ex)
                    {
                        return Results.Ok(new
                        {
                            applied = false,
                            error = ex.Message,
                            note = "Override recorded in the feed; Continue could not be routed."
                        });
                    }
                    return Results.Ok(new { applied = true });
                }

                return Results.Ok(new { applied = false, note = "Override recorded in the feed; no jobId was given to route to." });
            }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        // Orchestrator chat (Phase 3): the side-sheet conversation surface.
        // Different from the orchestrator log: the log records what the
        // runner / orchestrator did on its own; the chat is a real
        // bidirectional dialogue between the user and the global orchestrator
        // session, scoped to one project tab. The Task Server centrally owns
        // the managed context, transcript, receipts, summary, and lifecycle.
        runnerGroup.MapGet("/{projectName}/orchestrator-chat",
            async (string projectName, TaskScannerService scanner, OrchestratorChatService chatService, CancellationToken ct) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var turns = await chatService.ReadAsync(projectName, entry.Path, context: null, 1000, ct);
                var executionContext = chatService.ResolveExecutionContext(projectName, entry.Path);
                return Results.Ok(new { project = projectName, turns, executionContext });
            });

        runnerGroup.MapPost("/{projectName}/orchestrator-chat",
            async (string projectName, SendOrchestratorChatRequest req, HttpContext ctx, TaskScannerService scanner, OrchestratorChatService chatService, CancellationToken ct) =>
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Text))
                    return Results.BadRequest(new { error = "text is required" });

                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

                // Forward the registered X-Client-Id so the orchestrator's
                // per-turn USER PREFERENCES block resolves to the live
                // defaults of the user who is actually chatting.
                var clientId = ctx.Items["ClientId"] as string;
                try
                {
                    var reply = await chatService.SendAsync(projectName, entry.Path, req, clientId, ct);
                    var executionContext = chatService.ResolveExecutionContext(projectName, entry.Path);
                    return Results.Ok(new { project = projectName, reply, executionContext });
                }
                catch (OrchestratorContextEnvelopeException exception)
                {
                    return Results.BadRequest(new { code = exception.Code, error = exception.Message });
                }
            }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);

        // Per-context transcript history (MC-2, Concept §4). The side sheet's
        // context follows the operator's navigation — the board yields a
        // `project:<PROJ>` context, a task page a `task:<PROJ>/<KEY>` one — and
        // the contextKey mirrors OrchestratorContextKey. A task context reads
        // and writes its own thread so a pinned task and the board no longer
        // share one history; a project context resolves to the same canonical
        // per-project thread the bare-project route above serves, so existing
        // project chats are byte-for-byte unaffected. The literal-prefixed
        // routes are strictly more specific than `{projectName}`, so routing
        // prefers them without ambiguity (same pattern as the orchestrator
        // session-turn endpoints).
        static async Task<IResult> ReadContextChat(
            string rawContextKey,
            TaskScannerService scanner,
            OrchestratorChatService chatService,
            CancellationToken ct)
        {
            if (!OrchestratorContextKey.TryParse(rawContextKey, out var key))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == key.ProjectId);
            if (entry == null) return Results.NotFound(new { error = $"Unknown project '{key.ProjectId}'" });
            var turns = await chatService.ReadAsync(key.ProjectId!, entry.Path, key, 1000, ct);
            var executionContext = chatService.ResolveExecutionContext(key.ProjectId!, entry.Path);
            return Results.Ok(new { contextKey = key.Value, project = key.ProjectId, turns, executionContext });
        }

        static async Task<IResult> SendContextChat(
            string rawContextKey, SendOrchestratorChatRequest req, HttpContext ctx,
            TaskScannerService scanner, OrchestratorChatService chatService, CancellationToken ct)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });
            if (!OrchestratorContextKey.TryParse(rawContextKey, out var key))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == key.ProjectId);
            if (entry == null) return Results.NotFound(new { error = $"Unknown project '{key.ProjectId}'" });

            // Forward the registered X-Client-Id so the orchestrator's per-turn
            // USER PREFERENCES block resolves to the live defaults of the user
            // who is actually chatting (matches the per-project route above).
            var clientId = ctx.Items["ClientId"] as string;
            try
            {
                var reply = await chatService.SendAsync(key.ProjectId!, entry.Path, req, clientId, key, ct);
                var executionContext = chatService.ResolveExecutionContext(key.ProjectId!, entry.Path);
                return Results.Ok(new { contextKey = key.Value, project = key.ProjectId, reply, executionContext });
            }
            catch (OrchestratorContextEnvelopeException exception)
            {
                return Results.BadRequest(new { code = exception.Code, error = exception.Message });
            }
        }

        runnerGroup.MapGet("/project:{projectId}/orchestrator-chat",
            (string projectId, TaskScannerService scanner, OrchestratorChatService chatService, CancellationToken ct) =>
                ReadContextChat($"project:{projectId}", scanner, chatService, ct));
        runnerGroup.MapGet("/task:{projectId}/{taskKey}/orchestrator-chat",
            (string projectId, string taskKey, TaskScannerService scanner, OrchestratorChatService chatService, CancellationToken ct) =>
                ReadContextChat($"task:{projectId}/{taskKey}", scanner, chatService, ct));

        runnerGroup.MapPost("/project:{projectId}/orchestrator-chat",
            (string projectId, SendOrchestratorChatRequest req, HttpContext ctx, TaskScannerService scanner, OrchestratorChatService chatService, CancellationToken ct) =>
                SendContextChat($"project:{projectId}", req, ctx, scanner, chatService, ct))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);
        runnerGroup.MapPost("/task:{projectId}/{taskKey}/orchestrator-chat",
            (string projectId, string taskKey, SendOrchestratorChatRequest req, HttpContext ctx, TaskScannerService scanner, OrchestratorChatService chatService, CancellationToken ct) =>
                SendContextChat($"task:{projectId}/{taskKey}", req, ctx, scanner, chatService, ct))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);

        // Image upload + serving for the orchestrator chat composer.
        // Files land under <watchPath>/.orchestrator/chat-attachments/.
        // The frontend uploads each pasted/dropped image first, then sends
        // the chat message with the relative paths so the orchestrator
        // sees them as proper file references rather than placeholders.
        runnerGroup.MapPost("/{projectName}/orchestrator-chat/attachments",
            async (string projectName, HttpRequest request, TaskScannerService scanner, OrchestratorChat chat) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest(new { error = "multipart/form-data expected" });

                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

                var form = await request.ReadFormAsync();
                var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded" });

                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);

                var (fileName, relativePath, error) = chat.SaveAttachment(entry.Path, ms.ToArray(), file.FileName, file.ContentType);
                if (fileName is null) return Results.BadRequest(new { error });

                return Results.Ok(new
                {
                    fileName,
                    relativePath,
                    url = $"/api/runner/{Uri.EscapeDataString(projectName)}/orchestrator-chat/attachments/{fileName}"
                });
            }).DisableAntiforgery();

        runnerGroup.MapGet("/{projectName}/orchestrator-chat/attachments/{fileName}",
            (string projectName, string fileName, TaskScannerService scanner, OrchestratorChat chat) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound();
                var (path, contentType) = chat.ResolveAttachment(entry.Path, fileName);
                return path is null ? Results.NotFound() : Results.File(path, contentType);
            });

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s[..(max - 1)].TrimEnd() + "...";
        }
    }
}

/// <summary>
/// One unresolved interruptive decision sentinel the project's running
/// job has emitted. Shape of an entry under
/// <c>GET /api/runner/{project}/pending-decisions</c>.
/// </summary>
public sealed record RunnerPendingDecisionDto(
    string JobId,
    string Title,
    string Kind,
    string? Reason,
    DateTime DetectedAt);

/// <summary>
/// Response body for the runner's continuous-decision surface
/// (<c>GET /api/runner/{project}/pending-decisions</c>). <c>Items</c> is
/// empty when nothing is pending.
/// </summary>
public sealed record RunnerPendingDecisionsResponse(
    string Project,
    IReadOnlyList<RunnerPendingDecisionDto> Items);

/// <summary>
/// Response body for <c>PUT /api/runner/{project}/mode</c> (ADR-0044).
/// <para>
/// <see cref="Applied"/> is <c>true</c> when the requested mode is live now;
/// <c>false</c> when the change is deferred behind an active job (in that
/// case the live mode stays at its previous <c>auto-*</c> value and
/// <see cref="PendingMode"/> + <see cref="WillApplyAfterJobId"/> describe
/// the deferred change). The frontend renders the pill as "<see cref="Mode"/>
/// (then <see cref="PendingMode"/> after <see cref="WillApplyAfterJobId"/>)"
/// while the queued change is pending.
/// </para>
/// </summary>
public sealed record SetRunnerModeResponse(
    bool Applied,
    string Mode,
    string? PendingMode,
    string? WillApplyAfterJobId);
