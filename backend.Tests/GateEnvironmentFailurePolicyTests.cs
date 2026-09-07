using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2720: a gate command that dies inside its own bundler or toolchain BEFORE
/// the first test ran evaluated nothing about the delivery. It must classify as
/// <see cref="BuildTestGateFailureKind.GateEnvironment"/> (infrastructure), never
/// as a product failure.
///
/// The reference case is CAC-18: on the Windows studio the pre-main full suite
/// exited 1 inside vite's case-insensitive filesystem probe before a single
/// vitest test ran, 412 times, while the Linux review host passed the same suite.
/// The classification is deliberately two-sided - a known environment signature
/// AND no evidence that a test runner started - so an ordinary red suite that
/// merely mentions node_modules stays a product failure.
/// </summary>
public sealed class GateEnvironmentFailurePolicyTests
{
    /// <summary>The CAC-18 transcript, as recorded in the failing gate log.</summary>
    private const string Cac18Transcript = """
        > coding-agent-chat@0.1.0 test
        > vitest run

        failed to load config from D:\studio\gate\vite.config.ts
        Error: EINVAL: invalid argument, rename
            at testCaseInsensitiveFS (D:\studio\gate\node_modules\vite\dist\node\chunks\config.js:1911:42)
            at loadConfigFromFile (D:\studio\gate\node_modules\vite\dist\node\chunks\config.js:2043:18)
        """;

    private static BuildTestGateProcessEvidence Evidence(
        string stdout = "",
        string stderr = "",
        int? exitCode = 1,
        bool timedOut = false)
        => new()
        {
            Command = "npm test",
            FileName = "cmd.exe",
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            TimedOut = timedOut,
        };

    [Fact]
    public void Cac18ViteProbe_IsGateEnvironment_NotCode()
    {
        var kind = BuildTestGateRunner.ClassifyFailure(Evidence(stdout: Cac18Transcript));

        Assert.Equal(BuildTestGateFailureKind.GateEnvironment, kind);
    }

    [Fact]
    public void Cac18ViteProbe_NamesTheFaultForTheOperator()
    {
        var diagnosis = GateEnvironmentFailurePolicy.Diagnose(Cac18Transcript);

        Assert.True(diagnosis.IsEnvironmentFailure);
        Assert.Equal(
            "gate environment: vite case-insensitive FS probe failed",
            GateEnvironmentFailurePolicy.DescribeReason(diagnosis));
    }

    [Fact]
    public void GateEnvironment_IsInfrastructure_NeverAProductVerdict()
    {
        var result = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 10, Cac18Transcript, "gate environment: x", false, true)
        {
            FailureKind = BuildTestGateFailureKind.GateEnvironment,
        };

        Assert.True(result.IsInfrastructureFailure);
        Assert.True(GateEnvironmentFailurePolicy.IsGateEnvironmentFailure(result));
    }

    [Theory]
    [InlineData(
        "Error [ERR_MODULE_NOT_FOUND]: Cannot find module '/repo/node_modules/vite/dist/node/index.js'",
        "a bundler module is missing from node_modules")]
    [InlineData(
        "Error: You installed esbuild for another platform than the one you're currently using.",
        "esbuild was installed for another platform")]
    [InlineData(
        "Error: Expected \"0.21.5\" but got \"0.19.2\": Host version \"0.21.5\" does not match binary version \"0.19.2\"",
        "the installed esbuild binary does not match its host version")]
    [InlineData(
        "Error: Failed to load native binding\n    at Object.<anonymous> (/repo/node_modules/rollup/dist/native.js:64:9)",
        "a native toolchain binding failed to load")]
    public void KnownToolchainSignatures_AreGateEnvironment(string output, string expectedReason)
    {
        var diagnosis = GateEnvironmentFailurePolicy.Diagnose(output);

        Assert.True(diagnosis.IsEnvironmentFailure);
        Assert.Equal(expectedReason, diagnosis.Reason);
        Assert.Equal(
            BuildTestGateFailureKind.GateEnvironment,
            BuildTestGateRunner.ClassifyFailure(Evidence(stderr: output)));
    }

    [Fact]
    public void UnnamedCrashInsideAnInstalledTool_IsStillGateEnvironment()
    {
        var output = "TypeError: Cannot read properties of undefined\n"
                     + "    at build (/repo/node_modules/typescript/lib/tsc.js:112:7)";

        var diagnosis = GateEnvironmentFailurePolicy.Diagnose(output);

        Assert.True(diagnosis.IsEnvironmentFailure);
        Assert.Contains("typescript", diagnosis.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FailingSuite_StaysCode_EvenWhenTheStackMentionsNodeModules()
    {
        // The whole point of the two-sided rule: vitest ran, so the failure is
        // the product's own result no matter which frames the stack shows.
        var output = """
            RUN  v1.6.0 /repo/frontend
             ❯ src/app/task.spec.ts (3 tests | 1 failed)
            AssertionError: expected 2 to be 3
                at /repo/node_modules/vitest/dist/chunks/runtime.js:44:11
             Test Files  1 failed (1)
            """;

        Assert.False(GateEnvironmentFailurePolicy.Diagnose(output).IsEnvironmentFailure);
        Assert.Equal(
            BuildTestGateFailureKind.Code,
            BuildTestGateRunner.ClassifyFailure(Evidence(stdout: output)));
    }

    [Fact]
    public void TypeScriptDiagnostic_StaysCode()
    {
        // A compiler diagnostic is the product's own defect: no toolchain crash
        // signature, no stack frame inside an installed package.
        var output = "src/app/task.component.ts(42,9): error TS2322: "
                     + "Type 'string' is not assignable to type 'number'.";

        Assert.False(GateEnvironmentFailurePolicy.Diagnose(output).IsEnvironmentFailure);
        Assert.Equal(
            BuildTestGateFailureKind.Code,
            BuildTestGateRunner.ClassifyFailure(Evidence(stdout: output)));
    }

    [Fact]
    public void MissingProductModule_OutsideNodeModules_StaysCode()
    {
        var output = "Error: Cannot find module './services/deleted.service'";

        Assert.False(GateEnvironmentFailurePolicy.Diagnose(output).IsEnvironmentFailure);
    }

    [Fact]
    public void TimedOutProcess_KeepsItsOwnInfrastructureClass()
    {
        // A timeout is already an honest infrastructure class with its own retry
        // budget; the environment probe must not relabel it.
        var kind = BuildTestGateRunner.ClassifyFailure(
            Evidence(stdout: Cac18Transcript, exitCode: null, timedOut: true));

        Assert.Equal(BuildTestGateFailureKind.Timeout, kind);
    }

    [Fact]
    public void RecoveredGateReceipt_IsRecognizedFromItsReasonLine()
    {
        // A durable receipt replayed after a crash carries only the reason line,
        // so the prefix has to be enough to keep the classification.
        var replayed = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 10, string.Empty,
            "gate environment: vite case-insensitive FS probe failed; dependency-cache hit scope=.",
            false, true);

        Assert.True(GateEnvironmentFailurePolicy.IsGateEnvironmentFailure(replayed));
    }

    [Fact]
    public void CacheDecisionSummary_MakesAHitOnABrokenTreeVisible()
    {
        // Without the cache decision in the reason, a `hit` on a corrupted entry
        // is invisible on the card - which is why CAC-18 went unexplained.
        var summary = BuildTestGateRunner.CacheDecisionSummary(
        [
            new BuildTestGateDependencyCacheEvidence(".", "hit", "lock-unchanged", "abc", ["package-lock.json"], false),
        ]);

        Assert.Equal("dependency-cache hit scope=. reason=lock-unchanged", summary);
    }
}
