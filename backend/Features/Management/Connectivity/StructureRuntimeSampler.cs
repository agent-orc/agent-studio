using System.Net.Http.Json;
using AgentStudio.Cli;
using AgentStudio.Deploy;
using AgentStudio.Diagnostics;
using AgentStudio.Host;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Management;

public sealed class StructureRuntimeSampler(IConfiguration config, StudioSelfObserver self, ProjectRegistry projects, TaskScannerService scanner,
    AttemptAuthorityService authority, DeliveryIntegrationQueueBoard integration, PostMergeRunStore postMerge,
    QuotaService quota, StudioReleasesService releases, StructureHistoryBuffer history, IHttpClientFactory clients, SshGateHostProbe gateProbe, PullGateStore gates)
{
    private StudioReleasesView? _lastReleaseView;

    internal async Task<StructureInstallationFacts> ReadAsync(DateTime now, IReadOnlyList<RunnerCapabilitySnapshotDto> hosts,
        CancellationToken ct)
    {
        var active = projects.List().Where(p => !p.Archived).ToArray();
        var queues = new StructureQueueLengths(integration.WaitingCount, await QueuedReviewsAsync(ct),
            PostMergeQueue(active),
            scanner.TryGetPublishedBoard()?.Live.Count(task => !task.Fixture && task.State == TaskStates.Ready), gates.QueuedCount);
        var (rate, inFlight) = self.RequestLoad(now);
        var api = new StructureApiFacts(new(now, "Studio API", now.AddSeconds(10)), rate, inFlight, queues);
        var readings = hosts.Where(h => now - h.LastSeenAt <= TimeSpan.FromSeconds(90)).Select(h => h.Telemetry).ToArray();
        int? busy = readings.Length > 0 && readings.All(t => t is not null && now - t.ObservedAt <= TimeSpan.FromSeconds(90))
            ? readings.Sum(t => t!.ActiveSlots) : null;
        history.Sample(now, busy, queues.QueuedTasks);
        foreach (var host in hosts)
        {
            if (host.RegisteredAt != default)
                history.Add(new(host.RegisteredAt, "runner registration", "host-joined", host.Name));
            if (host.LastSeenAt != default && now - host.LastSeenAt > TimeSpan.FromSeconds(90))
                history.Add(new(host.LastSeenAt.AddSeconds(90), "heartbeat expiry", "host-left", host.Name));
        }
        if (releases.View is { } view && !ReferenceEquals(view, _lastReleaseView))
        {
            foreach (var release in view.Releases.Where(r => r.TimeBasis == "deploy").OrderByDescending(r => r.AtUtc).Take(20))
                history.Add(new(release.AtUtc, "Studio deployment record", "deploy", release.OccurrenceId));
            foreach (var card in view.Releases.SelectMany(r => r.Cards).Concat(view.Pending)
                .Where(c => c.IntegratedAtUtc.HasValue).OrderByDescending(c => c.IntegratedAtUtc).Take(20))
                history.Add(new(card.IntegratedAtUtc!.Value, "integration receipt", "delivery-integrated", card.Key));
            _lastReleaseView = view;
        }
        var windows = quota.GetCached().Snapshots.Where(s => s.Source != DemoQuotaSnapshots.Source && s.CliType is "codex" or "claude")
            .SelectMany(s => s.Windows.Where(w => w.WindowHours == 168).Select(w => new StructureWeeklyWindow(
                new(s.FetchedAt, s.Source ?? "subscription probe", s.FetchedAt + quota.Ttl), s.CliType,
                w.Label, w.UsedPct, w.ResetAt))).ToArray();
        return new(api, config.GetValue<bool?>("Security:PublicCloudflareEdge"), config["RemoteGate:SshHost"],
            active.Any(p => RemoteProjectRepositoryResolver.ResolveRepositoryUrl(p) is not null), windows, history.Snapshot(now),
            config["Structure:GateNetwork"], new[] { "claude", "codex" }.Where(provider => hosts.Any(h =>
                h.Capabilities.Any(c => c.Key == CapabilityProtocol.ProviderAuthentication(provider)))).ToArray(), gateProbe.LatestDisk);
    }

    private int? PostMergeQueue(IEnumerable<ProjectRecord> active)
    {
        try { return active.Count(project => postMerge.ReadAcceptanceEvidence(project.Id)?.PendingWindow is not null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { SilentCatch.Note(ex, "Structure: post-merge queue unavailable"); return null; }
    }

    private async Task<int?> QueuedReviewsAsync(CancellationToken ct)
    {
        if (!TaskServerPlaneProxy.IsConfigured(config)) return authority.ListPendingReviewAttempts().Count;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName)
                .GetFromJsonAsync<AutoReviewQueueResponse>("/api/v1/studio/runner/auto-review-queue", timeout.Token);
            return response?.Entries.Count(e => e.Status == "queued");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        { SilentCatch.Note(ex, "Structure: remote review queue unavailable"); return null; }
    }
}
