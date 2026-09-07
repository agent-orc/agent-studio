namespace AgentStudio.Watcher;

/// <summary>
/// Quota probe health as a presence signal. A probe whose last success is older
/// than its own cache TTL is the exact shape of the 6 September 2026 finding
/// where a launcher stub kept the Claude snapshot eight hours stale.
/// </summary>
/// <remarks>
/// The probe cadence belongs to <see cref="QuotaService"/>. This source reads
/// the cached report only; it never triggers a probe, so a sweep can never turn
/// into a background refresh storm.
/// </remarks>
public sealed class WatcherQuotaSignalSource : IWatcherSignalSource
{
    private readonly QuotaService _quota;

    public WatcherQuotaSignalSource(QuotaService quota)
    {
        _quota = quota;
    }

    public string Name => "quota-probe";

    public Task<WatcherSweepInput> CollectAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var report = _quota.GetCached();
        var presence = new List<WatcherPresenceSignal>();

        foreach (var snapshot in report.Snapshots)
        {
            if (snapshot.ProbeFailedAt is null) continue;
            presence.Add(new WatcherPresenceSignal(
                WatcherSignalSources.CliQuotaProbe,
                $"{snapshot.CliType}:probe",
                snapshot.FetchedAt,
                // The producer's own TTL is the cadence. The Watcher does not
                // invent a second threshold for a signal it does not own.
                _quota.Ttl,
                nowUtc,
                $"The {snapshot.CliType} quota probe has not produced a fresh snapshot")
            {
                Evidence =
                [
                    new WatcherEvidenceItem(
                        "Probe error",
                        snapshot.Error ?? "(probe failed without a diagnostic)",
                        WatcherSignalSources.CliQuotaProbe),
                    new WatcherEvidenceItem(
                        "Probe failed at",
                        snapshot.ProbeFailedAt.Value.ToString("u"),
                        WatcherSignalSources.CliQuotaProbe),
                    snapshot.CliVersion is { Length: > 0 } version
                        ? new WatcherEvidenceItem("CLI version", version, WatcherSignalSources.CliQuotaProbe)
                        : WatcherEvidenceItem.Missing("CLI version", WatcherSignalSources.CliQuotaProbe),
                    new WatcherEvidenceItem(
                        "Last good reading source",
                        snapshot.Source ?? "(unknown)",
                        WatcherSignalSources.CliQuotaProbe),
                ],
            });
        }

        return Task.FromResult(new WatcherSweepInput { NowUtc = nowUtc, Presence = presence });
    }
}

/// <summary>
/// Integration failures across the board. One dirty integration checkout that
/// blocks nine reviewed deliveries shows up here as one fingerprint on nine
/// cards, which is the cross-card repetition shape of QS-102.
/// </summary>
public sealed class WatcherIntegrationSignalSource : IWatcherSignalSource
{
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;

    public WatcherIntegrationSignalSource(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
    }

    public string Name => "integration";

    public Task<WatcherSweepInput> CollectAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var jobs = _scanner.ScanAllAutomationJobs()
            .Where(job => job.State is not (TaskStates.Archive or TaskStates.Backlog))
            .ToList();
        var statuses = _integrationStatus.BuildLookup(jobs);
        var failures = new List<WatcherFailureSignal>();

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (!statuses.TryGetValue(job.TaskKey, out var status)) continue;
            if (status?.Failure is not { } failure) continue;

            failures.Add(new WatcherFailureSignal(
                WatcherSignalSources.Integration,
                job.TaskKey,
                // Code plus reason, not the free-form detail: the detail carries
                // per-card SHAs that would split one project-wide fault into one
                // fingerprint per card.
                $"{failure.Code}: {failure.Reason}",
                job.EnteredLaneAt,
                failure.Label)
            {
                Project = job.ProjectName,
                AffectedCards = [job.TaskKey],
                Evidence =
                [
                    new WatcherEvidenceItem("Integration status", status.Status, WatcherSignalSources.Integration),
                    new WatcherEvidenceItem("Integration branch", status.IntegrationBranch, WatcherSignalSources.Integration),
                    new WatcherEvidenceItem("Failure code", failure.Code, WatcherSignalSources.Integration),
                    status.Detail is { Length: > 0 } detail
                        ? new WatcherEvidenceItem($"Detail for {job.TaskKey}", detail, WatcherSignalSources.Integration)
                        : WatcherEvidenceItem.Missing($"Detail for {job.TaskKey}", WatcherSignalSources.Integration),
                ],
            });
        }

        return Task.FromResult(new WatcherSweepInput { NowUtc = nowUtc, Failures = failures });
    }
}
