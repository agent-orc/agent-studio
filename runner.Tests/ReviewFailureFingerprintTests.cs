using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2916 review finding (2026-09-29): the raw-output fingerprint of a build
/// or lint failure kept the clean repeat clone's random suffix, the runtime
/// temp directory and timings, so a reproducible failure never matched itself.
/// </summary>
public sealed class ReviewFailureFingerprintTests
{
    private const string Root = "/srv/review/attempt-7";

    [Fact]
    public void Build_error_in_the_candidate_and_the_clean_repeat_clone_has_one_fingerprint()
    {
        var first = new ProcessResult(1,
            $"{Root}/repository/src/App.cs(3,7): error CS0103: The name 'x' does not exist [{Root}/repository/src/App.csproj]\n" +
            "Time Elapsed 00:00:04.12\n",
            "");
        var clean = new ProcessResult(1,
            $"{Root}/clean-repeat-0123456789ab-3f2c9e1d4b5a46f7a8b9c0d1e2f3a4b5/src/App.cs(3,7): error CS0103: The name 'x' does not exist " +
            $"[{Root}/clean-repeat-0123456789ab-3f2c9e1d4b5a46f7a8b9c0d1e2f3a4b5/src/App.csproj]\n" +
            "Time Elapsed 00:00:09.87\n",
            "");

        Assert.Equal(
            ReviewFailureFingerprint.Compute(Root, "verify-1", [], first),
            ReviewFailureFingerprint.Compute(Root, "verify-1", [], clean));
    }

    [Fact]
    public void Runtime_temp_home_and_cache_paths_and_timings_do_not_change_the_fingerprint()
    {
        var first = new ProcessResult(1,
            $"error: lint cache {Root}/tmp/eslint-4711 failed after 1.23s at 2026-09-29T00:20:42.811Z\n", "");
        var clean = new ProcessResult(1,
            $"error: lint cache {Root}/baseline-runtime-0123456789ab/tmp/eslint-4711 failed after 812 ms at 2026-09-29T00:21:07.004Z\n", "");

        Assert.Equal(
            ReviewFailureFingerprint.Compute(Root, "verify-7", [], first),
            ReviewFailureFingerprint.Compute(Root, "verify-7", [], clean));
    }

    [Fact]
    public void Output_without_a_diagnostic_line_falls_back_to_the_exit_status()
    {
        var first = new ProcessResult(2, $"Building {Root}/repository took 4711ms\n", "");
        var clean = new ProcessResult(2, "Building elsewhere took 12ms\n", "warming up");
        var otherExit = new ProcessResult(3, "Building elsewhere took 12ms\n", "");

        Assert.Equal("exit:2", ReviewFailureFingerprint.OutputIdentity(Root, first));
        Assert.Equal(
            ReviewFailureFingerprint.Compute(Root, "verify-1", [], first),
            ReviewFailureFingerprint.Compute(Root, "verify-1", [], clean));
        Assert.NotEqual(
            ReviewFailureFingerprint.Compute(Root, "verify-1", [], first),
            ReviewFailureFingerprint.Compute(Root, "verify-1", [], otherExit));
    }

    [Fact]
    public void Different_diagnostics_keep_different_fingerprints()
    {
        var first = new ProcessResult(1, $"{Root}/repository/a.ts:3:7 error no-unused-vars\n", "");
        var other = new ProcessResult(1, $"{Root}/repository/a.ts:3:7 error no-undef\n", "");

        Assert.NotEqual(
            ReviewFailureFingerprint.Compute(Root, "verify-7", [], first),
            ReviewFailureFingerprint.Compute(Root, "verify-7", [], other));
    }

    [Fact]
    public void Parsed_test_names_stay_the_identity_regardless_of_output()
    {
        var first = new ProcessResult(1, "Failed Product.Test [1 ms]", "");
        var clean = new ProcessResult(1, "Failed Product.Test [900 ms] different noise error", "");

        Assert.Equal(
            ReviewFailureFingerprint.Compute(Root, "verify-2", ["Product.Test"], first),
            ReviewFailureFingerprint.Compute(Root, "verify-2", ["Product.Test"], clean));
        Assert.StartsWith(ReviewFailureFingerprint.Prefix,
            ReviewFailureFingerprint.Compute(Root, "verify-2", ["Product.Test"], first), StringComparison.Ordinal);
    }
}
