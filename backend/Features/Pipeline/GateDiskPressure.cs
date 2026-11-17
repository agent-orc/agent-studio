using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>The measured file system that was critical when a gate run failed.</summary>
public sealed record GateDiskPressureEvidence(string Purpose, string Path, long FreeBytes,
    long TotalBytes, double FreePercent, string Host);

/// <summary>
/// Names disk pressure on a failed gate instead of a generic environment
/// failure (AGT-3866). On 16 November 2026 the gate host's root file system
/// was 95 % full: the full suite, its baseline and its clean repeat were red
/// with different fingerprints and the batch members were parked as
/// <c>gate-infrastructure</c> without a word about the disk.
/// </summary>
public static class GateDiskPressure
{
    /// <summary>Start of every gate reason and parked detail this attribution writes.</summary>
    public const string ReasonPrefix = "Gate host disk pressure: ";
    public const string Verdict = "disk-pressure";

    /// <summary>
    /// Returns the result unchanged unless it failed for a reason other than
    /// cancellation while <paramref name="decision"/> is critical. Then the run
    /// is a <see cref="BuildTestGateFailureKind.DiskFull"/> gate failure whose
    /// reason names the path, free bytes and free percent.
    /// </summary>
    public static BuildTestGateResult Attach(BuildTestGateResult result,
        HostDiskPressureDecision decision, string host)
    {
        if (result.Verdict != BuildTestGateVerdict.Fail
            || result.FailureKind == BuildTestGateFailureKind.Cancellation
            || result.DiskPressure is not null
            || decision.Level != HostDiskPressureLevel.Critical
            || decision.Filesystem is not { TotalBytes: > 0 } filesystem)
            return result;
        var evidence = new GateDiskPressureEvidence(filesystem.Purpose, filesystem.Path,
            filesystem.FreeBytes, filesystem.TotalBytes,
            Math.Round(100d * filesystem.FreeBytes / filesystem.TotalBytes, 1), host);
        return result with
        {
            FailureKind = BuildTestGateFailureKind.DiskFull,
            DiskPressure = evidence,
            Reason = Describe(evidence) + " Original gate reason: " + result.Reason,
        };
    }

    /// <summary>One sentence an operator can act on, starting with <see cref="ReasonPrefix"/>.</summary>
    public static string Describe(GateDiskPressureEvidence evidence)
        => ReasonPrefix + string.Create(CultureInfo.InvariantCulture,
            $"{evidence.Purpose} file system {evidence.Path} on {evidence.Host} had "
            + $"{evidence.FreeBytes} bytes free ({evidence.FreeBytes / 1073741824d:0.0} GiB, "
            + $"{evidence.FreePercent:0.0} % of {evidence.TotalBytes / 1073741824d:0.0} GiB) "
            + $"when the gate failed, below the critical disk threshold.");

    public static bool Names(string? reason)
        => reason?.Contains(ReasonPrefix, StringComparison.Ordinal) == true;
}
