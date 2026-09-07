namespace AgentStudio.Watcher;

/// <summary>Operator answer on a Watcher proposal.</summary>
/// <param name="Decision">approved | edited | merged | rejected.</param>
/// <param name="Reason">Required on a rejection; it feeds the suppression list.</param>
/// <param name="MergeIntoTaskKey">Required on a merge.</param>
public sealed record WatcherDecisionRequest(string Decision, string? Reason, string? MergeIntoTaskKey);

/// <summary>
/// Read surface for the Watcher plus the one write an operator makes: the
/// decision on a proposal. There is deliberately no endpoint that creates a
/// case or a proposal by hand; both are outputs of the sweep.
/// </summary>
public static class WatcherEndpoints
{
    public static void MapWatcherEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/watcher");

        group.MapGet("/status", (
            WatcherHostedService watcher,
            WatcherSweepService sweep,
            WatcherSignalCollector collector,
            WatcherStore store,
            IConfiguration configuration) =>
        {
            var options = WatcherOptions.FromConfiguration(configuration);
            var snapshot = watcher.Current;
            return Results.Ok(new
            {
                snapshot,
                enabled = options.Enabled,
                intervalSeconds = (int)options.Interval.TotalSeconds,
                analysisEnabled = options.AnalysisEnabled,
                analysisTier = options.AnalysisTier,
                detectorClasses = WatcherDetectorClasses.All,
                sources = collector.SourceNames,
                contingent = sweep.Contingent(),
                // The Watcher does not judge its own health; the fact is
                // published so an independent monitor can.
                stale = snapshot.IsStale(
                    DateTime.UtcNow, options.Interval, WatcherDefaults.HeartbeatMissesBeforeUnavailable),
                storeAvailable = store.IsAvailable,
            });
        });

        group.MapGet("/contingent", (WatcherSweepService sweep) => Results.Ok(sweep.Contingent()));

        group.MapGet("/cases", (WatcherStore store, string? state, string? detectorClass) =>
        {
            var cases = store.Cases().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(state))
                cases = cases.Where(item => string.Equals(item.State, state, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(detectorClass))
                cases = cases.Where(item =>
                    string.Equals(item.DetectorClass, detectorClass, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { cases = cases.ToList() });
        });

        group.MapGet("/cases/{caseId}", (string caseId, WatcherStore store, IConfiguration configuration) =>
        {
            var item = store.Case(caseId);
            if (item is null) return Results.NotFound(new { error = $"Unknown Watcher case '{caseId}'." });
            var options = WatcherOptions.FromConfiguration(configuration);
            return Results.Ok(new
            {
                @case = item,
                evidencePack = WatcherEvidencePackBuilder.Build(item, options.EvidenceCharacterBudget),
                proposal = item.ProposalId is null ? null : store.Proposal(item.ProposalId),
            });
        });

        group.MapGet("/proposals", (WatcherStore store, string? decision) =>
        {
            var proposals = store.Proposals().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(decision))
                proposals = proposals.Where(item =>
                    string.Equals(item.Decision.State, decision, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { proposals = proposals.ToList() });
        });

        group.MapGet("/proposals/{proposalId}", (string proposalId, WatcherStore store) =>
        {
            var proposal = store.Proposal(proposalId);
            return proposal is null
                ? Results.NotFound(new { error = $"Unknown Watcher proposal '{proposalId}'." })
                : Results.Ok(proposal);
        });

        group.MapPost("/proposals/{proposalId}/decision", async (
            string proposalId,
            WatcherDecisionRequest request,
            HttpContext context,
            WatcherReviewService review,
            CancellationToken ct) =>
        {
            var decidedBy = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? human.User.Username
                : context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "operator";

            var result = await review.DecideAsync(
                proposalId,
                request.Decision?.Trim().ToLowerInvariant() ?? "",
                decidedBy,
                request.Reason,
                request.MergeIntoTaskKey,
                ct);

            return result.Status switch
            {
                WatcherDecisionStatus.Success => Results.Ok(result.Proposal),
                WatcherDecisionStatus.ProposalNotFound =>
                    Results.NotFound(new { error = $"Unknown Watcher proposal '{proposalId}'." }),
                WatcherDecisionStatus.AlreadyDecided => Results.Conflict(new { error = result.Message }),
                WatcherDecisionStatus.MoveRefused => Results.Conflict(new { error = result.Message }),
                WatcherDecisionStatus.CardNotFound =>
                    Results.NotFound(new { error = result.Message }),
                _ => Results.BadRequest(new { error = result.Message ?? "The decision was rejected." }),
            };
        });

        group.MapGet("/suppressions", (WatcherStore store) =>
        {
            var nowUtc = DateTime.UtcNow;
            return Results.Ok(new
            {
                // Expired entries stay visible so an operator can see that a
                // fingerprint was once rejected and when it came back.
                suppressions = store.Suppressions().Select(item => new
                {
                    item.Fingerprint,
                    item.DetectorClass,
                    item.Reason,
                    item.CreatedAtUtc,
                    item.ExpiresAtUtc,
                    item.CreatedBy,
                    active = item.IsActiveAt(nowUtc),
                }).ToList(),
            });
        });

        group.MapGet("/promotion-evidence", (WatcherReviewService review) =>
            Results.Ok(new { classes = review.PromotionEvidence() }));

        group.MapGet("/fixtures", () => Results.Ok(new
        {
            nowUtc = WatcherFixtureMatrix.NowUtc,
            fixtures = WatcherFixtureMatrix.All.Select(fixture => new
            {
                fixture.Id,
                fixture.Finding,
                fixture.Signal,
                fixture.ExpectedDetectorClass,
                fixture.ManualTickets,
                signalCount = fixture.Input.SignalCount,
            }).ToList(),
        }));

        // Operator-triggered cycle. It runs the same code path as the cadence,
        // so a manual run cannot behave differently from an automatic one.
        group.MapPost("/sweep", async (WatcherHostedService watcher, CancellationToken ct) =>
        {
            var result = await watcher.RunOnceAsync(ct);
            return result is null
                ? Results.Conflict(new { error = "The Watcher is disabled (Watcher:Enabled)." })
                : Results.Ok(result);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
    }
}
