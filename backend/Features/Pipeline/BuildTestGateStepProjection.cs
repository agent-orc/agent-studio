namespace AgentStudio.Pipeline;

/// <summary>One gate result projected into the pipeline ledger.</summary>
public static class BuildTestGateStepProjection
{
    public static PipelineStepExecution Create(
        PipelineStepStatus status, long durationMs, string verdictToken,
        string reason, BuildTestGateResult? result, DateTime nowUtc) => new()
    {
        StepId = PipelineCatalogue.BuildTestGateStepId,
        Kind = StepKind.Tool,
        Status = status,
        StartedAt = nowUtc - TimeSpan.FromMilliseconds(
            result?.VerdictSource == GateVerdictSource.CacheHit ? 0 : durationMs),
        CompletedAt = nowUtc,
        DurationMs = result?.VerdictSource == GateVerdictSource.CacheHit ? 0 : durationMs,
        Verdict = result?.VerdictSource == GateVerdictSource.CacheHit
            ? $"cached-{verdictToken}" : verdictToken,
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        GateVerdictSource = result?.VerdictSource,
        GateOriginEvidencePath = result?.OriginEvidencePath,
        GateOriginCompletedAtUtc = result?.GateCompletedAtUtc,
    };
}
