

namespace AgentStudio.Clients;

/// <summary>
/// Routes for client identity registration, listing, lookup, and
/// soft-delete. Pairs with <see cref="ClientIdentityStore"/> and the
/// X-Client-Id middleware.
/// </summary>
public static class ClientEndpoints
{
    public static void MapClientEndpoints(this WebApplication app)
    {
        var clients = app.MapGroup("/api/clients");

        clients.MapPost("/register", (RegisterClientRequest request, ClientIdentityStore store) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.BadRequest(new { error = "displayName is required" });
            }
            try
            {
                var record = store.Register(request);
                return Results.Ok(ClientSummary.From(record));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        clients.MapGet("/", (
            ClientIdentityStore store,
            ProjectRegistry registry,
            ProjectSettingsService projectSettings) =>
        {
            var projects = registry.List();
            var now = DateTime.UtcNow;
            var summaries = store.ListAll().Select(record =>
            {
                var summary = ClientSummary.From(record) with
                {
                    RunnerProjectPreflights = RemoteProjectClaimabilityPolicy.ProjectForRunner(
                        record,
                        projects,
                        projectSettings,
                        now),
                };
                return summary;
            }).ToList();
            summaries.AddRange(store.ListDiagnostics().Select(DiagnosticSummary));
            return Results.Ok(summaries);
        });

        clients.MapGet("/{id}", (string id, ClientIdentityStore store, TaskScannerService scanner) =>
        {
            var record = store.Find(id);
            if (record is null)
            {
                var diagnostic = store.FindDiagnostic(id);
                if (diagnostic is not null)
                {
                    return Results.Conflict(new
                    {
                        error = "identity-file-corrupt",
                        message = diagnostic.Message,
                        file = diagnostic.FileName,
                        modifiedAt = diagnostic.ModifiedAt,
                        sizeBytes = diagnostic.SizeBytes,
                        detail = diagnostic.Detail,
                        hint = diagnostic.RestoreHint,
                    });
                }
                return Results.NotFound(new { error = "client-not-found" });
            }

            var owned = scanner.ScanAllAutomationJobs()
                .Where(j => string.Equals(j.OwnerClientId, id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.LastActivity)
                .ToList();

            var detail = new ClientDetail
            {
                Identity = ClientSummary.From(record),
                OwnedJobCount = owned.Count,
                RecentJobIds = owned.Take(10).Select(j => j.Id).ToList()
            };
            return Results.Ok(detail);
        });

        // Compatibility route: DELETE used to flip kind immediately. Keep the
        // route for older callers, but give it the same graceful semantics as
        // the explicit retire action. Permanent deletion is deliberately only
        // available through DELETE /{id}/permanent after retirement.
        clients.MapDelete("/{id}", (string id, ClientIdentityStore store) =>
        {
            if (string.Equals(id, DefaultClientIdentity.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "default-identity-cannot-be-retired" });
            }
            var updated = store.RequestDrain(id, retireAfterDrain: true);
            return updated is not null
                ? Results.Ok(ClientSummary.From(updated))
                : Results.NotFound(new { error = "client-not-found-or-retired" });
        });

        clients.MapPost("/{id}/drain", (string id, ClientIdentityStore store) =>
        {
            var updated = store.RequestDrain(id, retireAfterDrain: false);
            return updated is null
                ? Results.NotFound(new { error = "client-not-found-or-retired" })
                : Results.Ok(ClientSummary.From(updated));
        });

        clients.MapPost("/{id}/retire", (string id, ClientIdentityStore store) =>
        {
            if (string.Equals(id, DefaultClientIdentity.Id, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "default-identity-cannot-be-retired" });
            var updated = store.RequestDrain(id, retireAfterDrain: true);
            return updated is null
                ? Results.NotFound(new { error = "client-not-found-or-retired" })
                : Results.Ok(ClientSummary.From(updated));
        });

        clients.MapPost("/{id}/revive", (string id, ClientIdentityStore store) =>
        {
            var updated = store.Revive(id);
            return updated is null
                ? Results.NotFound(new { error = "client-not-found-or-not-retired" })
                : Results.Ok(ClientSummary.From(updated));
        });

        clients.MapDelete("/{id}/permanent", (string id, ClientIdentityStore store) =>
        {
            if (string.Equals(id, DefaultClientIdentity.Id, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "default-identity-cannot-be-deleted" });
            return store.PermanentlyDelete(id)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "client-must-be-retired-before-delete" });
        });

        clients.MapGet("/{id}/telemetry", (string id, string? window, ClientIdentityStore identities, HostTelemetryStore telemetry) =>
        {
            if (identities.Find(id) is null) return Results.NotFound(new { error = "client-not-found" });
            var selected = window is "1h" or "6h" or "48h" or "14d" ? window : "48h";
            return Results.Ok(telemetry.Query(id, selected));
        });

        clients.MapPost("/{id}/runner-git-capability", (string id, RunnerGitCapabilityRequest? request, HttpContext context, ClientIdentityStore store) =>
        {
            if (request is null || request.Status is not ("ready" or "ready-no-workflow-scope" or "read-only"))
                return Results.BadRequest(new { error = "status must be ready, ready-no-workflow-scope, or read-only" });
            var caller = context.Request.Headers["X-Client-Id"].ToString();
            if (!string.Equals(caller, id, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "runner may only report its own capability" });
            var updated = store.SetRunnerGitCapability(id, request.Status, request.Detail, request.CheckedAt);
            return updated is null ? Results.NotFound(new { error = "client-not-found" }) : Results.Ok(ClientSummary.From(updated));
        });

        // Central host capacity (AGT-2302 / AGT-2376). Capacity is a host fact:
        // one ceiling, one target load, one ramp strategy per execution host,
        // shared by every project that host claims for. Reads stay open like the
        // rest of /api/clients; the write is a mutation and therefore already
        // behind the X-Client-Id registration boundary.
        clients.MapGet("/{id}/runner-capacity", (string id, ClientIdentityStore store) =>
        {
            var record = store.Find(id);
            return record is null
                ? Results.NotFound(new { error = "client-not-found" })
                : Results.Ok(new
                {
                    id = record.Id,
                    maxParallelism = record.RunnerDesiredMaxParallelism,
                    targetLoadPercent = record.RunnerTargetLoadPercent
                                        ?? HostCapacityPolicy.DefaultTargetLoadPercent,
                    rampStrategy = RunnerRampStrategies.Normalize(record.RunnerRampStrategy),
                    effectiveMaxParallelism = record.RunnerEffectiveMaxParallelism,
                    effectiveMaxParallelismAppliedAt = record.RunnerEffectiveMaxParallelismAppliedAt,
                    updatedAt = record.RunnerCapacityUpdatedAt,
                });
        });

        clients.MapPut("/{id}/runner-capacity", (string id, SetRunnerCapacityRequest? request, ClientIdentityStore store) =>
        {
            if (request is null) return Results.BadRequest(new { error = "body-required" });
            if (request.MaxParallelism is { } max
                && (max < HostCapacityPolicy.MinMaxParallelism || max > HostCapacityPolicy.MaxMaxParallelism))
            {
                return Results.BadRequest(new
                {
                    error = "invalid-max-parallelism",
                    message = $"maxParallelism must be between {HostCapacityPolicy.MinMaxParallelism} and {HostCapacityPolicy.MaxMaxParallelism}",
                });
            }
            if (request.TargetLoadPercent is { } load
                && (load < HostCapacityPolicy.MinTargetLoadPercent || load > HostCapacityPolicy.MaxTargetLoadPercent))
            {
                return Results.BadRequest(new
                {
                    error = "invalid-target-load-percent",
                    message = $"targetLoadPercent must be between {HostCapacityPolicy.MinTargetLoadPercent} and {HostCapacityPolicy.MaxTargetLoadPercent}",
                });
            }
            if (request.RampStrategy is not null && !RunnerRampStrategies.IsValid(request.RampStrategy))
            {
                return Results.BadRequest(new
                {
                    error = "invalid-ramp-strategy",
                    allowed = RunnerRampStrategies.All,
                });
            }

            var updated = store.SetRunnerCapacity(
                id, request.MaxParallelism, request.TargetLoadPercent, request.RampStrategy);
            return updated is null
                ? Results.NotFound(new { error = "client-not-found-or-retired" })
                : Results.Ok(ClientSummary.From(updated));
        });

        clients.MapPost("/{id}/runner-project-preflights/invalidate", (string id, ClientIdentityStore store) =>
        {
            var updated = store.InvalidateRunnerProjectPreflightsForHost(id);
            return updated is null
                ? Results.NotFound(new { error = "client-not-found" })
                : Results.Ok(ClientSummary.From(updated));
        });

        // Per-client default CLI + model used when the user creates new tasks
        // (and surfaced into the orchestrator chat prompt so a "create me
        // three tasks" request lands on the user's actual preferences, not a
        // hardcoded fallback). Reads are open; writes require a registered
        // X-Client-Id, same as the rest of /api/.
        clients.MapGet("/{id}/defaults", (string id, ClientIdentityStore store) =>
        {
            var record = store.Find(id);
            if (record is null) return Results.NotFound(new { error = "client-not-found" });
            return Results.Ok(new ClientDefaultsResponse
            {
                Id = record.Id,
                DefaultCliType = record.DefaultCliType,
                DefaultModel = record.DefaultModel,
                DefaultThinkingLevel = record.DefaultThinkingLevel
            });
        });

        clients.MapPut("/{id}/defaults", (string id, SetClientDefaultsRequest? request, ClientIdentityStore store) =>
        {
            if (request is null) return Results.BadRequest(new { error = "body-required" });

            // Empty string clears the corresponding side; null leaves it
            // untouched. The store distinguishes the two via the clear flags.
            var clearCli = request.DefaultCliType is not null && string.IsNullOrWhiteSpace(request.DefaultCliType);
            var clearModel = request.DefaultModel is not null && string.IsNullOrWhiteSpace(request.DefaultModel);
            var clearThinkingLevel = request.DefaultThinkingLevel is not null && string.IsNullOrWhiteSpace(request.DefaultThinkingLevel);

            // Validate the CLI value against the known set if it's a non-empty set.
            string? cli = string.IsNullOrWhiteSpace(request.DefaultCliType) ? null : request.DefaultCliType!.Trim().ToLowerInvariant();
            if (cli is not null && cli is not ("claude" or "codex" or "gemini"))
            {
                return Results.BadRequest(new { error = "invalid-cli-type", allowed = new[] { "claude", "codex", "gemini" } });
            }

            string? model = string.IsNullOrWhiteSpace(request.DefaultModel) ? null : request.DefaultModel!.Trim();
            if (model is { Length: > 200 })
            {
                return Results.BadRequest(new { error = "model-too-long" });
            }

            string? thinkingLevel = string.IsNullOrWhiteSpace(request.DefaultThinkingLevel)
                ? null
                : request.DefaultThinkingLevel!.Trim().ToLowerInvariant();
            if (thinkingLevel is { Length: > 32 })
            {
                return Results.BadRequest(new { error = "thinking-level-too-long" });
            }

            var existing = store.Find(id);
            var normalizedThinkingLevel = thinkingLevel is null
                ? null
                : ModelMetadataRegistry.ResolveThinkingLevel(cli ?? existing?.DefaultCliType, model ?? existing?.DefaultModel, thinkingLevel);

            var updated = store.SetDefaults(id, cli, model, clearCli, clearModel, normalizedThinkingLevel, clearThinkingLevel);
            if (updated is null) return Results.NotFound(new { error = "client-not-found" });

            return Results.Ok(new ClientDefaultsResponse
            {
                Id = updated.Id,
                DefaultCliType = updated.DefaultCliType,
                DefaultModel = updated.DefaultModel,
                DefaultThinkingLevel = updated.DefaultThinkingLevel
            });
        });
    }

    private static ClientSummary DiagnosticSummary(ClientIdentityFileDiagnostic diagnostic) => new()
    {
        Id = diagnostic.IdentityId,
        DisplayName = diagnostic.IdentityId,
        Kind = ClientIdentityKinds.Service,
        RegisteredAt = diagnostic.ModifiedAt,
        Notes = diagnostic.RestoreHint,
        IdentityFileError = diagnostic.Message,
        IdentityFileName = diagnostic.FileName,
        IdentityFileModifiedAt = diagnostic.ModifiedAt,
        IdentityFileSizeBytes = diagnostic.SizeBytes,
        IdentityRestoreHint = diagnostic.RestoreHint,
    };
}
