using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>Repository-sized evidence for a per-task gate. Full process output is transient.</summary>
internal static class FallbackGateEvidence
{
    internal const int MaximumOutputBytes = 64 * 1024;

    internal static void Write(string path, BuildTestGateResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var failedTests = BatchBaseRedPolicy.FailedTests(result);
        var record = new
        {
            result.Verdict,
            result.ExitCode,
            result.DurationMs,
            result.Reason,
            result.RanBackendBuild,
            result.RanFrontendBuild,
            result.TestedSha,
            result.ExpectedSha,
            result.GateRunId,
            result.GateId,
            result.GateProfileDigest,
            result.OriginEvidencePath,
            result.GateStartedAtUtc,
            result.GateCompletedAtUtc,
            result.GateQueueWaitMs,
            result.ViolatedBudget,
            result.CancellationReason,
            result.FailureKind,
            result.FailureFingerprint,
            result.GateCollisionDetected,
            result.RetryPerformed,
            result.PreparationCacheRetryPerformed,
            result.FlakyQuarantinedFailures,
            RecordedFailedTests = failedTests,
            Processes = result.Processes.Select(process => new
            {
                process.Phase,
                process.ExitCode,
                process.StartedAtUtc,
                process.CompletedAtUtc,
                process.Resources,
                process.TestReport,
            }),
            Reruns = result.Processes.Where(process => process.Phase == "flaky-rerun")
                .Select(process => new
                {
                    Reason = process.Phase,
                    Outcome = process.Cancelled ? "cancelled"
                        : process.TimedOut ? "timed-out"
                        : process.ExitCode == 0 ? "passed" : "failed",
                    process.ExitCode,
                    DurationMs = (long)(process.CompletedAtUtc - process.StartedAtUtc).TotalMilliseconds,
                }),
            TestShards = result.TestShards.Select(suite => new
            {
                suite.SuiteId,
                suite.ShardCount,
                suite.WallMs,
                Shards = suite.Shards.Select(shard => new
                {
                    shard.Index,
                    shard.Host,
                    shard.Classes,
                    shard.DurationMs,
                    shard.ExitCode,
                    shard.Outcome,
                    shard.Tests,
                    shard.FailedTests,
                    shard.Rerun,
                }),
            }),
            Output = Tail(result.Output, MaximumOutputBytes),
        };
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, record);
        stream.Flush(flushToDisk: true);
    }

    internal static string Tail(string? output, int maximumBytes)
    {
        if (string.IsNullOrEmpty(output)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(output);
        if (bytes.Length <= maximumBytes) return output;
        var start = bytes.Length - maximumBytes;
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
