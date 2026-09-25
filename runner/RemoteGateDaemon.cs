using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// One-slot deterministic gate executor. The Review workspace supplies exact
/// materialization and containment, while gate authority remains independent.
/// </summary>
public sealed class RemoteGateDaemon
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;
    internal Func<RemoteReviewWorkspace, string, Task<bool>>? CleanupOverride { get; set; }

    public RemoteGateDaemon(RunnerOptions options, TaskServerClient client, Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        if (!_client.UsesDurableTaskServer)
            throw new InvalidOperationException("Gate execution requires the durable Task Server.");
        await _client.RegisterAsync(_options.RunnerName, "gate-executor", shutdown);
        var outbox = new GateReportOutbox(_options.StateDir);
        var claims = new GateClaimJournal(_options.StateDir);
        long generation = 0;
        var nextAdvertisement = DateTime.MinValue;
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (await outbox.ReplayAsync(_client, _log, shutdown))
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
                    continue;
                }
                await RecoverAbandonedClaimsAsync(claims, shutdown);
                if (DateTime.UtcNow >= nextAdvertisement)
                {
                    var capabilities = RunnerCapabilityProbe.Advertise(_options, gitPushReady: false).ToList();
                    if (!string.IsNullOrWhiteSpace(_options.GitRemote)
                        && capabilities.Any(capability => capability.Key == CapabilityProtocol.GateGit))
                    {
                        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                        probeDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                        var reachable = false;
                        try
                        {
                            var probe = await ProcessRunner.RunAsync("git",
                                ["-c", "credential.helper=", "ls-remote", "--exit-code", _options.GitRemote!, "HEAD"],
                                environment: GateProbeEnvironment(), clearEnvironment: true,
                                ct: probeDeadline.Token);
                            reachable = probe.Success;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException || !shutdown.IsCancellationRequested)
                        {
                            _log($"gate repository capability probe failed: {exception.GetType().Name}");
                        }
                        capabilities.Add(new AdvertisedCapabilityDto(
                            CapabilityProtocol.GateRepository(RepositoryIdentityContract.FromUrl(_options.GitRemote)!),
                            "repository", reachable ? "ready" : "unavailable",
                            Identity: _options.GitRemote));
                    }
                    await _client.AdvertiseCapabilitiesAsync(
                        capabilities, telemetry: null, ++generation, shutdown);
                    nextAdvertisement = DateTime.UtcNow.AddMinutes(1);
                }
                var claim = await _client.ClaimGateAsync(
                    new GateClaimRequest(_options.RunnerId, _client.RunnerInstanceId, 1,
                        Math.Clamp(_options.TtlSeconds, 30, 300)), shutdown);
                if (claim.Status == "claimed" && claim.Subject is not null
                    && claim.Attempt is not null && claim.Lease is not null)
                    await ExecuteAsync(claim, outbox, claims, shutdown);
                else
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _log($"gate daemon poll failed: {exception.GetType().Name}: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), shutdown);
            }
        }
    }

    internal Task RunClaimedAsync(GateClaimResponse claim, CancellationToken ct)
        => ExecuteAsync(claim, new GateReportOutbox(_options.StateDir),
            new GateClaimJournal(_options.StateDir), ct);

    private async Task ExecuteAsync(GateClaimResponse claim, GateReportOutbox outbox,
        GateClaimJournal claims, CancellationToken shutdown)
    {
        var subject = claim.Subject!;
        var attempt = claim.Attempt!;
        var lease = claim.Lease!;
        await claims.SaveAsync(claim, shutdown);
        var authority = new GateAuthorityRequest(lease.ExecutorId, lease.InstanceId,
            lease.LeaseId, lease.Fence, lease.AuthorityEpoch);
        var workspace = Workspace(claim);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        overall.CancelAfter(TimeSpan.FromSeconds(subject.Plan.OverallDeadlineSeconds));
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = RenewLoopAsync(attempt.AttemptId, authority, overall, heartbeatStop.Token);

        var evidence = new List<GateCommandEvidence>();
        string? testedSha = null;
        string? testedTree = null;
        var dirtyBefore = false;
        var dirtyAfter = false;
        var outcome = "passed";
        string? classification = null;
        var cleanupStatus = "failed";
        var phaseReady = false;
        try
        {
            await _client.AdvanceGatePhaseAsync(attempt.AttemptId,
                new GatePhaseRequest(authority, GateStates.Materializing), overall.Token);
            var proof = await workspace.PrepareAsync(_client, overall.Token);
            var type = await ProcessRunner.RunAsync("git", ["cat-file", "-t", subject.ExpectedSha],
                workspace.RepositoryPath, environment: workspace.ProcessEnvironment(),
                clearEnvironment: true, ct: overall.Token);
            if (!type.Success || type.StdOut.Trim() != "commit")
                throw new ReviewInfrastructureException("SnapshotUnavailable", "Expected gate object is not a commit.");
            testedSha = proof.ActualHead;
            testedTree = proof.TreeHash;
            dirtyBefore = proof.DirtyBefore;
            await _client.AdvanceGatePhaseAsync(attempt.AttemptId,
                new GatePhaseRequest(authority, GateStates.Running), overall.Token);

            foreach (var command in subject.Plan.Commands)
            {
                var before = DateTime.UtcNow;
                var cwd = SafeSubdirectory(workspace.RepositoryPath,
                    Path.Combine(subject.Plan.WorkingSubdirectory, command.WorkingSubdirectory));
                using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                commandDeadline.CancelAfter(TimeSpan.FromSeconds(command.DeadlineSeconds));
                ProcessResult process;
                try
                {
                    process = await ProcessRunner.RunAsync(command.FileName, command.Arguments,
                        cwd, environment: workspace.ProcessEnvironment(), clearEnvironment: true,
                        isolateProcessGroup: true, ct: commandDeadline.Token);
                }
                catch (OperationCanceledException) when (!overall.IsCancellationRequested)
                {
                    evidence.Add(new GateCommandEvidence(command.StepId, null, true,
                        Digest(""), 0, before, DateTime.UtcNow));
                    outcome = "infra-failed";
                    classification = GateClassifications.ExecutionTimeout;
                    break;
                }
                var output = process.StdOut + process.StdErr;
                var bytes = Encoding.UTF8.GetBytes(output);
                evidence.Add(new GateCommandEvidence(command.StepId, process.ExitCode, false,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    Math.Min(bytes.Length, subject.Plan.MaxOutputBytes), before, DateTime.UtcNow));
                if (process.ExitCode != 0)
                {
                    outcome = process.ExitCode is 126 or 127 ? "infra-failed" : "product-failed";
                    classification = process.ExitCode is 126 or 127
                        ? GateClassifications.ToolFailure : GateClassifications.ProductFailure;
                    break;
                }
            }
            var status = await ProcessRunner.RunAsync("git", ["status", "--porcelain", "--untracked-files=all"],
                workspace.RepositoryPath, environment: workspace.ProcessEnvironment(),
                clearEnvironment: true, ct: overall.Token);
            if (!status.Success)
                throw new ReviewInfrastructureException("ToolFailure", "Gate dirty proof failed.");
            dirtyAfter = status.StdOut.Length > 0;
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            outcome = "infra-failed";
            classification = GateClassifications.ExecutionTimeout;
        }
        catch (ReviewInfrastructureException exception)
        {
            outcome = "infra-failed";
            classification = exception.Classification is "SnapshotUnavailable" or "SourceBundleDigestMismatch"
                ? GateClassifications.MissingSnapshot : GateClassifications.ToolFailure;
            _log($"gate materialization failed attempt={attempt.AttemptId}: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outcome = "infra-failed";
            classification = GateClassifications.ToolFailure;
            _log($"gate execution failed attempt={attempt.AttemptId}: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            try
            {
                await AdvanceToCleaningAsync(attempt.AttemptId, authority);
                phaseReady = true;
            }
            catch (Exception exception)
            {
                _log($"gate cleaning phase unavailable attempt={attempt.AttemptId}: {exception.Message}");
            }
            try
            {
                cleanupStatus = await (CleanupOverride?.Invoke(workspace, attempt.AttemptId)
                    ?? workspace.CleanupAsync(attempt.AttemptId)) ? "complete" : "failed";
            }
            catch (Exception exception)
            {
                _log($"gate cleanup failed attempt={attempt.AttemptId}: {exception.Message}");
            }
            heartbeatStop.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }

        if (shutdown.IsCancellationRequested || !phaseReady) return;
        var report = new GateReport(outcome, classification, testedSha, testedTree,
            dirtyBefore, dirtyAfter, evidence,
            $"{RuntimeInformation.OSDescription}/{RuntimeInformation.ProcessArchitecture}",
            Environment.Version.ToString(),
            evidence.ToDictionary(item => item.StepId, item => item.OutputSha256, StringComparer.Ordinal),
            new Dictionary<string, string>(), cleanupStatus);
        await outbox.EnqueueAsync(attempt.AttemptId,
            new SubmitGateReportRequest(authority, report,
                $"gate:{attempt.AttemptId}:{lease.Fence}"), CancellationToken.None);
        if (!await outbox.ReplayAsync(_client, _log, CancellationToken.None))
            claims.Delete(attempt.AttemptId);
    }

    private RemoteReviewWorkspace Workspace(GateClaimResponse claim)
    {
        var subject = claim.Subject!;
        var lease = claim.Lease!;
        return new RemoteReviewWorkspace(_options,
            new ReviewSubjectDto(subject.SubjectId, subject.TaskId, subject.SourceRunId,
                subject.RepositoryId,
                subject.ResultRef is null ? null : subject.RepositoryUrl,
                subject.ExpectedSha, subject.ResultRef, subject.SourceBundleId,
                subject.SourceBundleSha256, null, subject.PolicyHash,
                new ReviewPlanDto([], []), subject.CreatedAt),
            new ReviewLeaseDto(lease.LeaseId, lease.AttemptId, subject.SubjectId,
                lease.ExecutorId, lease.InstanceId, lease.HostId, lease.Fence,
                lease.AcquiredAt, lease.ExpiresAt, "active", lease.ResourceNamespace,
                24000, lease.AuthorityEpoch), _log);
    }

    private async Task RecoverAbandonedClaimsAsync(GateClaimJournal claims, CancellationToken ct)
    {
        foreach (var pending in await claims.ReadAllAsync(ct))
        {
            var claim = pending.Claim;
            var lease = claim.Lease!;
            var attemptId = claim.Attempt!.AttemptId;
            if (lease.InstanceId == _client.RunnerInstanceId || GateClaimJournal.ProcessStillAlive(pending))
                continue;
            if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
            {
                _log($"gate recovery needs Linux process containment proof attempt={attemptId}");
                continue;
            }
            try
            {
                var workspace = Workspace(claim);
                if (!await workspace.CleanupAsync(attemptId)
                    || WorktreeProcessReaper.FindByCwd(workspace.AttemptRoot).Count != 0
                    || Directory.Exists(workspace.AttemptRoot))
                    continue;
                var receipt = new GateContainmentReceipt(
                    new GateAuthorityRequest(lease.ExecutorId, lease.InstanceId,
                        lease.LeaseId, lease.Fence, lease.AuthorityEpoch),
                    _client.RunnerInstanceId, lease.ResourceNamespace,
                    NoProcesses: true, WorkspaceAbsent: true, DateTime.UtcNow,
                    $"gate-recovery:{attemptId}:{_client.RunnerInstanceId}");
                await _client.RecoverGateAsync(attemptId, receipt, ct);
                claims.Delete(attemptId);
            }
            catch (TaskServerException exception) when (exception.StatusCode == 409)
            {
                _log($"gate recovery rejected attempt={attemptId}: {exception.Message}");
                claims.Delete(attemptId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log($"gate recovery pending attempt={attemptId}: {exception.Message}");
            }
        }
    }

    private async Task RenewLoopAsync(string attemptId, GateAuthorityRequest authority,
        CancellationTokenSource execution, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stop);
                await _client.RenewGateAsync(attemptId, new GateRenewRequest(authority, 120), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (TaskServerException exception) when (exception.StatusCode == 409)
            {
                execution.Cancel();
                return;
            }
            catch (Exception exception)
            {
                _log($"gate lease renewal failed attempt={attemptId}: {exception.Message}");
            }
        }
    }

    private async Task AdvanceToCleaningAsync(string attemptId, GateAuthorityRequest authority)
    {
        await _client.AdvanceGatePhaseAsync(attemptId,
            new GatePhaseRequest(authority, GateStates.Reporting), CancellationToken.None);
        await _client.AdvanceGatePhaseAsync(attemptId,
            new GatePhaseRequest(authority, GateStates.Cleaning), CancellationToken.None);
    }

    private static string SafeSubdirectory(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (path != root && !path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Gate command leaves its repository workspace.");
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Gate command working directory is missing: {relative}");
        var current = new DirectoryInfo(path);
        while (current is not null && current.FullName != root)
        {
            if (current.LinkTarget is not null)
                throw new InvalidOperationException("Gate command working directory contains a symlink.");
            current = current.Parent;
        }
        return path;
    }

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private IReadOnlyDictionary<string, string?> GateProbeEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = Environment.GetEnvironmentVariable("HOME"),
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        foreach (var name in _options.ReviewCredentialEnvironment)
            values[name] = Environment.GetEnvironmentVariable(name);
        return values;
    }
}

internal sealed record PendingGateReport(string AttemptId, SubmitGateReportRequest Request);

internal sealed record PendingGateClaim(GateClaimResponse Claim, int ProcessId, DateTime ProcessStartedAt);

internal sealed class GateClaimJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    public GateClaimJournal(string stateDirectory)
    {
        _directory = Path.Combine(stateDirectory, "gate-claims");
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(GateClaimResponse claim, CancellationToken ct)
    {
        var path = Path.Combine(_directory, claim.Attempt!.AttemptId + ".json");
        var staging = path + ".tmp";
        using var process = Process.GetCurrentProcess();
        var pending = new PendingGateClaim(claim, process.Id, process.StartTime.ToUniversalTime());
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(pending, Json), ct);
        File.Move(staging, path, overwrite: true);
    }

    public async Task<IReadOnlyList<PendingGateClaim>> ReadAllAsync(CancellationToken ct)
    {
        var result = new List<PendingGateClaim>();
        foreach (var path in Directory.GetFiles(_directory, "gat_*.json"))
        {
            var pending = JsonSerializer.Deserialize<PendingGateClaim>(
                await File.ReadAllTextAsync(path, ct), Json);
            if (pending?.Claim.Attempt is not null && pending.Claim.Lease is not null)
                result.Add(pending);
        }
        return result;
    }

    public void Delete(string attemptId)
        => File.Delete(Path.Combine(_directory, attemptId + ".json"));

    public static bool ProcessStillAlive(PendingGateClaim pending)
    {
        try
        {
            using var process = Process.GetProcessById(pending.ProcessId);
            return Math.Abs((process.StartTime.ToUniversalTime() - pending.ProcessStartedAt).TotalSeconds) < 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

internal sealed class GateReportOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    public GateReportOutbox(string stateDirectory)
    {
        _directory = Path.Combine(stateDirectory, "gate-reports");
        Directory.CreateDirectory(_directory);
    }

    public async Task EnqueueAsync(string attemptId, SubmitGateReportRequest request, CancellationToken ct)
    {
        var path = Path.Combine(_directory, attemptId + ".json");
        var staging = path + ".tmp";
        await File.WriteAllTextAsync(staging,
            JsonSerializer.Serialize(new PendingGateReport(attemptId, request), Json), ct);
        File.Move(staging, path, overwrite: true);
    }

    public async Task<bool> ReplayAsync(TaskServerClient client, Action<string> log, CancellationToken ct)
    {
        var pending = Directory.GetFiles(_directory, "gat_*.json");
        foreach (var path in pending)
        {
            var entry = JsonSerializer.Deserialize<PendingGateReport>(
                await File.ReadAllTextAsync(path, ct), Json)
                ?? throw new InvalidDataException("Gate report outbox entry is empty.");
            try
            {
                await client.ReportGateAsync(entry.AttemptId, entry.Request, ct);
                File.Delete(path);
            }
            catch (TaskServerException exception) when (exception.StatusCode == 409)
            {
                log($"gate report was fenced by Task Server attempt={entry.AttemptId}: {exception.Message}");
                File.Delete(path);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log($"gate report remains queued attempt={entry.AttemptId}: {exception.Message}");
            }
        }
        return Directory.GetFiles(_directory, "gat_*.json").Length > 0;
    }
}
