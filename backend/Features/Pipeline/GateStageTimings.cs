namespace AgentStudio.Pipeline;

/// <summary>Measured wall time, in milliseconds. Missing stages stay unknown, never inferred as zero.</summary>
internal static class GateStageTimings
{
    internal static readonly string[] BuildStages = ["restore", "build", "releaseBuildCheck", "frontendBuild"];

    internal static readonly string[] GateStages = [ "gateQueueWait", "dispatchQueueWait", "bundlePreparation", "bundleTransfer",
            "sourceLockWait", "sourceObjects", "sourceCheckout", "restore", "build", "releaseBuildCheck",
            "frontendBuild", "verdictHandBack"];

    internal static IReadOnlyDictionary<string, long> Reused() => GateStages.ToDictionary(stage => stage, _ => 0L);

    internal static Dictionary<string, long> Sum(IReadOnlyDictionary<string, long> first, IReadOnlyDictionary<string, long> second)
    {
        var stages = new Dictionary<string, long>(first);
        foreach (var (stage, duration) in second) stages[stage] = stages.GetValueOrDefault(stage) + duration;
        return stages;
    }

    internal static IReadOnlyDictionary<string, long> Measure(BuildTestGateResult result, long? checkoutMs)
    {
        var stages = new Dictionary<string, long>(result.StageDurationsMs);
        stages["gateQueueWait"] = result.GateQueueWaitMs;
        if (checkoutMs is { } checkout) stages["sourceCheckout"] = checkout;
        if (result.VerdictSource == GateVerdictSource.CacheHit)
        {
            foreach (var stage in BuildStages) stages[stage] = 0;
            return stages;
        }
        if (result.PreparationManifest is { } manifest) stages["restore"] = manifest.DurationMs;
        foreach (var process in result.Processes)
        {
            var build = process.CommandKind == VerifyCommandKind.Build
                || process.CommandKind is null && (process.Command.Contains("dotnet build", StringComparison.Ordinal)
                    || process.Command.Contains("check-release-build", StringComparison.Ordinal)
                    || process.Command.Contains("run build", StringComparison.Ordinal));
            var stage = process.Phase == "preparation" ? "restore"
                : !build ? null
                : process.Command.Contains("check-release-build", StringComparison.Ordinal) ? "releaseBuildCheck"
                : process.CommandEcosystem == VerifyEcosystem.Node
                    || process.Command.Contains("npm", StringComparison.Ordinal) || process.Command.Contains("ng ", StringComparison.Ordinal)
                    ? "frontendBuild" : "build";
            if (stage is null) continue;
            stages[stage] = stages.GetValueOrDefault(stage)
                + Math.Max(0, (long)(process.CompletedAtUtc - process.StartedAtUtc).TotalMilliseconds);
        }
        // Only a completed command plan proves that omitted build stages cost zero.
        if (result.Verdict == BuildTestGateVerdict.Ok)
            foreach (var stage in BuildStages) stages.TryAdd(stage, 0);
        return stages;
    }
}
