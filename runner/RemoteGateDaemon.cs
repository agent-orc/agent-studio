using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>One registered gate role, with one disposable namespace per fenced claim.</summary>
public sealed class RemoteGateDaemon
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;
    private readonly Func<RemoteReviewWorkspace, string, Task<bool>> _cleanup;

    public RemoteGateDaemon(RunnerOptions options, TaskServerClient client, Action<string> log)
        : this(options, client, log, (workspace, attemptId) => workspace.CleanupAsync(attemptId))
    {
    }

    internal RemoteGateDaemon(RunnerOptions options, TaskServerClient client, Action<string> log,
        Func<RemoteReviewWorkspace, string, Task<bool>> cleanup)
    {
        _options = options;
        _client = client;
        _log = log;
        _cleanup = cleanup;
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        await _client.RegisterAsync(_options.RunnerName, "gate-executor", shutdown);
        await RecoverPreviousClaimAsync(shutdown);
        long generation = 0;
        var nextAdvertisement = DateTime.MinValue;
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(ClaimFile))
                    await RecoverPreviousClaimAsync(shutdown);
                if (DateTime.UtcNow >= nextAdvertisement)
                {
                    await _client.AdvertiseCapabilitiesAsync(
                        RunnerCapabilityProbe.Advertise(_options, gitPushReady: false),
                        null, ++generation, shutdown);
                    nextAdvertisement = DateTime.UtcNow.AddMinutes(1);
                }
                var claim = await _client.ClaimGateAsync(new GateClaimRequest(
                    _options.RunnerId, _client.RunnerInstanceId, 1, _options.TtlSeconds), shutdown);
                if (claim.Status == "claimed" && claim.Subject is not null
                    && claim.Attempt is not null && claim.Lease is not null)
                {
                    SaveClaim(claim);
                    if (await ExecuteAsync(claim.Subject, claim.Attempt, claim.Lease, shutdown))
                        File.Delete(ClaimFile);
                }
                else
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log($"gate poll failed: {exception.GetType().Name}: {exception.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), shutdown); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            }
        }
    }

    private string ClaimFile => Path.Combine(_options.StateDir, "gate-claim.json");

    private void SaveClaim(GateClaimResponse claim)
    {
        Directory.CreateDirectory(_options.StateDir);
        var temporary = ClaimFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(claim));
        File.Move(temporary, ClaimFile, overwrite: true);
    }

    internal async Task RecoverPreviousClaimAsync(CancellationToken ct)
    {
        if (!File.Exists(ClaimFile)) return;
        var claim = JsonSerializer.Deserialize<GateClaimResponse>(await File.ReadAllTextAsync(ClaimFile, ct));
        if (claim?.Subject is null || claim.Attempt is null || claim.Lease is null)
            throw new InvalidDataException("Persisted gate claim is incomplete.");
        var status = await _client.GetGateStatusAsync(claim.Subject.SubjectId, ct);
        var previous = status?.Attempts.FirstOrDefault(item => item.AttemptId == claim.Attempt.AttemptId);
        if (previous is null || (GateStates.IsTerminal(previous.State)
            && previous.CleanedAt is not null))
        {
            File.Delete(ClaimFile);
            return;
        }
        var syntheticSubject = new ReviewSubjectDto(claim.Subject.SubjectId,
            claim.Subject.TaskId, claim.Subject.SourceRunId, claim.Subject.RepositoryId,
            claim.Subject.RepositoryUrl, claim.Subject.ExpectedSha, claim.Subject.ResultRef,
            claim.Subject.SourceBundleArtifactId, claim.Subject.SourceBundleSha256, null,
            claim.Subject.PolicyHash, new ReviewPlanDto([], []), claim.Subject.CreatedAt);
        var lease = claim.Lease;
        var syntheticLease = new ReviewLeaseDto(lease.LeaseId, lease.AttemptId,
            claim.Subject.SubjectId, lease.ExecutorId, lease.InstanceId, lease.HostId,
            lease.Fence, lease.AcquiredAt, lease.ExpiresAt, "active",
            lease.ResourceNamespace, lease.PortBase, lease.AuthorityEpoch);
        var workspace = new RemoteReviewWorkspace(_options, syntheticSubject, syntheticLease, _log);
        await CliProcessReaper.ReapWorkspaceAsync(workspace.AttemptRoot,
            claim.Attempt.AttemptId, _log, ct);
        if (!await workspace.CleanupAsync(claim.Attempt.AttemptId))
            throw new IOException("Previous gate namespace could not be removed.");
        if (GateStates.IsTerminal(previous.State)
            || status!.Attempts.Any(item => item.AttemptNumber > previous.AttemptNumber))
        {
            File.Delete(ClaimFile);
            return;
        }
        await _client.ConfirmGateContainmentAsync(claim.Attempt.AttemptId,
            new GateContainmentRequest(_options.RunnerId, _client.RunnerInstanceId,
                lease.HostId, lease.Fence, lease.ResourceNamespace,
                NoProcesses: true, WorkspaceRemoved: true), ct);
        File.Delete(ClaimFile);
    }

    internal async Task<bool> ExecuteAsync(GateSubject subject, GateAttempt attempt, GateLease lease, CancellationToken shutdown)
    {
        var authority = new GateAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId,
            lease.Fence, lease.AuthorityEpoch);
        var reviewSubject = new ReviewSubjectDto(subject.SubjectId, subject.TaskId, subject.SourceRunId,
            subject.RepositoryId, subject.RepositoryUrl, subject.ExpectedSha, subject.ResultRef,
            subject.SourceBundleArtifactId, subject.SourceBundleSha256, null, subject.PolicyHash,
            new ReviewPlanDto([], []), subject.CreatedAt);
        var reviewLease = new ReviewLeaseDto(lease.LeaseId, lease.AttemptId, attempt.SubjectId,
            lease.ExecutorId, lease.InstanceId, lease.HostId, lease.Fence, lease.AcquiredAt,
            lease.ExpiresAt, "active", lease.ResourceNamespace, lease.PortBase, lease.AuthorityEpoch);
        var workspace = new RemoteReviewWorkspace(_options, reviewSubject, reviewLease, _log);
        var evidence = new List<GateCommandEvidence>();
        var actualSha = subject.ExpectedSha;
        var tree = string.Empty;
        var dirtyBefore = false;
        var dirtyAfter = false;
        var failure = (string?)null;
        var cleanup = "clean";
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        overall.CancelAfter(TimeSpan.FromSeconds(subject.Plan.OverallTimeoutSeconds));
        using var authorityLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        using var leaseLoss = CancellationTokenSource.CreateLinkedTokenSource(overall.Token, authorityLifetime.Token);
        var renewer = RenewUntilDoneAsync(attempt.AttemptId, authority, lease.ExpiresAt, authorityLifetime);
        try
        {
            await _client.AdvanceGateAsync(attempt.AttemptId,
                new GatePhaseRequest(authority, GateStates.Materializing), leaseLoss.Token);
            var proof = await workspace.PrepareAsync(_client, leaseLoss.Token);
            actualSha = proof.ActualHead;
            tree = proof.TreeHash;
            dirtyBefore = proof.DirtyBefore;
            var objectType = await ProcessRunner.RunAsync("git", ["cat-file", "-t", "HEAD"],
                workspace.RepositoryPath, environment: workspace.ProcessEnvironment(),
                clearEnvironment: true, ct: leaseLoss.Token);
            if (!objectType.Success || objectType.StdOut.Trim() != "commit")
                throw new ReviewInfrastructureException("SnapshotUnavailable", "Gate source is not a commit object.");
            await _client.AdvanceGateAsync(attempt.AttemptId,
                new GatePhaseRequest(authority, GateStates.Running), leaseLoss.Token);
            foreach (var command in subject.Plan.Commands)
            {
                var start = DateTime.UtcNow;
                using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(leaseLoss.Token);
                commandDeadline.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));
                var workDir = Path.GetFullPath(Path.Combine(workspace.RepositoryPath,
                    subject.Plan.WorkingSubdirectory, command.WorkingSubdirectory));
                var root = Path.GetFullPath(workspace.RepositoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (workDir != workspace.RepositoryPath && !workDir.StartsWith(root, StringComparison.Ordinal))
                    throw new ReviewInfrastructureException("ToolFailure", "Gate command working directory escapes its workspace.");
                ProcessResult result;
                try
                {
                    result = await ProcessRunner.RunAsync(command.FileName, command.Arguments,
                        workDir, environment: workspace.ProcessEnvironment(), clearEnvironment: true,
                        isolateProcessGroup: true, ct: commandDeadline.Token);
                }
                catch (OperationCanceledException) when (!leaseLoss.IsCancellationRequested)
                {
                    evidence.Add(new GateCommandEvidence(command.StepId, null, true,
                        Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(),
                        "command deadline exceeded", start, DateTime.UtcNow));
                    failure = GateFailureClasses.ExecutionTimeout;
                    break;
                }
                var output = result.StdOut + "\n" + result.StdErr;
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
                var excerpt = BoundedUtf8Tail(output, subject.Plan.MaxOutputBytes);
                evidence.Add(new GateCommandEvidence(command.StepId, result.ExitCode, false,
                    digest, excerpt, start, DateTime.UtcNow));
                if (!result.Success)
                {
                    failure = result.ExitCode is 126 or 127
                        ? GateFailureClasses.ToolFailure : GateFailureClasses.ProductFailure;
                    break;
                }
            }
            var final = await ProcessRunner.RunAsync("git", ["status", "--porcelain", "--untracked-files=all"],
                workspace.RepositoryPath, environment: workspace.ProcessEnvironment(),
                clearEnvironment: true, ct: leaseLoss.Token);
            if (!final.Success) throw new ReviewInfrastructureException("ToolFailure", "Gate final dirty proof failed.");
            dirtyAfter = !string.IsNullOrWhiteSpace(final.StdOut);
            var finalHead = await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"],
                workspace.RepositoryPath, environment: workspace.ProcessEnvironment(),
                clearEnvironment: true, ct: leaseLoss.Token);
            var finalTree = await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD^{tree}"],
                workspace.RepositoryPath, environment: workspace.ProcessEnvironment(),
                clearEnvironment: true, ct: leaseLoss.Token);
            if (!finalHead.Success || !finalTree.Success
                || !string.Equals(finalHead.StdOut.Trim(), subject.ExpectedSha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(finalTree.StdOut.Trim(), tree, StringComparison.OrdinalIgnoreCase))
            {
                dirtyAfter = true;
                failure = GateFailureClasses.ToolFailure;
                _log($"gate subject changed during execution attempt={attempt.AttemptId}");
            }
        }
        catch (ReviewInfrastructureException exception)
        {
            failure = exception.Classification switch
            {
                "SnapshotUnavailable" or "SourceBundleDigestMismatch" or "ShaMismatch" or "RepositoryMismatch"
                    => GateFailureClasses.SnapshotUnavailable,
                _ => GateFailureClasses.ToolFailure,
            };
            _log($"gate infrastructure failure attempt={attempt.AttemptId}: {exception.Message}");
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            failure = authorityLifetime.IsCancellationRequested && !overall.IsCancellationRequested
                ? GateFailureClasses.LostLease : GateFailureClasses.ExecutionTimeout;
        }
        catch (Exception exception)
        {
            failure = GateFailureClasses.ToolFailure;
            _log($"gate tool failure attempt={attempt.AttemptId}: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            try
            {
                var status = await _client.GetGateStatusAsync(subject.SubjectId, CancellationToken.None);
                var phase = status?.Attempts.LastOrDefault(item => item.AttemptId == attempt.AttemptId)?.State;
                if (phase is GateStates.Claimed or GateStates.Materializing or GateStates.Running)
                    await _client.AdvanceGateAsync(attempt.AttemptId,
                        new GatePhaseRequest(authority, GateStates.Reporting), CancellationToken.None);
                await _client.AdvanceGateAsync(attempt.AttemptId,
                    new GatePhaseRequest(authority, GateStates.Cleaning), CancellationToken.None);
            }
            catch (Exception exception)
            {
                _log($"gate cleanup phase pending attempt={attempt.AttemptId}: {exception.Message}");
            }
            try
            {
                if (!await _cleanup(workspace, attempt.AttemptId))
                    cleanup = "failed";
            }
            catch (Exception exception)
            {
                cleanup = "failed";
                _log($"gate cleanup failed attempt={attempt.AttemptId}: {exception.Message}");
            }
        }

        // Phase writes and the report remain fenced. A restart of the Task Server
        // may interrupt a request; retry only while the lease is still current.
        try
        {
            var status = await _client.GetGateStatusAsync(subject.SubjectId, CancellationToken.None);
            if (status is null) return false;
            var phase = status.Attempts.LastOrDefault(item => item.AttemptId == attempt.AttemptId)?.State;
            var phases = new[] { GateStates.Claimed, GateStates.Materializing,
                GateStates.Running, GateStates.Reporting, GateStates.Cleaning };
            var currentIndex = Array.IndexOf(phases, phase);
            for (var index = currentIndex + 1; index < phases.Length; index++)
            {
                if (currentIndex < 0) break;
                var next = phases[index];
                if (next == GateStates.Running && failure is not null && evidence.Count == 0)
                    continue;
                await _client.AdvanceGateAsync(attempt.AttemptId,
                    new GatePhaseRequest(authority, next), CancellationToken.None);
            }
            var outcome = failure == GateFailureClasses.ProductFailure
                ? GateStates.ProductFailed : failure is null ? GateStates.Passed : GateStates.InfraFailed;
            var report = new GateReport(outcome, failure, actualSha, tree, dirtyBefore, dirtyAfter,
                evidence, $"{lease.HostId}:{RuntimeInformation.OSDescription}",
                RuntimeInformation.FrameworkDescription, evidence.Select(item => item.OutputSha256).ToArray(),
                [], cleanup);
            await _client.ReportGateAsync(attempt.AttemptId,
                new SubmitGateReportRequest(authority, report), CancellationToken.None);
            return cleanup == "clean";
        }
        catch (Exception exception)
        {
            _log($"gate report pending attempt={attempt.AttemptId}: {exception.Message}");
            return false;
        }
        finally
        {
            authorityLifetime.Cancel();
            try { await renewer; } catch (OperationCanceledException) { }
        }
    }

    private async Task RenewUntilDoneAsync(string attemptId, GateAuthority authority,
        DateTime initialExpiry, CancellationTokenSource execution)
    {
        var expiry = initialExpiry;
        while (!execution.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), execution.Token);
                var lease = await _client.RenewGateAsync(attemptId,
                    new GateRenewRequest(authority, _options.TtlSeconds), execution.Token);
                expiry = lease.ExpiresAt;
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested) { return; }
            catch (TaskServerException exception) when (exception.ErrorCode == "stale-gate-fence")
            {
                execution.Cancel();
                return;
            }
            catch (Exception exception)
            {
                _log($"gate lease renewal failed attempt={attemptId}: {exception.Message}");
                if (DateTime.UtcNow >= expiry) { execution.Cancel(); return; }
            }
        }
    }

    private static string BoundedUtf8Tail(string output, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(output) <= maximumBytes) return output;
        var low = 0;
        var high = Math.Min(output.Length, maximumBytes);
        while (low < high)
        {
            var count = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(output.AsSpan(output.Length - count)) <= maximumBytes)
                low = count;
            else high = count - 1;
        }
        return output[^low..];
    }
}
