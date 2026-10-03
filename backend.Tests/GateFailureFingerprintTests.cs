using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2916 review finding (2026-09-29): gate-side fingerprinting hashed raw
/// output with timings and per-run cache paths, so a reproducible build or lint
/// gate failure never matched its clean repeat and was never charged as product.
/// </summary>
public sealed class GateFailureFingerprintTests
{
    private static readonly string Lease = Path.Combine(
        BuildTestGateRunner.ReviewWorkspaceRoot, "3f2c9e1d4b5a46f7a8b9c0d1e2f3a4b5");
    private static readonly string CleanClone = Path.Combine(
        BuildTestGateRunner.ReviewWorkspaceRoot, "clean-diagnostic-9a8b7c6d5e4f40312a1b2c3d4e5f6a7b");
    private static readonly string LeaseCache = BuildTestGateRunner.PreparationCacheRoot;
    private static readonly string CleanCache = Path.Combine(
        BuildTestGateRunner.PreparationCacheRoot, "diagnostics", "0f1e2d3c4b5a49687766554433221100");

    [Fact]
    public void Reproduced_build_failure_differing_only_in_timings_and_paths_has_one_fingerprint_and_classifies_as_product()
    {
        var first = RedGate(Lease, "dotnet build backend",
            $"{Lease}/backend/Orders.cs(12,5): error CS0103: The name 'total' does not exist in the current context [{Lease}/backend/Api.csproj]\n" +
            $"  Restored {LeaseCache}/entries/nuget/5d41402abc4b2a76/project.assets.json (in 212 ms).\n" +
            "[10:14:03 INF] build started 2026-09-29T10:14:03.118Z on http://localhost:41873\n" +
            "Build FAILED.\n" +
            "Time Elapsed 00:00:04.12\n",
            $"MSBUILD : error MSB1009: Project file {Lease}/tmp/7c1e2a90-3d4b-4f5a-9e8d-1c2b3a4d5e6f.rsp failed after 4.1s\n");
        var clean = RedGate(CleanClone, "dotnet build backend",
            $"{CleanClone}/backend/Orders.cs(12,5): error CS0103: The name 'total' does not exist in the current context [{CleanClone}/backend/Api.csproj]\n" +
            $"  Determining projects to restore...\n" +
            $"  Restored {CleanCache}/entries/nuget/9e107d9d372bb682/project.assets.json (in 18.4 sec).\n" +
            "[10:31:47 INF] build started 2026-09-29T10:31:47.902Z on http://localhost:52011\n" +
            "Build FAILED.\n" +
            "Time Elapsed 00:00:37.80\n",
            $"MSBUILD : error MSB1009: Project file {CleanClone}/tmp/0b9a8c7d-6e5f-4a3b-8c2d-1e0f9a8b7c6d.rsp failed after 812 ms\n");

        var firstFingerprint = BuildTestGateRunner.DiagnosticFingerprint(first);
        var cleanFingerprint = BuildTestGateRunner.DiagnosticFingerprint(clean);

        Assert.Equal(firstFingerprint, cleanFingerprint);
        var diagnosis = DeliveryFailureDiagnosis.Classify(new DeliveryFailureDiagnosisInput(
            firstFingerprint, BaselinePassed: true, BaselineFingerprint: null,
            CleanRepeatPassed: false, CleanRepeatFingerprint: cleanFingerprint));
        Assert.Equal(DeliveryFailureDiagnosis.Product, diagnosis.Classification);
        Assert.True(diagnosis.ChargesCard);
    }

    [Fact]
    public void Reproduced_lint_failure_in_the_canonical_checkout_matches_its_clean_clone()
    {
        const string checkout = "/srv/projects/shop";
        var first = RedGate(checkout + "/frontend", "npm run lint",
            $"{checkout}/frontend/src/app/cart.ts\n  14:7  error  'unused' is assigned a value but never used  no-unused-vars\n" +
            "✖ 1 problem (1 error, 0 warnings)\nDone in 3.42s.\n", "");
        var clean = RedGate(CleanClone + "/frontend", "npm run lint",
            $"{CleanClone}/frontend/src/app/cart.ts\n  14:7  error  'unused' is assigned a value but never used  no-unused-vars\n" +
            "✖ 1 problem (1 error, 0 warnings)\nDone in 11.9s.\n", "");

        Assert.Equal(
            BuildTestGateRunner.DiagnosticFingerprint(first, checkout),
            BuildTestGateRunner.DiagnosticFingerprint(clean));
    }

    [Fact]
    public void A_different_build_error_keeps_a_different_fingerprint()
    {
        var first = RedGate(Lease, "dotnet build backend",
            $"{Lease}/backend/Orders.cs(12,5): error CS0103: The name 'total' does not exist\n", "");
        var other = RedGate(CleanClone, "dotnet build backend",
            $"{CleanClone}/backend/Orders.cs(12,5): error CS1002: ; expected\n", "");

        Assert.NotEqual(
            BuildTestGateRunner.DiagnosticFingerprint(first),
            BuildTestGateRunner.DiagnosticFingerprint(other));
    }

    [Fact]
    public void Gate_and_review_share_one_output_normaliser()
    {
        var identity = FailureOutputNormalizer.Identity(
            "warming up in 3.2s\nerror: listen EADDRINUSE 127.0.0.1:4711 at 12:01:02 (7c1e2a90-3d4b-4f5a-9e8d-1c2b3a4d5e6f)\n", 1);

        Assert.Equal("error: listen eaddrinuse 127.0.0.1:<port> at <time> (<id>)", identity);
        Assert.Equal("exit:2", FailureOutputNormalizer.Identity("compiled in 3.2s\n", 2));
    }

    private static BuildTestGateResult RedGate(string workingDirectory, string command, string stdout, string stderr)
        => new(BuildTestGateVerdict.Fail, 1, 4_000, stdout, "verification failed", true, false)
        {
            Processes =
            [
                new BuildTestGateProcessEvidence
                {
                    Phase = "verification",
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    ExitCode = 1,
                    StandardOutput = stdout,
                    StandardError = stderr,
                },
            ],
        };
}
