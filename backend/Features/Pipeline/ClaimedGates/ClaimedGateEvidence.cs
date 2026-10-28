using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Maps a terminal claimed gate status to the same <see cref="BuildTestGateResult"/>
/// evidence fields the SSH bridge worker stamps (DP-S4), so merge, promotion
/// and batch receipts read identically whichever transport ran the gate.
/// </summary>
public static class ClaimedGateEvidence
{
    /// <summary>Evidence fields both transports must fill for an executed gate.</summary>
    public static readonly IReadOnlyList<string> RequiredFields =
    [
        nameof(BuildTestGateResult.GateId),
        nameof(BuildTestGateResult.GateRunId),
        nameof(BuildTestGateResult.GateStartedAtUtc),
        nameof(BuildTestGateResult.GateCompletedAtUtc),
        nameof(BuildTestGateResult.ExpectedSha),
        nameof(BuildTestGateResult.TestedSha),
        nameof(BuildTestGateResult.Executor),
        nameof(BuildTestGateResult.GateProfileDigest),
        nameof(BuildTestGateResult.PipelineDefinitionVersion),
        nameof(BuildTestGateResult.ToolchainIdentity),
    ];

    public static IReadOnlyList<string> MissingFields(BuildTestGateResult result)
    {
        var missing = new List<string>();
        Check(nameof(BuildTestGateResult.GateId), result.GateId);
        Check(nameof(BuildTestGateResult.GateRunId), result.GateRunId);
        Check(nameof(BuildTestGateResult.GateStartedAtUtc), result.GateStartedAtUtc?.ToString("O"));
        Check(nameof(BuildTestGateResult.GateCompletedAtUtc), result.GateCompletedAtUtc?.ToString("O"));
        Check(nameof(BuildTestGateResult.ExpectedSha), result.ExpectedSha);
        Check(nameof(BuildTestGateResult.TestedSha), result.TestedSha);
        Check(nameof(BuildTestGateResult.Executor), result.Executor);
        Check(nameof(BuildTestGateResult.GateProfileDigest), result.GateProfileDigest);
        Check(nameof(BuildTestGateResult.PipelineDefinitionVersion), result.PipelineDefinitionVersion?.ToString());
        Check(nameof(BuildTestGateResult.ToolchainIdentity), result.ToolchainIdentity);
        return missing;

        void Check(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) missing.Add(name);
        }
    }

    public static BuildTestGateResult Result(GateStatus status, string gateId, string label,
        string expectedSha, DateTimeOffset started, DateTimeOffset finished)
    {
        var report = status.Report;
        var output = string.Join('\n', (report?.Commands ?? []).Select(command =>
            $"[{command.StepId}] exit={command.ExitCode?.ToString() ?? "none"}{(command.TimedOut ? " timed-out" : "")}\n{command.OutputExcerpt}"));
        var failed = report?.Commands.FirstOrDefault(command => command.ExitCode is not 0 || command.TimedOut);
        var (verdict, kind, reason) = status.Phase switch
        {
            GateStates.Passed => (BuildTestGateVerdict.Ok, BuildTestGateFailureKind.None, $"{label} passed"),
            GateStates.ProductFailed => (BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Code,
                $"{label} failed in {failed?.StepId ?? "a verify command"}"),
            GateStates.TimedOut => (BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Timeout,
                $"{label} timed out"),
            _ => (BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Environment,
                $"{label} ended {status.Phase}: {status.TerminalOutcome ?? "no outcome"}"),
        };
        if (report?.Proof is { BundleSha256: { } bundle } proof)
            reason += $" Source bundle {bundle} ({proof.BundleBytes} bytes) verified by the host.";
        var attempt = status.Attempts[^1];
        return new BuildTestGateResult(verdict, failed?.ExitCode ?? (verdict == BuildTestGateVerdict.Ok ? 0 : null),
            (long)(finished - started).TotalMilliseconds, output, reason, false, false)
        {
            GateId = gateId,
            GateRunId = status.Subject.SubjectId,
            GateStartedAtUtc = started,
            GateCompletedAtUtc = finished,
            GateQueueWaitMs = (long)(status.QueueAgeSeconds * 1000),
            ExpectedSha = expectedSha,
            TestedSha = report?.TestedSha,
            ToolchainIdentity = report?.ToolchainIdentity,
            GateProfileDigest = status.Subject.PolicyHash,
            PipelineDefinitionVersion = int.TryParse(status.Subject.PipelineDefinitionVersion,
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out var version) ? version : null,
            Executor = $"gate:{attempt.HostId ?? "unclaimed"}/{attempt.AttemptId}#{attempt.Fence}",
            FailureKind = kind,
        };
    }
}
