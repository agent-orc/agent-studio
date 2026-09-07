namespace AgentStudio.Watcher;

/// <summary>Read and decision surface for the Watcher inbox.</summary>
public static class WatcherEndpoints
{
    public static void MapWatcherEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/watcher");

        // Status strip: what is being watched, what was last done, how many
        // decisions await the operator.
        group.MapGet("/status", (WatcherHostedService watcher) => Results.Ok(watcher.Current));

        group.MapGet("/cases", (WatcherStore store, string? state) =>
        {
            var cases = store.Cases()
                .Where(row => string.IsNullOrWhiteSpace(state)
                              || string.Equals(row.State, state, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.UpdatedAtUtc)
                .ToList();
            return Results.Ok(new { cases });
        });

        group.MapGet("/proposals", (WatcherStore store, string? decision) =>
        {
            var proposals = store.Proposals()
                .Where(row => string.IsNullOrWhiteSpace(decision)
                              || string.Equals(row.Decision, decision, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.CreatedAtUtc)
                .ToList();
            return Results.Ok(new { proposals });
        });

        group.MapGet("/contingent", (
            WatcherHostedService watcher,
            WatcherStore store,
            IConfiguration configuration) =>
        {
            // The strip must stay truthful before the first sweep has run, so
            // it is derived from the store rather than from the last snapshot.
            var options = WatcherOptions.FromConfiguration(configuration);
            var backlog = store.Cases().Count(row =>
                row.State == WatcherCaseStates.Open && row.BacklogReason is not null);
            return Results.Ok(WatcherContingentPolicy.Describe(
                options.Contingent, store.Snapshot().Spend, DateTime.UtcNow, backlog));
        });

        group.MapGet("/suppressions", (WatcherStore store) =>
        {
            var now = DateTime.UtcNow;
            var suppressions = store.Suppressions()
                .Select(row => new
                {
                    row.Fingerprint,
                    row.DetectorClass,
                    row.Reason,
                    row.SuppressedBy,
                    row.SuppressedAtUtc,
                    row.ExpiresAtUtc,
                    active = row.IsActive(now),
                })
                .OrderByDescending(row => row.SuppressedAtUtc)
                .ToList();
            return Results.Ok(new { suppressions });
        });

        group.MapGet("/class-evidence", (WatcherReviewService review) =>
            Results.Ok(new { classes = review.ClassEvidence() }));

        // The one write in review mode. Approve, edit, merge, or reject; the
        // answer is always attributed.
        group.MapPost("/proposals/{proposalId}/decision", async (
            string proposalId,
            WatcherDecisionRequest request,
            HttpContext context,
            WatcherReviewService review,
            CancellationToken ct) =>
        {
            var outcome = await review.DecideAsync(proposalId, request, Actor(context), nowUtc: null, ct);
            return outcome.Applied
                ? Results.Ok(new { outcome.Reason, proposal = outcome.Proposal })
                : Results.BadRequest(new { error = outcome.Reason });
        });

        group.MapDelete("/suppressions/{fingerprint}", (string fingerprint, WatcherReviewService review) =>
            review.Unsuppress(fingerprint)
                ? Results.Ok()
                : Results.NotFound(new { error = $"No suppression for '{fingerprint}'." }));

        // Operator-triggered sweep, for the moment after a fault is repaired
        // when waiting five minutes for confirmation is the wrong experience.
        group.MapPost("/sweep", async (WatcherHostedService watcher, CancellationToken ct) =>
            Results.Ok(await watcher.RunOnceAsync(ct)));
    }

    /// <summary>
    /// Who answered. A decision that cannot be attributed is not a decision, so
    /// the fallback names the channel rather than pretending to be anonymous.
    /// </summary>
    private static string Actor(HttpContext context)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            && !string.IsNullOrWhiteSpace(human.User.Username))
        {
            return human.User.Username;
        }

        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(clientId) ? "local-operator" : clientId;
    }
}
