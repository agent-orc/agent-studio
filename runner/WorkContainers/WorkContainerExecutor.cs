using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentRunner.WorkContainers;

public sealed record WorkContainerTimings(
    double? ImagePullMs,
    double? GrantToFirstCommandMs,
    double? MaterializeMs,
    double? CommandMs,
    double WallMs);

/// <summary>
/// Durable evidence for one attempt: admission, limits, image, exact SHA,
/// cache state, timings, the flushed log, and verified cleanup.
/// </summary>
public sealed record WorkContainerReceipt(
    string HostId,
    string Role,
    string AttemptId,
    string LeaseId,
    long Fence,
    string Image,
    string ContainerName,
    string Outcome,
    string Reason,
    int? ExitCode,
    WorkContainerLimits? Limits,
    WorkContainerTimings Timings,
    IReadOnlyDictionary<string, string> Cache,
    bool ShaVerified,
    string? LogPath,
    bool CleanupVerified,
    IReadOnlyList<string> CleanupResidue,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    string? CredentialRevocationStatus = null,
    string? SecretDeletionStatus = null);

public sealed record WorkReconciliationAction(string Kind, string Name, string AttemptId, long Fence, string Action, string Reason);

/// <summary>Host memory facts for the AGT-3019 memory floor.</summary>
public sealed record HostMemory(long AvailableBytes, long TotalBytes)
{
    public static HostMemory Read()
    {
        long available = 0, total = 0;
        if (File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !long.TryParse(parts[1], out var kib)) continue;
                if (parts[0] == "MemAvailable:") available = kib * 1024;
                else if (parts[0] == "MemTotal:") total = kib * 1024;
            }
        }
        else
        {
            var info = GC.GetGCMemoryInfo();
            total = info.TotalAvailableMemoryBytes;
            available = Math.Max(0, total - info.MemoryLoadBytes);
        }
        return new HostMemory(available, total);
    }
}

/// <summary>
/// Coordinates one fresh Linux work container per claimed attempt:
/// pre-admission, image presence, admission under the host grant lock,
/// attempt volume and container creation, run, log flush, removal, and
/// verified cleanup. Restart reconciliation removes everything that does not
/// belong to a live lease and fence.
/// </summary>
public sealed class WorkContainerExecutor(
    IWorkContainerRuntime runtime,
    WorkContainerSettings settings,
    Func<HostMemory>? readMemory = null,
    TimeProvider? clock = null,
    Action<string>? log = null,
    IWorkContainerCredentialSource? credentials = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<HostMemory> _readMemory = readMemory ?? HostMemory.Read;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Action<string> _log = log ?? (_ => { });

    public string ReceiptDirectory => Path.Combine(settings.StateDirectory, "receipts");
    public string LogDirectory => Path.Combine(settings.StateDirectory, "logs");

    public async Task<WorkContainerReceipt> RunAsync(
        WorkContainerAttempt attempt, string image, WorkContainerGrant grant, CancellationToken ct)
    {
        var started = _clock.GetUtcNow();
        WorkContainerLaunchPlanner.Validate(attempt, image);

        // 1. Cheap pre-admission: an over-budget or stale grant never touches the runtime.
        var precheck = WorkContainerAdmissionPolicy.Decide(grant, Budget(0), settings);
        if (!precheck.Admitted) return Denied(attempt, image, precheck.Reason, started);

        // 2. Image presence, timed separately from the grant.
        double? pullMs = null;
        if (!await runtime.ImagePresentAsync(image, ct))
        {
            var pullStart = _clock.GetTimestamp();
            var pulled = await runtime.PullAsync(image, ct);
            pullMs = _clock.GetElapsedTime(pullStart).TotalMilliseconds;
            if (!pulled.Ok) return Finish(attempt, image, "", "image-unavailable", pulled.Output, null, null,
                new(pullMs, null, null, null, Elapsed(started)), Empty, false, null, true, [], started);
        }

        // 3. Admission against live grants, then creation, under one host-wide lock
        //    shared by every role adapter process on this host.
        WorkContainerLaunchPlan plan;
        DateTimeOffset grantedAt;
        Directory.CreateDirectory(settings.StateDirectory);
        await using (var _ = await HostFileOperationLock.AcquireAsync(Path.Combine(settings.StateDirectory, "grants.lock"), ct))
        {
            var inventory = await runtime.ListManagedAsync(attempt.HostId, null, ct);
            if (inventory.Containers.Any(item => item.Resource.AttemptId == attempt.AttemptId && item.HoldsGrant))
                return Denied(attempt, image, "attempt-already-running: one container per attempt.", started);
            var admission = WorkContainerAdmissionPolicy.Decide(grant, Budget(inventory.ReservedHalfCores), settings);
            if (!admission.Admitted) return Denied(attempt, image, admission.Reason, started);
            plan = WorkContainerLaunchPlanner.Plan(attempt, image, admission.Limits!, settings,
                credentialRequired: credentials is not null);
            grantedAt = _clock.GetUtcNow();
            var volume = await runtime.CreateVolumeAsync(plan.AttemptVolume, plan.Labels, ct);
            var created = volume.Ok ? await runtime.CreateContainerAsync(plan.CreateArguments, ct) : volume;
            if (!created.Ok)
            {
                var residue = await CleanupAsync(plan, attempt, CancellationToken.None);
                return Finish(attempt, image, plan.ContainerName, "launch-failed", created.Output, null, plan.Limits,
                    new(pullMs, null, null, null, Elapsed(started)), Empty, false, null, residue.Count == 0, residue, started);
            }
        }

        // 4. Run. Cancellation means lease loss or host stop: the container is
        //    removed, never left to finish unfenced.
        int? exitCode = null;
        string outcome;
        string reason;
        WorkCredential? credential = null;
        var secretValues = new List<string>();
        string? revocationStatus = null;
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task renewalTask = Task.CompletedTask;
        try
        {
            var start = await runtime.StartAsync(plan.ContainerName, ct);
            if (!start.Ok)
            {
                outcome = "launch-failed";
                reason = start.Output;
            }
            else
            {
                if (credentials is not null)
                {
                    credential = await credentials.AcquireAsync(attempt, ct);
                    secretValues.Add(credential.Token);
                    var injected = await runtime.InjectGitTokenAsync(plan.ContainerName, credential.Token, ct);
                    if (!injected.Ok) throw new InvalidOperationException(injected.Output);
                    renewalTask = RenewCredentialUntilStoppedAsync(attempt, plan.ContainerName,
                        credential, credentials, renewed =>
                        {
                            credential = renewed;
                            secretValues.Add(renewed.Token);
                        }, renewalStop.Token);
                }
                exitCode = await runtime.WaitAsync(plan.ContainerName, ct);
                (outcome, reason) = exitCode switch
                {
                    0 => ("succeeded", "work command exited 0."),
                    WorkContainerLaunchPlanner.ShaMismatchExitCode => ("sha-mismatch", "HEAD did not match the exact SHA; no work command ran."),
                    null => ("runtime-lost", "the runtime did not report an exit code."),
                    var code => ("failed", $"work command exited {code}."),
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "stopped";
            reason = "lease lost or host stopping; container removed.";
        }
        catch (Exception ex) when (credentials is not null && ex is InvalidOperationException or HttpRequestException)
        {
            outcome = "credential-unavailable";
            reason = "Attempt credential exchange or injection failed.";
        }
        finally
        {
            renewalStop.Cancel();
            try { await renewalTask; } catch (OperationCanceledException) { }
            if (credential is not null && credentials is not null)
            {
                try { revocationStatus = await credentials.RevokeAsync(attempt, credential, CancellationToken.None); }
                catch (Exception) { revocationStatus = "expiry-backstop"; }
            }
        }

        // 5. Flush the log before anything is removed, then clean up and verify.
        var (logPath, markers) = await FlushLogAsync(plan, attempt, secretValues,
            credentials is not null, CancellationToken.None);
        var cleanupResidue = await CleanupAsync(plan, attempt, CancellationToken.None);
        var deletionStatus = cleanupResidue.Count == 0 ? "container-tmpfs-removed" : "cleanup-residue";
        if (credential is not null && credentials is not null && cleanupResidue.Count == 0
            && revocationStatus is not null)
        {
            try { await credentials.RecordDeletionAsync(attempt, credential, deletionStatus, CancellationToken.None); }
            catch (Exception) { deletionStatus = "server-receipt-pending"; }
        }
        var firstCommand = markers.FirstCommand is { } first ? (first - grantedAt).TotalMilliseconds : (double?)null;
        var materialize = markers is { Materialize: { } m, FirstCommand: { } f } ? (f - m).TotalMilliseconds : (double?)null;
        var command = markers is { FirstCommand: { } c, Exit: { } e } ? (e - c).TotalMilliseconds : (double?)null;
        return Finish(attempt, image, plan.ContainerName, outcome, reason, exitCode, plan.Limits,
            new(pullMs, firstCommand, materialize, command, Elapsed(started)), markers.Cache, markers.ShaVerified,
            logPath, cleanupResidue.Count == 0, cleanupResidue, started, revocationStatus, deletionStatus);
    }

    private async Task RenewCredentialUntilStoppedAsync(WorkContainerAttempt attempt, string container,
        WorkCredential initial, IWorkContainerCredentialSource source, Action<WorkCredential> onRenewed,
        CancellationToken ct)
    {
        var current = initial;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            if (current.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5)) continue;
            try
            {
                var renewed = await source.RenewAsync(attempt, current, ct);
                if (renewed is null) continue;
                var injected = await runtime.InjectGitTokenAsync(container, renewed.Token, ct);
                if (!injected.Ok) throw new InvalidOperationException(injected.Output);
                current = renewed;
                onRenewed(renewed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { _log($"work-container credential renewal unavailable attempt={attempt.AttemptId}"); }
        }
    }

    /// <summary>
    /// Restart reconciliation against the host's persisted attempt authority.
    /// Logs are flushed before any removal; nothing is ever started.
    /// </summary>
    public async Task<IReadOnlyList<WorkReconciliationAction>> ReconcileAsync(
        string hostId, IReadOnlyList<WorkAttemptAuthority> authority, CancellationToken ct)
    {
        var byAttempt = authority.GroupBy(item => item.AttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Fence).First(), StringComparer.Ordinal);
        var inventory = await runtime.ListManagedAsync(hostId, null, ct);
        var actions = new List<WorkReconciliationAction>();
        var keptVolumes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in inventory.Containers)
        {
            var resource = container.Resource;
            var verdict = WorkContainerReconciliationPolicy.Decide(resource, byAttempt.GetValueOrDefault(resource.AttemptId));
            if (verdict.Action == WorkResourceAction.FlushAndRemove)
            {
                var logs = await runtime.LogsAsync(resource.Name, ct);
                WriteDurably(Path.Combine(LogDirectory, $"orphan-{resource.Name}.log"), SafeMarkerLog(logs.Output));
                var removed = await runtime.RemoveContainerAsync(resource.Name, ct);
                if (!removed.Ok) throw new InvalidOperationException($"Could not remove orphan container {resource.Name}: {removed.Output}");
            }
            else keptVolumes.Add(resource.Name);
            actions.Add(new("container", resource.Name, resource.AttemptId, resource.Fence, verdict.Action.ToString(), verdict.Reason));
            _log($"work-container reconcile container={resource.Name} attempt={resource.AttemptId} fence={resource.Fence} action={verdict.Action} reason={verdict.Reason}");
        }
        foreach (var volume in inventory.Volumes)
        {
            var verdict = keptVolumes.Contains(volume.Name)
                ? new WorkResourceVerdict(WorkResourceAction.Keep, "volume-of-kept-container.")
                : WorkContainerReconciliationPolicy.Decide(volume, byAttempt.GetValueOrDefault(volume.AttemptId));
            // A live attempt keeps its volume only while its container runs.
            if (verdict.Action == WorkResourceAction.Keep && !keptVolumes.Contains(volume.Name))
                verdict = new(WorkResourceAction.FlushAndRemove, "volume-without-running-container.");
            if (verdict.Action == WorkResourceAction.FlushAndRemove)
            {
                var removed = await runtime.RemoveVolumeAsync(volume.Name, ct);
                if (!removed.Ok) throw new InvalidOperationException($"Could not remove orphan volume {volume.Name}: {removed.Output}");
            }
            actions.Add(new("volume", volume.Name, volume.AttemptId, volume.Fence, verdict.Action.ToString(), verdict.Reason));
            _log($"work-container reconcile volume={volume.Name} attempt={volume.AttemptId} fence={volume.Fence} action={verdict.Action} reason={verdict.Reason}");
        }
        WriteDurably(Path.Combine(settings.StateDirectory, "reconcile-latest.json"), JsonSerializer.Serialize(actions, Json));
        return actions;
    }

    private WorkContainerHostBudget Budget(int reserved)
    {
        var memory = _readMemory();
        return new WorkContainerHostBudget(reserved, memory.AvailableBytes, memory.TotalBytes);
    }

    private async Task<IReadOnlyList<string>> CleanupAsync(WorkContainerLaunchPlan plan, WorkContainerAttempt attempt, CancellationToken ct)
    {
        IReadOnlyList<string> residue = [];
        for (var round = 0; round < 3; round++)
        {
            await runtime.RemoveContainerAsync(plan.ContainerName, ct);
            await runtime.RemoveVolumeAsync(plan.AttemptVolume, ct);
            var inventory = await runtime.ListManagedAsync(attempt.HostId, attempt.AttemptId, ct);
            residue = inventory.Containers.Where(item => item.Resource.Fence == attempt.Fence).Select(item => $"container:{item.Resource.Name}")
                .Concat(inventory.Volumes.Where(item => item.Fence == attempt.Fence).Select(item => $"volume:{item.Name}"))
                .ToArray();
            if (residue.Count == 0) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (round + 1)), ct);
        }
        return residue;
    }

    internal sealed record LogMarkers(
        DateTimeOffset? Materialize,
        DateTimeOffset? FirstCommand,
        DateTimeOffset? Exit,
        IReadOnlyDictionary<string, string> Cache,
        bool ShaVerified);

    private async Task<(string? Path, LogMarkers Markers)> FlushLogAsync(
        WorkContainerLaunchPlan plan, WorkContainerAttempt attempt, IReadOnlyList<string> tokens,
        bool safeMarkersOnly, CancellationToken ct)
    {
        var logs = await runtime.LogsAsync(plan.ContainerName, ct);
        var path = Path.Combine(LogDirectory, $"{plan.ContainerName}.log");
        var redacted = logs.Output;
        foreach (var token in tokens)
            if (!string.IsNullOrEmpty(token)) redacted = redacted.Replace(token, "[REDACTED]", StringComparison.Ordinal);
        var markers = ParseMarkers(redacted);
        WriteDurably(path, safeMarkersOnly ? SafeMarkerLog(redacted) : redacted);
        return (path, markers);
    }

    private static string SafeMarkerLog(string raw)
    {
        var markers = ParseMarkers(raw);
        return JsonSerializer.Serialize(new
        {
            markers.Materialize,
            markers.FirstCommand,
            markers.Exit,
            markers.Cache,
            markers.ShaVerified,
            Output = "omitted-credential-boundary",
        }, Json);
    }

    /// <summary>Reads phase, cache and SHA markers from a timestamped runtime log.</summary>
    internal static LogMarkers ParseMarkers(string log)
    {
        DateTimeOffset? materialize = null, firstCommand = null, exit = null;
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var verified = false;
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var stamp = ParseTimestamp(line[..space]);
            var text = line[(space + 1)..];
            if (text == "AGENT_PHASE materialize") materialize ??= stamp;
            else if (text == "AGENT_PHASE first-command") firstCommand ??= stamp;
            else if (text.StartsWith("AGENT_PHASE exit ", StringComparison.Ordinal)) exit ??= stamp;
            else if (text.StartsWith("AGENT_CACHE ", StringComparison.Ordinal))
            {
                var parts = text.Split(' ');
                if (parts.Length == 3 && (parts[1] is "repo" or "npm" or "nuget")
                    && (parts[2] is "hit" or "miss" or "refresh" or "quarantined"))
                    cache[parts[1]] = parts[2];
            }
            else if (text.StartsWith("AGENT_SHA_VERIFIED ", StringComparison.Ordinal)) verified = true;
        }
        return new LogMarkers(materialize, firstCommand, exit, cache, verified);
    }

    private static DateTimeOffset? ParseTimestamp(string value)
    {
        // Runtimes print nanoseconds; .NET parses at most seven fraction digits.
        var dot = value.IndexOf('.');
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < value.Length && char.IsDigit(value[end])) end++;
            var fraction = value[(dot + 1)..end];
            if (fraction.Length > 7) value = value[..(dot + 1)] + fraction[..7] + value[end..];
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : null;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private WorkContainerReceipt Denied(WorkContainerAttempt attempt, string image, string reason, DateTimeOffset started)
    {
        _log($"work-container denied attempt={attempt.AttemptId} fence={attempt.Fence} reason={reason}");
        return Finish(attempt, image, "", "denied", reason, null, null, new(null, null, null, null, Elapsed(started)),
            Empty, false, null, true, [], started);
    }

    private double Elapsed(DateTimeOffset started) => (_clock.GetUtcNow() - started).TotalMilliseconds;

    private WorkContainerReceipt Finish(
        WorkContainerAttempt attempt, string image, string container, string outcome, string reason, int? exitCode,
        WorkContainerLimits? limits, WorkContainerTimings timings, IReadOnlyDictionary<string, string> cache,
        bool shaVerified, string? logPath, bool cleanupVerified, IReadOnlyList<string> residue, DateTimeOffset started,
        string? revocationStatus = null, string? secretDeletionStatus = null)
    {
        var receipt = new WorkContainerReceipt(attempt.HostId, attempt.Role, attempt.AttemptId, attempt.LeaseId, attempt.Fence,
            image, container, outcome, reason, exitCode, limits, timings, cache, shaVerified, logPath, cleanupVerified, residue,
            started.UtcDateTime, _clock.GetUtcNow().UtcDateTime, revocationStatus, secretDeletionStatus);
        WriteDurably(Path.Combine(ReceiptDirectory,
                $"{WorkContainerLaunchPlanner.Sanitize(attempt.AttemptId)}-f{attempt.Fence.ToString(CultureInfo.InvariantCulture)}.json"),
            JsonSerializer.Serialize(receipt, Json));
        _log($"work-container attempt={attempt.AttemptId} fence={attempt.Fence} outcome={outcome} cleanupVerified={cleanupVerified}");
        return receipt;
    }

    private static void WriteDurably(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(Encoding.UTF8.GetBytes(content));
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
