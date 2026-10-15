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
/// host with the gate role claims the attempt under lease and fence.
/// </summary>
public sealed class ClaimedBatchGateRunner(MonolithGateStore gates, GitService git, TimeProvider time)
{
    public const string ExecutionMode = "claimed";
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public static bool Enabled(IConfiguration? configuration)
        => string.Equals(configuration?["BatchGate:Execution"], ExecutionMode, StringComparison.OrdinalIgnoreCase);

    public async Task<BuildTestGateResult> RunAsync(BatchGateManifest manifest, BatchGateRunRecord run,
        string repo, IReadOnlyList<VerifyCommand> verify, TimeSpan timeout, CancellationToken ct)
    {
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
        var status = gates.Create(request);
        try
        {
            while (!GateStates.IsTerminal(status.Phase))
            {
                await Task.Delay(PollInterval, time, ct).ConfigureAwait(false);
                status = gates.GetStatus(status.Subject.SubjectId)
                    ?? throw new InvalidDataException("Batch gate subject disappeared.");
            }
        }
        catch (OperationCanceledException)
        {
            gates.Cancel(status.Subject.SubjectId);
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
