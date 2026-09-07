namespace AgentStudio.Pipeline;

/// <summary>
/// One dependency-cache scope as the gate observed it, reduced to the two facts
/// the environment classification needs: which install root it describes and
/// whether the gate trusted a cached tree instead of installing.
/// </summary>
public sealed record GateDependencyCacheDecision(string WorkingSubdir, bool ServedFromCache);

/// <summary>
/// AGT-2720 - pure policy that separates a repairable gate environment from a
/// product failure.
///
/// <para>
/// CAC-18 passed remote review 412 times and failed every pre-main full suite on
/// the Windows studio with an exit-1 stack frame inside
/// <c>node_modules/vite/dist/node/chunks/config.js</c>, before a single vitest
/// test ran. The cause was a dependency-cache entry that held 2,580 of 25,748
/// files; the lock hash still matched, so the gate reported
/// <c>hit / lock-unchanged</c> and skipped <c>npm ci</c> forever. The card was
/// marked <c>partial</c> as though the delivery were red.
/// </para>
///
/// <para>
/// The classification is deliberately bounded rather than open-ended: a toolchain
/// death is an environment failure only while the gate has something to repair,
/// namely a cached dependency tree it did not install itself. Once the scope has
/// been evicted, the next attempt installs fresh; if the same toolchain frame
/// appears again it is reported as a normal product failure. One corruption
/// therefore costs exactly one extra attempt and can never become a retry loop
/// that hides a genuinely red suite.
/// </para>
/// </summary>
public static class GateEnvironmentFailurePolicy
{
    /// <summary>
    /// Frames that can only be produced by the bundler or type checker running as
    /// the toolchain, never by an assertion an authored test wrote. Both path
    /// separators are listed because the gate runs on Windows and Linux hosts.
    /// </summary>
    private static readonly string[] ToolchainFrames =
    [
        "node_modules/vite/dist/", "node_modules\\vite\\dist\\",
        "node_modules/vitest/dist/", "node_modules\\vitest\\dist\\",
        "node_modules/esbuild/", "node_modules\\esbuild\\",
        "node_modules/rollup/dist/", "node_modules\\rollup\\dist\\",
        "node_modules/typescript/lib/", "node_modules\\typescript\\lib\\",
        "node_modules/@angular/build/", "node_modules\\@angular\\build\\",
        "node_modules/@angular-devkit/", "node_modules\\@angular-devkit\\",
    ];

    /// <summary>
    /// Installer integrity messages. They name a broken dependency tree directly,
    /// so they need no stack frame.
    /// </summary>
    private static readonly string[] InstallIntegrityMessages =
    [
        "can only install with an existing package-lock.json",
        "cannot read properties of undefined (reading 'resolved')",
        "failed to resolve entry for package",
    ];

    /// <summary>
    /// Evidence that the runner reported on tests it actually executed. Bundler
    /// progress lines such as vite's "modules transformed" are deliberately not
    /// listed: they mean the toolchain ran, not that a test did.
    /// </summary>
    private static readonly string[] TestDiscoveryEvidence =
    [
        "test files", "test suites:", "tests:", "total tests:",
        "passed!", "failed!", " passing", " failing", "executed ",
    ];

    /// <summary>
    /// True when the command died inside the toolchain before the runner reported
    /// a single test, and at least one dependency scope was served from the cache
    /// without installing. Both halves are required: the first says the failure is
    /// not a product verdict, the second says the gate can still repair it.
    /// </summary>
    public static bool IsRepairableEnvironmentFailure(
        string? evidence,
        IReadOnlyList<GateDependencyCacheDecision> dependencyCache)
        => FailedBeforeTestDiscovery(evidence) && dependencyCache.Any(scope => scope.ServedFromCache);

    /// <summary>
    /// True when the evidence carries a toolchain or installer signature and no
    /// executed-test report. Exposed separately so the reason text can name the
    /// diagnosis even when nothing is left to repair.
    /// </summary>
    public static bool FailedBeforeTestDiscovery(string? evidence)
    {
        var value = evidence ?? string.Empty;
        if (value.Length == 0) return false;
        var hasToolchainSignature = Contains(value, ToolchainFrames)
                                    || Contains(value, InstallIntegrityMessages);
        return hasToolchainSignature && !Contains(value, TestDiscoveryEvidence);
    }

    /// <summary>
    /// The scopes whose cached tree must be dropped before the retry: the ones the
    /// gate trusted instead of installing.
    /// </summary>
    public static IReadOnlyList<string> ScopesToEvict(
        IReadOnlyList<GateDependencyCacheDecision> dependencyCache)
        => dependencyCache
            .Where(scope => scope.ServedFromCache)
            .Select(scope => scope.WorkingSubdir)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool Contains(string value, string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
