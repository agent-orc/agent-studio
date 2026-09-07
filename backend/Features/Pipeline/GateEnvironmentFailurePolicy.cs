namespace AgentStudio.Pipeline;

/// <summary>
/// One diagnosis of a gate process that never reached its own test discovery.
/// <see cref="Marker"/> is the raw signature that matched; <see cref="Reason"/>
/// is the operator-facing sentence the card renders.
/// </summary>
public sealed record GateEnvironmentDiagnosis(string Marker, string Reason)
{
    public static readonly GateEnvironmentDiagnosis None = new("", "");

    public bool IsEnvironmentFailure => Marker.Length > 0;
}

/// <summary>
/// Pure decision for AGT-2720: did a gate command die inside its own bundler or
/// toolchain BEFORE the first test ran? Such a run evaluated nothing about the
/// delivery, so it must be classified as a gate environment fault instead of a
/// product failure.
/// <para>
/// CAC-18 is the reference case: on the Windows studio the pre-main full suite
/// exited 1 inside vite's case-insensitive filesystem probe
/// (<c>testCaseInsensitiveFS</c>) before a single vitest test ran, 412 times,
/// while the Linux review host - which installs dependencies fresh - passed the
/// same suite. Reading that as a red suite marked the card <c>partial</c> and
/// stopped the acceptance rail on a delivery nothing had actually tested.
/// </para>
/// <para>
/// The decision is deliberately conservative and needs BOTH halves: the output
/// must carry a known environment signature AND show no evidence that a test
/// runner ever started. A compiler diagnostic, a failing assertion, or any run
/// with discovery output stays a product failure.
/// </para>
/// </summary>
public static class GateEnvironmentFailurePolicy
{
    /// <summary>
    /// Signatures of a toolchain that could not run, ordered most specific
    /// first. Each one names the fault in the operator's words; the card shows
    /// <c>gate environment: &lt;reason&gt;</c>. A signature that a product defect
    /// could also produce carries a <c>Corroborator</c> that must appear too.
    /// </summary>
    private static readonly (string Marker, string? Corroborator, string Reason)[] EnvironmentSignatures =
    [
        ("testCaseInsensitiveFS", null, "vite case-insensitive FS probe failed"),
        ("ERR_MODULE_NOT_FOUND", "node_modules", "a bundler module is missing from node_modules"),
        ("Cannot find module", "node_modules", "a bundler module is missing from node_modules"),
        ("Failed to load native binding", null, "a native toolchain binding failed to load"),
        ("Failed to resolve entry for package", null, "an installed package has no resolvable entry point"),
        ("The service was stopped", "esbuild", "the esbuild service stopped before the build"),
        ("does not match binary version", null, "the installed esbuild binary does not match its host version"),
        ("You installed esbuild for another platform", null, "esbuild was installed for another platform"),
        ("EPERM: operation not permitted", "node_modules", "the gate workspace denied a toolchain file operation"),
    ];

    /// <summary>
    /// Directories whose presence in a stack frame proves the crash happened
    /// inside an installed tool rather than in repository source.
    /// </summary>
    private static readonly string[] ToolchainPackagePaths =
    [
        "node_modules/vite/",
        "node_modules/vitest/",
        "node_modules/esbuild/",
        "node_modules/rollup/",
        "node_modules/typescript/",
        "node_modules/webpack/",
        "node_modules/@angular/build/",
        "node_modules/@angular-devkit/",
    ];

    /// <summary>
    /// Proof that a test runner started. Any of these means the toolchain did
    /// its job and whatever failed afterwards is the product's own result.
    /// </summary>
    private static readonly string[] TestDiscoveryMarkers =
    [
        "Test Files",
        "Tests  ",
        "Tests:",
        "Test suites",
        "Ran all test suites",
        "Starting test execution",
        "Passed!",
        "Failed!",
        "assertion",
        "AssertionError",
        "expect(",
    ];

    public static GateEnvironmentDiagnosis Diagnose(BuildTestGateProcessEvidence process)
    {
        if (process.LaunchError is not null || process.TimedOut || process.Cancelled)
            return GateEnvironmentDiagnosis.None;
        return Diagnose(process.StandardError + "\n" + process.StandardOutput);
    }

    public static GateEnvironmentDiagnosis Diagnose(string? output)
    {
        var text = output ?? string.Empty;
        if (text.Length == 0 || ReachedTestDiscovery(text)) return GateEnvironmentDiagnosis.None;

        foreach (var (marker, corroborator, reason) in EnvironmentSignatures)
        {
            if (!text.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            if (corroborator is not null
                && !text.Contains(corroborator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return new GateEnvironmentDiagnosis(marker, reason);
        }

        var normalized = text.Replace('\\', '/');
        foreach (var package in ToolchainPackagePaths)
        {
            if (normalized.Contains(package, StringComparison.OrdinalIgnoreCase)
                && HasStackFrame(normalized))
            {
                return new GateEnvironmentDiagnosis(
                    package,
                    $"the gate crashed inside {PackageName(package)} before test discovery");
            }
        }

        return GateEnvironmentDiagnosis.None;
    }

    /// <summary>Prefix that makes a gate environment fault recognizable in any reason line.</summary>
    public const string ReasonPrefix = "gate environment: ";

    /// <summary>
    /// True when a completed gate result is a toolchain fault rather than a
    /// verdict about the delivery. A durable gate receipt replayed after a crash
    /// carries only the reason line, so the prefix counts as evidence too.
    /// </summary>
    public static bool IsGateEnvironmentFailure(BuildTestGateResult? result)
        => result is not null
           && (result.FailureKind == BuildTestGateFailureKind.GateEnvironment
               || (result.GateEnvironmentReason ?? result.Reason ?? string.Empty)
                   .StartsWith(ReasonPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The card-visible sentence for one diagnosed gate environment fault.</summary>
    public static string DescribeReason(GateEnvironmentDiagnosis diagnosis)
        => ReasonPrefix + diagnosis.Reason;

    private static bool ReachedTestDiscovery(string text)
        => TestDiscoveryMarkers.Any(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // A bare package path can appear in an ordinary import diagnostic. A stack
    // frame is what proves the tool itself threw.
    private static bool HasStackFrame(string normalizedText)
        => normalizedText.Contains("\n    at ", StringComparison.Ordinal)
           || normalizedText.Contains("\nat ", StringComparison.Ordinal)
           || normalizedText.StartsWith("at ", StringComparison.Ordinal);

    private static string PackageName(string packagePath)
        => packagePath.Replace("node_modules/", string.Empty, StringComparison.Ordinal).TrimEnd('/');
}
