using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2720 - CAC-18 passed remote review 412 times and failed every pre-main
/// full suite on the Windows studio with an exit-1 frame inside
/// <c>node_modules/vite/dist/node/chunks/config.js</c>, before a single vitest
/// test ran. The gate called that a product failure and the card was left
/// <c>partial</c>. These tests pin the matrix that separates such a toolchain
/// death from a red suite, and the bound that keeps the classification from
/// becoming an endless retry.
/// </summary>
public sealed class GateEnvironmentFailureTests
{
    /// <summary>The transcript tail the studio gate recorded on 2026-09-02.</summary>
    private const string Cac18Transcript = """
        # verify plan: build-profile (2 command(s))
        # dependency-cache hit scope=frontend reason=lock-unchanged lockHash=9f21c0
        # working directory: C:\Users\studio\AppData\Local\Temp\agentstudio-review-gates\a17\frontend
        [stderr] failed to load config from C:\...\vite.config.ts
        [stderr] Error: ENOENT: no such file or directory
        [stderr]     at testCaseInsensitiveFS (C:\...\node_modules\vite\dist\node\chunks\config.js:1911:42)
        [stderr]     at loadConfigFromFile (C:\...\node_modules\vite\dist\node\chunks\config.js:2044:7)
        """;

    private static BuildTestGateProcessEvidence Evidence(string stderr, string stdout = "")
        => new()
        {
            Command = "npm test",
            FileName = "cmd.exe",
            ExitCode = 1,
            StandardOutput = stdout,
            StandardError = stderr,
        };

    private static IReadOnlyList<GateDependencyCacheDecision> ServedFromCache()
        => [new GateDependencyCacheDecision("frontend", ServedFromCache: true)];

    private static IReadOnlyList<GateDependencyCacheDecision> InstalledFresh()
        => [new GateDependencyCacheDecision("frontend", ServedFromCache: false)];

    [Theory]
    [InlineData(@"at testCaseInsensitiveFS (C:\repo\node_modules\vite\dist\node\chunks\config.js:1911:42)")]
    [InlineData("Error: Build failed with 1 error:\n  at /repo/node_modules/esbuild/lib/main.js:1604:15")]
    [InlineData("Cannot read file '/repo/node_modules/typescript/lib/lib.dom.d.ts'.")]
    [InlineData("npm error The `npm ci` command can only install with an existing package-lock.json")]
    public void Toolchain_death_on_a_cached_tree_is_a_gate_environment_failure(string stderr)
    {
        var kind = BuildTestGateRunner.ClassifyFailure(Evidence(stderr), ServedFromCache());

        Assert.Equal(BuildTestGateFailureKind.GateEnvironment, kind);
    }

    [Fact]
    public void The_cac18_transcript_reproduces_the_environment_classification()
    {
        var kind = BuildTestGateRunner.ClassifyFailure(
            Evidence(stderr: Cac18Transcript), ServedFromCache());

        Assert.Equal(BuildTestGateFailureKind.GateEnvironment, kind);
        Assert.True(
            new BuildTestGateResult(
                BuildTestGateVerdict.Fail, 1, 0, "", "vite failed", false, false)
            {
                FailureKind = kind,
            }.IsInfrastructureFailure,
            "a gate that never reached the first test must not be a product verdict");
    }

    [Fact]
    public void The_same_toolchain_frame_after_a_fresh_install_is_a_product_failure()
    {
        // The bound: once the scope installed its own dependencies, the gate has
        // nothing left to repair and the frame is the delivery's own problem. One
        // corruption therefore costs exactly one extra attempt.
        var kind = BuildTestGateRunner.ClassifyFailure(
            Evidence(stderr: Cac18Transcript), InstalledFresh());

        Assert.Equal(BuildTestGateFailureKind.Code, kind);
    }

    [Fact]
    public void A_test_that_fails_through_a_bundler_frame_stays_a_product_failure()
    {
        var evidence = Evidence(
            stderr: @"at transform (/repo/node_modules/vite/dist/node/chunks/config.js:44:1)",
            stdout: " Test Files  1 failed (12)\n Tests  1 failed | 41 passed (42)");

        var kind = BuildTestGateRunner.ClassifyFailure(evidence, ServedFromCache());

        Assert.Equal(BuildTestGateFailureKind.Code, kind);
    }

    [Fact]
    public void A_plain_assertion_failure_is_never_an_environment_failure()
    {
        var evidence = Evidence(
            stderr: "AssertionError: expected 3 to be 4",
            stdout: " Tests  1 failed | 0 passed (1)");

        var kind = BuildTestGateRunner.ClassifyFailure(evidence, ServedFromCache());

        Assert.Equal(BuildTestGateFailureKind.Code, kind);
    }

    [Fact]
    public void A_timeout_keeps_its_own_classification()
    {
        var evidence = new BuildTestGateProcessEvidence
        {
            Command = "npm test",
            FileName = "cmd.exe",
            ExitCode = null,
            StandardError = Cac18Transcript,
            TimedOut = true,
        };

        var kind = BuildTestGateRunner.ClassifyFailure(evidence, ServedFromCache());

        Assert.Equal(BuildTestGateFailureKind.Timeout, kind);
    }

    [Fact]
    public void Only_the_scopes_that_were_served_from_cache_are_evicted()
    {
        IReadOnlyList<GateDependencyCacheDecision> decisions =
        [
            new("frontend", ServedFromCache: true),
            new("tools", ServedFromCache: false),
        ];

        Assert.Equal(["frontend"], GateEnvironmentFailurePolicy.ScopesToEvict(decisions));
    }
}
