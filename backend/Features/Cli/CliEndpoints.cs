

namespace AgentStudio.Cli;

/// <summary>
/// Cross-CLI observability surface: the multi-CLI introspection
/// routes under <c>/api/cli</c> (model catalogs, session
/// usage, quota windows, and the dev-only PTY probe).
/// </summary>
public static class CliEndpoints
{
    public static void MapCliEndpoints(this WebApplication app)
    {
        // ── Multi-CLI endpoints ────────────────────────────────────────

        var cliGroup = app.MapGroup("/api/cli");

        cliGroup.MapGet("/types", () => Results.Ok(CliTypes.All));

        // Per-CLI completion contract: how each backend signals turn
        // completion (native frame -> typed CliRunEvent). Static, derived
        // from the live adapter mappings; the Admin/CLI page renders it so
        // the contract shown is the real one, not a frontend guess.
        cliGroup.MapGet("/contracts", () => Results.Ok(CliCompletionContracts.All));

        // ── Working Memory (ASS-1748 / T1c): per-CLI persistent-state panel ──
        // GET lists the memory / session state a CLI keeps on disk (path, size,
        // last-used, preview) plus its protected auth / config entries; DELETE
        // removes a single memory / session state. The delete is guarded inside
        // CliWorkingMemoryService so this surface can never remove credentials -
        // auth / config entries are reported Deletable=false and refused.
        cliGroup.MapGet("/{cliType}/working-memory", (string cliType, CliWorkingMemoryService mem) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            return Results.Ok(mem.Describe(cliType));
        });

        cliGroup.MapDelete("/{cliType}/working-memory", (string cliType, string? path, CliWorkingMemoryService mem) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path query parameter is required" });

            var result = mem.Delete(cliType, path);
            return result.Status switch
            {
                CliWorkingMemoryDeleteStatus.Deleted => Results.Ok(result),
                CliWorkingMemoryDeleteStatus.NotFound => Results.Json(result, statusCode: StatusCodes.Status404NotFound),
                CliWorkingMemoryDeleteStatus.Protected => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        cliGroup.MapGet("/{cliType}/models", async (string cliType, bool? refresh, CliRouter router, CancellationToken ct) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            try
            {
                var catalog = await router.Get(cliType).GetModelCatalogAsync(refresh ?? false, ct);
                return Results.Ok(catalog);
            }
            catch (Exception ex)
            {
                // Last-resort guard: discovery (e.g. a CLI's PTY probe) can
                // fail when no cache exists. Return 503 with the reason so the
                // UI can surface "models temporarily unavailable" rather than
                // breaking the whole page on a 500.
                return Results.Json(
                    new { error = ex.Message, cliType },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        cliGroup.MapGet("/model-routing/policy", (
            ModelRoutingPolicyRegistry registry,
            ModelRoutingPolicyStateStore state) =>
        {
            return Results.Ok(new
            {
                version = registry.Policy.Version,
                wikiPath = registry.Policy.WikiPath,
                economyMode = state.EconomyMode,
                economyModeLabel = registry.Policy.EconomyMode.Label,
                tiers = registry.Policy.Tiers,
                taskTypeDefaults = registry.Policy.TaskTypeDefaults,
            });
        });

        // AGT-2716: the migration catalog (version + known-safe "from -> to"
        // model replacements). The card model badge, project pipeline settings,
        // and Workspace CLI Management all look a pinned model id up against
        // this list client-side to decide whether to show "update available" -
        // no per-card backend computation needed.
        cliGroup.MapGet("/model-migrations", (AgentStudio.Pipeline.ModelMigrationCatalogRegistry registry) =>
        {
            return Results.Ok(new
            {
                version = registry.Catalog.Version,
                wikiPath = registry.Catalog.WikiPath,
                migrations = registry.Catalog.Migrations,
            });
        });

        cliGroup.MapPut("/model-routing/economy-mode", (
            SetModelRoutingEconomyModeRequest request,
            ModelRoutingPolicyStateStore state) =>
            Results.Ok(state.SetEconomyMode(request.EconomyMode)));

        cliGroup.MapGet("/model-routing/recommendation", async (
            string taskType,
            string cliType,
            ModelRoutingPolicyRegistry registry,
            ModelRoutingPolicyStateStore state,
            CliRouter router,
            CancellationToken ct) =>
        {
            if (!TaskTypes.All.Contains(taskType, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"taskType must be one of {string.Join(", ", TaskTypes.All)}" });
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            try
            {
                var catalogue = await router.Get(cliType).GetModelCatalogAsync(false, ct);
                return Results.Ok(registry.Recommend(taskType, catalogue, state.EconomyMode));
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = ex.Message, cliType, taskType },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        cliGroup.MapGet("/usage", (CliRouter router, SessionRegistry sessions, TaskRunnerService runners) =>
        {
            // Snapshot the runner's per-project active job so the
            // LinkedJob chip can render `active` (green) when the linked
            // session belongs to the project's currently-running task.
            var status = runners.GetStatus();
            var activeJobByProject = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, projectStatus) in status.Projects)
            {
                activeJobByProject[name] = projectStatus.ActiveJobId;
            }
            return Results.Ok(sessions.BuildReport(router, activeJobByProject));
        });

        // Lazy deep-read of one session (row expand in the CLI-session tool).
        // Parses exactly one transcript on demand so the list report stays
        // body-free even with thousands of sessions.
        cliGroup.MapGet("/{cliType}/session-detail", (string cliType, string id, string? cwd, SessionRegistry sessions) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "id query parameter is required" });
            return Results.Ok(sessions.BuildSessionDetail(cliType, id, cwd));
        });

        // Guarded single-session cleanup. SessionRegistry confirms the resolved
        // path lives under the CLI's own session store before deleting; anything
        // outside is refused, so this can only remove a transcript.
        cliGroup.MapDelete("/{cliType}/session", (string cliType, string id, string? cwd, SessionRegistry sessions) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "id query parameter is required" });

            var result = sessions.DeleteSession(cliType, id, cwd);
            return result.Status switch
            {
                "Deleted" => Results.Ok(result),
                "NotFound" => Results.Json(result, statusCode: StatusCodes.Status404NotFound),
                _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        // ── Quota: per-CLI subscription quota for the right-hand sidesheet ──
        cliGroup.MapGet("/quota", (QuotaService quota, CancellationToken ct) =>
        {
            return Results.Ok(quota.GetWithBackgroundRefresh(ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        cliGroup.MapPost("/quota/refresh", async (QuotaService quota, CancellationToken ct) =>
        {
            return Results.Ok(await quota.RefreshAllAsync(ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        cliGroup.MapPost("/quota/refresh/{cliType}", async (string cliType, QuotaService quota, CancellationToken ct) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            var snap = await quota.RefreshAsync(cliType, ct);
            return snap == null ? Results.NotFound() : Results.Ok(snap);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        // ── Quota caps: per-CLI per-window usage ceilings ──
        // The user uses Claude Code (and others) outside the orchestrator too;
        // these caps stop the runner from burning the last few percent of a
        // 5-hour window or the weekly budget so manual ad-hoc work still has
        // headroom. Default 95% applied when no entry is configured.
        cliGroup.MapGet("/quota/caps", (CliQuotaCapsService caps) =>
        {
            return Results.Ok(new
            {
                defaultCapPct = CliQuotaCapsService.DefaultCapPct,
                caps = caps.GetAll()
            });
        });

        cliGroup.MapPut("/quota/caps", (SetCliQuotaCapRequest req, CliQuotaCapsService caps) =>
        {
            if (string.IsNullOrWhiteSpace(req.CliType) || string.IsNullOrWhiteSpace(req.WindowLabel))
                return Results.BadRequest(new { error = "cliType and windowLabel are required" });
            if (!CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{req.CliType}'" });
            if (req.CapPct < 1 || req.CapPct > 100)
                return Results.BadRequest(new { error = "capPct must be between 1 and 100" });

            caps.SetCap(req.CliType, req.WindowLabel, req.CapPct);
            return Results.Ok(new
            {
                defaultCapPct = CliQuotaCapsService.DefaultCapPct,
                caps = caps.GetAll()
            });
        });

        // CodingAgentRunner 0.6.0 wait-on-quota policy. Global defaults are
        // opt-in; individual projects may override them through the project
        // settings endpoint.
        cliGroup.MapGet("/quota/wait-policy", (CliQuotaWaitPolicyService policy) =>
            Results.Ok(policy.GetGlobal()));

        cliGroup.MapPut("/quota/wait-policy", (SetCliQuotaWaitPolicyRequest req, CliQuotaWaitPolicyService policy) =>
        {
            if (req.ThresholdMinutes is < CliQuotaWaitPolicyService.MinThresholdMinutes or > CliQuotaWaitPolicyService.MaxThresholdMinutes)
                return Results.BadRequest(new { error = $"thresholdMinutes must be between {CliQuotaWaitPolicyService.MinThresholdMinutes} and {CliQuotaWaitPolicyService.MaxThresholdMinutes}" });
            return Results.Ok(policy.SetGlobal(req.Enabled, req.ThresholdMinutes));
        });

        // Every selectable CLI gets an effective row: the operator's persisted
        // profile when one names a fallback, else the equivalence-catalogue
        // derived pair (AGT-2751). IsFallbackDerived tells the picker whether
        // to render "configured" or "auto (equivalence tier)".
        cliGroup.MapGet("/quota/model-routes", (
            CliQuotaFallbackService routes,
            IModelEquivalenceCatalog catalogue,
            CliFallbackPreferenceService preferences,
            QuotaService quota,
            CliQuotaCapsService caps,
            TaskScannerService scanner,
            ProjectSettingsService projectSettings) =>
        {
            var profiles = CliTypes.All.ToDictionary(
                cli => cli,
                cli => routes.GetEffectiveProfile(cli),
                StringComparer.OrdinalIgnoreCase);
            var table = catalogue.Routes.Select(RouteView).ToList();
            foreach (var profile in routes.GetAll().Values.Where(profile =>
                         !string.IsNullOrWhiteSpace(profile.PrimaryModel)
                         && !string.IsNullOrWhiteSpace(profile.FallbackModel)))
            {
                table.RemoveAll(row =>
                    string.Equals(row.FromCliType, profile.CliType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.FromModel, profile.PrimaryModel, StringComparison.OrdinalIgnoreCase));
                table.Add(OverrideView(profile));
            }
            foreach (var used in ModelsInUse(scanner, projectSettings))
            {
                if (table.Any(row =>
                        string.Equals(row.FromCliType, used.CliType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(row.FromModel, used.Model, StringComparison.OrdinalIgnoreCase)
                        && SameThinking(row.FromThinkingLevel, used.ThinkingLevel)))
                    continue;
                var targetCli = used.CliType == CliTypes.Claude ? CliTypes.Codex : CliTypes.Claude;
                var derived = catalogue.TryGetRoute(used.CliType, used.Model, used.ThinkingLevel, targetCli);
                table.Add(derived is null
                    ? UnavailableView(used.CliType, used.Model, used.ThinkingLevel, catalogue.Version)
                    : RouteView(derived));
            }

            var states = CliTypes.All.ToDictionary(
                cli => cli,
                cli => QuotaState(cli, quota.GetCachedFor(cli), caps, preferences.Get(cli)),
                StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                profiles,
                routes = table.OrderBy(row => row.FromCliType).ThenBy(row => row.FromModel),
                catalogueVersion = catalogue.Version,
                states,
                callersCannotReroute = new[]
                {
                    new
                    {
                        caller = "quota-probe",
                        cliType = "provider-native",
                        reason = "Provider quota introspection does not invoke a model and cannot switch CLI families.",
                    },
                },
            });
        });

        cliGroup.MapPut("/quota/fallback-preference", (
            SetCliFallbackPreferenceRequest req,
            CliFallbackPreferenceService preferences,
            QuotaService quota,
            CliQuotaCapsService caps) =>
        {
            if (!CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{req.CliType}'" });
            var now = DateTime.UtcNow;
            var reset = quota.GetCachedFor(req.CliType)?.Windows
                .Where(window => window.ResetAt > now)
                .OrderByDescending(window => (window.UsedPct ?? 0)
                    / Math.Max(1, caps.GetCap(req.CliType, window.Label)))
                .ThenBy(window => window.ResetAt)
                .Select(window => window.ResetAt)
                .FirstOrDefault();
            if (req.PreferFallback && reset is null)
                return Results.BadRequest(new { error = "A fresh quota window with a future reset is required." });
            return Results.Ok(preferences.Set(req.CliType, req.PreferFallback, reset));
        });

        cliGroup.MapPut("/quota/model-routes", (SetCliModelRouteRequest req, CliQuotaFallbackService routes) =>
        {
            if (!CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{req.CliType}'" });
            if (!string.IsNullOrWhiteSpace(req.FallbackCliType) && !CliTypes.IsValid(req.FallbackCliType))
                return Results.BadRequest(new { error = $"Unknown fallbackCliType '{req.FallbackCliType}'" });
            var saved = routes.Set(new CliModelRouteProfile
            {
                CliType = req.CliType,
                PrimaryModel = req.PrimaryModel,
                PrimaryThinkingLevel = req.PrimaryThinkingLevel,
                FallbackCliType = req.FallbackCliType,
                FallbackModel = req.FallbackModel,
                FallbackThinkingLevel = req.FallbackThinkingLevel,
            });
            return Results.Ok(saved);
        });

        // ── TEMPORARY: PTY slash-command probe for parser development ──
        // Spawns the requested CLI in a scratch dir, sends a slash command,
        // waits for output to settle, returns the ANSI-stripped snapshot.
        // Example: /api/cli/_probe/claude?cmd=/usage
        cliGroup.MapGet("/_probe/{cliType}", async (
            string cliType,
            string? cmd,
            string? followUp,
            int? settleMs,
            int? followUpSettleMs,
            CliRouter router,
            CliEnvironment env,
            CancellationToken ct) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            var slashCmd = string.IsNullOrWhiteSpace(cmd) ? "/usage" : cmd!;
            var settle = settleMs ?? 2500;

            var cli = router.Get(cliType);
            var (available, _, resolvedPath) = cli.TestCliPath();
            if (!available)
                return Results.BadRequest(new { error = $"{cliType} CLI not available" });

            var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-probe", cliType);
            Directory.CreateDirectory(scratch);
            try { env.EnsureFolderTrusted(scratch); env.EnsureTerminalSetupAcknowledged("vscode", "vscode-insiders", "windows-terminal"); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliEndpoints:178"); }

            try
            {
                await using var pty = await PtySession.SpawnAsync(
                    app: resolvedPath,
                    cwd: scratch,
                    extraEnv: CliEnvironment.ProbeEnvironment(),
                    ct: ct);
                await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);
                // For Claude/Codex confirm trust prompt first.
                if (cliType is "claude" or "codex")
                {
                    await pty.SendKeysAsync("1<Enter>", ct);
                    await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);
                }
                var preLen = pty.SnapshotStripped().Length;
                await pty.SendKeysAsync(slashCmd + "<Enter>", ct);
                await pty.WaitForIdleAsync(idleMs: settle, timeoutMs: 12000, ct);
                if (!string.IsNullOrEmpty(followUp))
                {
                    await pty.SendKeysAsync(followUp, ct);
                    await pty.WaitForIdleAsync(idleMs: followUpSettleMs ?? 2000, timeoutMs: 10000, ct);
                }
                var snap = pty.SnapshotStripped();
                try { await pty.SendKeysAsync("<Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliEndpoints:199"); }
                try { await pty.SendKeysAsync("<Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliEndpoints:200"); }
                return Results.Ok(new
                {
                    cliType,
                    command = slashCmd,
                    followUp,
                    resolvedPath,
                    preCharCount = preLen,
                    snapshotLength = snap.Length,
                    snapshot = snap
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Probe failed");
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }

    private static CliModelRouteView RouteView(ModelEquivalenceRoute route) => new(
        route.FromCliType, route.FromModel, route.FromThinkingLevel,
        route.ToCliType, route.ToModel, route.ToThinkingLevel,
        "catalogue", route.CatalogueVersion,
        route.FromInputPerMTok, route.FromOutputPerMTok,
        route.ToInputPerMTok, route.ToOutputPerMTok,
        route.CapabilityClass,
        true);

    private static CliModelRouteView OverrideView(CliModelRouteProfile profile)
    {
        var from = Price(profile.PrimaryModel);
        var to = Price(profile.FallbackModel);
        return new CliModelRouteView(
            profile.CliType, profile.PrimaryModel!, profile.PrimaryThinkingLevel,
            profile.FallbackCliType ?? profile.CliType, profile.FallbackModel!, profile.FallbackThinkingLevel,
            "override", null,
            from.Input, from.Output, to.Input, to.Output,
            "operator-override",
            true);
    }

    private static CliModelRouteView UnavailableView(
        string cliType, string model, string? thinkingLevel, string version)
    {
        var from = Price(model);
        return new CliModelRouteView(
            cliType, model, thinkingLevel,
            string.Empty, "wait: no comparable model", null,
            "catalogue", version,
            from.Input, from.Output, null, null,
            "unresolved",
            false);
    }

    private static IReadOnlyList<(string CliType, string Model, string? ThinkingLevel)> ModelsInUse(
        TaskScannerService scanner,
        ProjectSettingsService projectSettings)
    {
        var rows = new List<(string CliType, string Model, string? ThinkingLevel)>();
        foreach (var task in scanner.ScanAllJobs().Where(task =>
                     !string.IsNullOrWhiteSpace(task.Model)
                     && task.State is not TaskStates.Completed and not TaskStates.Archive))
            rows.Add((CliForModel(task.CliType, task.Model!), task.Model!, task.ThinkingLevel));
        foreach (var settings in projectSettings.GetAll().Values)
        {
            Add(settings.OrchestratorModel, settings.OrchestratorThinkingLevel, null);
            Add(settings.EpicPlanningModel, settings.EpicPlanningThinkingLevel, null);
            if (settings.PipelineSteps is not null)
                foreach (var step in settings.PipelineSteps.Values)
                    Add(step.Model, step.ThinkingLevel, step.CliType);
            if (settings.PipelineStepsByType is not null)
                foreach (var pipeline in settings.PipelineStepsByType.Values)
                    foreach (var step in pipeline.Values)
                        Add(step.Model, step.ThinkingLevel, step.CliType);
        }
        return rows.Distinct().ToArray();

        void Add(string? model, string? thinking, string? cli)
        {
            if (!string.IsNullOrWhiteSpace(model))
                rows.Add((CliForModel(cli, model), model.Trim(), thinking));
        }
    }

    private static string CliForModel(string? configuredCli, string model)
    {
        if (!string.IsNullOrWhiteSpace(configuredCli)) return CliTypes.Normalize(configuredCli);
        var metadata = ModelMetadataRegistry.Find(model);
        return string.Equals(metadata?.Vendor, "anthropic", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? CliTypes.Claude
            : CliTypes.Codex;
    }

    private static bool SameThinking(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static (decimal? Input, decimal? Output) Price(string? model)
    {
        var estimate = TokenPricing.Estimate(model, 0, 0, 0, 0);
        return (estimate.PriceBasis?.InputPerMillion, estimate.PriceBasis?.OutputPerMillion);
    }

    private static object QuotaState(
        string cliType,
        QuotaSnapshot? snapshot,
        CliQuotaCapsService caps,
        CliFallbackPreference preference)
    {
        var cap = caps.Evaluate(snapshot);
        var state = preference.Active
            ? "fallback-preferred"
            : cap.Blocked ? "fallback-active" : "normal";
        return new
        {
            cliType,
            state,
            activeSince = preference.Active ? preference.EnabledAt : cap.Blocked ? snapshot?.FetchedAt : null,
            preferenceExpiresAt = preference.ExpiresAt,
            windows = snapshot?.Windows.Select(window => new
            {
                window.Label,
                window.UsedPct,
                capPct = caps.GetCap(cliType, window.Label),
                window.ResetAt,
            }).ToArray() ?? [],
        };
    }
}

public sealed record SetCliQuotaWaitPolicyRequest
{
    public bool Enabled { get; init; }
    public int ThresholdMinutes { get; init; } = CliQuotaWaitPolicyService.DefaultThresholdMinutes;
}

public sealed record SetCliFallbackPreferenceRequest(
    string CliType,
    bool PreferFallback);

public sealed record CliModelRouteView(
    string FromCliType,
    string FromModel,
    string? FromThinkingLevel,
    string ToCliType,
    string ToModel,
    string? ToThinkingLevel,
    string Source,
    string? CatalogueVersion,
    decimal? FromInputPerMTok,
    decimal? FromOutputPerMTok,
    decimal? ToInputPerMTok,
    decimal? ToOutputPerMTok,
    string CapabilityClass,
    bool Reroutable);
