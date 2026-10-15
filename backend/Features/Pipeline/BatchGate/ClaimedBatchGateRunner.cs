using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Runs the batch suite as claimable gate work (AGT-3055, D16 A) instead of
/// the SSH bridge: the candidate is pushed to its fenced candidate ref, a
/// batch <see cref="GateSubject"/> binds the membership digest, and a runner
/// host with the gate role claims the attempt under lease and fence. The
/// subject lives at the authority runner gate claims reach
/// (<see cref="IBatchGateSubjectAuthority"/>). The Task Server proxy is
/// refused until batch member IDs are mapped to its task and run records.
/// </summary>
public sealed class ClaimedBatchGateRunner(IBatchGateSubjectAuthority gates, GitService git, TimeProvider time)
{
    public const string ExecutionKey = "BatchGate:Execution";
    public const string ExecutionMode = "claimed";
    /// <summary>Default: the batch suite runs through the build/test gate runner (SSH bridge).</summary>
    public const string BridgeMode = "bridge";
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Claimed gate work is opt-in (operator decision 15.10.2026): an unset
    /// value keeps the SSH bridge, so production behavior does not change.
    /// </summary>
    public static bool Enabled(IConfiguration? configuration)
        => Mode(configuration) == ExecutionMode;

    public static void ValidateConfiguration(IConfiguration configuration)
    {
        if (Enabled(configuration) && TaskServerPlaneProxy.IsConfigured(configuration))
            throw new InvalidOperationException(
                "BatchGate:Execution=claimed cannot run with TaskServer:BaseUrl: batch manifests carry "
                + "monolith task and run IDs, while Task Server requires its own IDs. Use BatchGate:Execution=bridge "
                + "or remove TaskServer:BaseUrl.");
    }

    /// <summary>Configured execution mode; an unknown value fails closed at startup.</summary>
    public static string Mode(IConfiguration? configuration)
    {
        var configured = configuration?[ExecutionKey]?.Trim();
        if (string.IsNullOrEmpty(configured)) return BridgeMode;
        if (string.Equals(configured, ExecutionMode, StringComparison.OrdinalIgnoreCase)) return ExecutionMode;
        if (string.Equals(configured, BridgeMode, StringComparison.OrdinalIgnoreCase)) return BridgeMode;
        throw new InvalidOperationException($"{ExecutionKey} must be '{ExecutionMode}' or '{BridgeMode}'.");
    }

    public async Task<BuildTestGateResult> RunAsync(BatchGateManifest manifest, BatchGateRunRecord run,
        string repo, IReadOnlyList<VerifyCommand> verify, TimeSpan timeout, CancellationToken ct)
    {
        if (gates is TaskServerBatchGateSubjectAuthority)
            throw new InvalidOperationException(
                "Claimed batch execution through Task Server requires Task Server task and run IDs; "
                + "use BatchGate:Execution=bridge with TaskServer:BaseUrl.");
        var started = time.GetUtcNow();
        var candidateRef = BatchGatePolicy.CandidateRef(manifest, run.CoordinatorFence);
        var pushed = git.PushBatchCandidateRef(repo, candidateRef, run.CandidateSha, ct);
        if (!pushed.Success && pushed.Status == "candidate-mismatch"
            && git.UpdateBatchCandidateRef(repo, candidateRef, run.CandidateSha, null).Success)
            pushed = git.PushBatchCandidateRef(repo, candidateRef, run.CandidateSha, ct);
        if (!pushed.Success)
            return Infrastructure(run, started, $"Batch candidate ref could not be published: {pushed.Error ?? pushed.Status}");
        var request = Subject(manifest, run, candidateRef, git.ReadOriginUrlAt(repo), verify, timeout,
            time.GetUtcNow().UtcDateTime);
        GateStatus status;
        try { status = await gates.CreateAsync(request, ct).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Infrastructure(run, started,
                $"Batch gate subject could not be created at the {gates.Name} authority: {exception.Message}");
        }
        try
        {
            while (!GateStates.IsTerminal(status.Phase))
            {
                await Task.Delay(PollInterval, time, ct).ConfigureAwait(false);
                status = await gates.GetAsync(status.Subject.SubjectId, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Batch gate subject disappeared.");
            }
        }
        catch (OperationCanceledException)
        {
            // Best effort: an unreachable authority expires the attempt at its dispatch deadline.
            try { await gates.CancelAsync(status.Subject.SubjectId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cancel) when (cancel is HttpRequestException or InvalidDataException)
            {
                SilentCatch.Warn(cancel, "ClaimedBatchGateRunner: cancel batch gate subject");
            }
            throw;
        }
        return Result(run, status, started, time.GetUtcNow());
    }

    internal static CreateGateSubjectRequest Subject(BatchGateManifest manifest, BatchGateRunRecord run,
        string candidateRef, string? repositoryUrl, IReadOnlyList<VerifyCommand> verify, TimeSpan timeout,
        DateTime now)
    {
        var seconds = (int)Math.Clamp(Math.Ceiling(timeout.TotalSeconds), 1, 21600);
        var commands = verify.Select((command, index) => new GateCommand(
            $"{command.Kind.ToString().ToLowerInvariant()}-{index + 1}", "bash", ["-lc", command.Command],
            seconds, command.WorkingSubdir)).ToArray();
        if (commands.Length == 0)
            throw new InvalidOperationException("Batch gate has no verify command to run.");
        var plan = new GatePlan("post-build-test-gate", 1, commands, "", seconds,
            GateRules.RequiredCapabilities(commands), 32768, "always", manifest.MembershipDigest);
        var members = manifest.Members;
        return new CreateGateSubjectRequest(
            members[0].TaskKey, members[0].RunAttempt, manifest.Scope.Repository, repositoryUrl,
            run.CandidateSha, candidateRef, null, null, GateRules.PlanHash(plan),
            manifest.Scope.GateProfileDigest, manifest.Scope.PlatformVersion,
            Digest(JsonSerializer.Serialize(new { run.BatchRunId, commands = verify.Select(item => item.Command) })),
            plan, now.Add(timeout), 2, manifest.BatchId, manifest.MembershipDigest, manifest.BaseSha,
            members.Select(member => member.RunAttempt).ToArray());
    }

    internal static BuildTestGateResult Result(BatchGateRunRecord run, GateStatus status,
        DateTimeOffset started, DateTimeOffset finished)
    {
        var report = status.Report;
        var output = string.Join('\n', (report?.Commands ?? []).Select(command =>
            $"[{command.StepId}] exit={command.ExitCode?.ToString() ?? "none"}{(command.TimedOut ? " timed-out" : "")}\n{command.OutputExcerpt}"));
        var failed = report?.Commands.FirstOrDefault(command => command.ExitCode is not 0 || command.TimedOut);
        var (verdict, kind, reason) = status.Phase switch
        {
            GateStates.Passed => (BuildTestGateVerdict.Ok, BuildTestGateFailureKind.None, "claimed batch gate passed"),
            GateStates.ProductFailed => (BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Code,
                $"claimed batch gate failed in {failed?.StepId ?? "a verify command"}"),
            GateStates.TimedOut => (BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Timeout,
                "claimed batch gate timed out"),
            _ => (BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Environment,
                $"claimed batch gate ended {status.Phase}: {status.TerminalOutcome ?? "no outcome"}"),
        };
        var attempt = status.Attempts[^1];
        return new BuildTestGateResult(verdict, failed?.ExitCode ?? (verdict == BuildTestGateVerdict.Ok ? 0 : null),
            (long)(finished - started).TotalMilliseconds, output, reason, false, false)
        {
            GateId = "batch-gate",
            GateRunId = status.Subject.SubjectId,
            GateStartedAtUtc = started,
            GateCompletedAtUtc = finished,
            GateQueueWaitMs = (long)(status.QueueAgeSeconds * 1000),
            ExpectedSha = run.CandidateSha,
            TestedSha = report?.TestedSha,
            ToolchainIdentity = report?.ToolchainIdentity,
            Executor = $"gate:{attempt.HostId ?? "unclaimed"}/{attempt.AttemptId}#{attempt.Fence}",
            FailureKind = kind,
        };
    }

    private BuildTestGateResult Infrastructure(BatchGateRunRecord run, DateTimeOffset started, string reason)
        => new(BuildTestGateVerdict.Fail, null, (long)(time.GetUtcNow() - started).TotalMilliseconds,
            reason, reason, false, false)
        {
            GateId = "batch-gate", ExpectedSha = run.CandidateSha,
            FailureKind = BuildTestGateFailureKind.Environment,
        };

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
