namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2818 - the one-off inventory of cards that claim to be queued and are
/// not. It walks every pickup lane across every project, runs the same
/// <see cref="PickupHoldPolicy"/> the board projection and the runner admission
/// gate use, and reports each held card with its mechanism, reason and age.
///
/// <para>Why a sweep at all: the two reported cards had been held for a month
/// and a week respectively, and neither was visible anywhere except in
/// <c>task.json</c> on disk. Making new holds legible on the card does nothing
/// about the ones already sitting there, so the backlog needs to be named once,
/// out loud, at boot. <see cref="RunOnce"/> is that announcement;
/// <see cref="Sweep"/> is the same evaluation without the logging, for the read
/// endpoint.</para>
///
/// <para>Report-only, like <see cref="ParkedCardRecallSweep"/>. It never
/// releases a gate, never drops an edge, never moves a card. Releasing a
/// validation gate is an operator decision about whether the validation still
/// has to happen, and a sweep is not entitled to make it.</para>
/// </summary>
public sealed class PickupHoldSweep
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _projectSettings;
    private readonly ILogger<PickupHoldSweep> _logger;
    private readonly TimeProvider _clock;
    private readonly AttemptAuthorityService? _attemptAuthority;

    public PickupHoldSweep(
        TaskScannerService scanner,
        ProjectSettingsService projectSettings,
        ILogger<PickupHoldSweep> logger,
        TimeProvider? clock = null,
        AttemptAuthorityService? attemptAuthority = null)
    {
        _scanner = scanner;
        _projectSettings = projectSettings;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _attemptAuthority = attemptAuthority;
    }

    /// <summary>
    /// Every card currently held in a pickup lane, oldest hold first. The
    /// crash-backoff deadline is deliberately not consulted here: it lives in a
    /// live runner's memory, expires within minutes, and a sweep that reported
    /// it would be reporting noise rather than a standstill.
    /// </summary>
    public IReadOnlyList<HeldPickupCard> Sweep(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // The published archive-inclusive reference graph, shared with claim
        // polling and the endpoint overlays. Archive inclusion is the point: a
        // release gate pointing at an archived target is exactly the case this
        // sweep exists to surface, and the board snapshot omits that lane. This
        // route is also served live on request, so it must not rebuild an O(N)
        // graph per call.
        var index = _scanner.GetReferenceIndex();
        var held = new List<HeldPickupCard>();

        foreach (var task in _scanner.ScanAllAutomationJobs())
        {
            ct.ThrowIfCancellationRequested();
            if (!PickupHoldPolicy.IsPickupLane(task.State)) continue;

            var waitsOn = task.References?.DependsOn.Count > 0 ? index.EvaluateWaitsOn(task) : null;
            if (waitsOn is not null && _attemptAuthority is not null)
            {
                waitsOn = waitsOn with
                {
                    Items = waitsOn.Items.Select(item =>
                    {
                        var review = _attemptAuthority.GetTaskProjection(item.Key).CurrentReviewAttempt;
                        var active = review is { State: AttemptLifecycleState.Pending }
                            || review is { State: AttemptLifecycleState.Leased, Lease: { } lease }
                               && lease.ExpiresAt > now;
                        return item with { TargetHasActiveReviewAttempt = active };
                    }).ToList(),
                };
            }
            var hold = PickupHoldPolicy.Evaluate(new PickupHoldFacts(
                Task: task,
                WaitsOn: waitsOn,
                IntakeEnabled: _projectSettings.Get(task.ProjectName).IntakeEnabled == true,
                CrashBackoffUntilUtc: null,
                Rejection: task.RemoteDispatchRejection,
                NowUtc: now));
            if (hold == null) continue;

            held.Add(new HeldPickupCard(
                Key: task.Key ?? task.Id,
                JobId: task.Id,
                Title: task.Title,
                ProjectName: task.ProjectName,
                State: task.State,
                WatchPath: task.WatchPath,
                Hold: hold));
        }

        held.Sort((left, right) => right.Hold.HeldForSeconds.CompareTo(left.Hold.HeldForSeconds));
        return held;
    }

    /// <summary>
    /// Runs the sweep once and writes the backlog to the log as one structured
    /// line per held card plus a summary. Called at boot; a failure here must
    /// never take the host down, so the caller wraps it.
    /// </summary>
    public IReadOnlyList<HeldPickupCard> RunOnce(CancellationToken ct = default)
    {
        var held = Sweep(ct);
        if (held.Count == 0)
        {
            _logger.LogInformation(
                "[taskboard] pickup-hold sweep: no card is held in a pickup lane.");
            return held;
        }

        var unsatisfiable = held.Count(card => card.Hold.Unsatisfiable);
        _logger.LogWarning(
            "[taskboard] pickup-hold sweep: {Count} card(s) sit in a pickup lane that the pickup gate skips, {Unsatisfiable} of them behind a gate that can never open.",
            held.Count, unsatisfiable);

        foreach (var card in held)
        {
            _logger.LogWarning(
                "[taskboard] pickup-hold {Key} ({Project}, {Lane}): mechanism={Mechanism} unsatisfiable={Unsatisfiable} held={HeldDays:F1}d reason={Reason} waysOut={WaysOut}",
                card.Key,
                card.ProjectName,
                card.State,
                card.Hold.Mechanism,
                card.Hold.Unsatisfiable,
                card.Hold.HeldForSeconds / 86400d,
                card.Hold.Reason,
                string.Join(" | ", card.Hold.Resolutions.Select(resolution => resolution.Label)));
        }

        return held;
    }
}

/// <summary>One card the pickup gate skips while it claims to be queued.</summary>
public sealed record HeldPickupCard(
    string Key,
    string JobId,
    string Title,
    string ProjectName,
    string State,
    string WatchPath,
    PickupHoldStatus Hold);
