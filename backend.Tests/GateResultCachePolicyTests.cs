using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateResultCachePolicyTests
{
    [Fact]
    public void Local_toolchain_identity_changes_when_selected_sdk_changes_without_host_binary_change()
    {
        var repository = Path.GetFullPath(Path.GetTempPath());
        var commands = new[]
        {
            new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"),
        };
        string Identity(string sdk) => GateResultCache.LocalToolchainIdentity(
            commands, repository,
            (executable, directory, argument) =>
            {
                Assert.Equal("--version", argument);
                Assert.Equal(repository, directory);
                return Path.GetFileName(executable) == "dotnet" ? sdk : "unchanged";
            });

        Assert.NotEqual(Identity("10.0.301"), Identity("10.0.302"));
    }

    [Fact]
    public void Profile_digest_changes_with_profile_definition_and_toolchain()
    {
        var request = new BuildTestGateRequest("/repo", new string('a', 40), "test");
        var command = new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test");
        var profile = new BuildProfile { BuildCmds = ["dotnet build"] };
        string Digest(BuildTestGateRequest selected, BuildProfile candidate, string toolchain) =>
            GateResultCache.ProfileDigest(selected, candidate, PostStepMode.Fail,
                ["src/File.cs"], [command], toolchain);

        var original = Digest(request, profile, "toolchain-1");
        Assert.NotEqual(original, Digest(request, profile with { BuildCmds = ["dotnet build --no-restore"] }, "toolchain-1"));
        Assert.NotEqual(original, Digest(request with { PipelineDefinitionVersion = 2 }, profile, "toolchain-1"));
        Assert.NotEqual(original, Digest(request, profile, "toolchain-2"));
        Assert.NotEqual(original, Digest(request with
        {
            ChangedFileStatuses = new Dictionary<string, string> { ["src/File.cs"] = "deleted" },
        }, profile, "toolchain-1"));
    }

    [Fact]
    public void Cached_pass_is_a_distinct_step_verdict_with_original_evidence()
    {
        var originalTime = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        var original = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 6000, "", "passed", true, false)
        {
            GateCompletedAtUtc = originalTime,
            OriginEvidencePath = "/evidence/original.json",
        };
        var now = DateTime.UtcNow;
        var executed = BuildTestGateStepProjection.Create(
            PipelineStepStatus.Passed, original.DurationMs, "ok", "passed", original, now);
        var cached = BuildTestGateStepProjection.Create(
            PipelineStepStatus.Passed, original.DurationMs, "ok", "passed",
            original with { VerdictSource = GateVerdictSource.CacheHit }, now);

        Assert.Equal("ok", executed.Verdict);
        Assert.Equal(GateVerdictSource.Executed, executed.GateVerdictSource);
        Assert.Equal("cached-ok", cached.Verdict);
        Assert.Equal(GateVerdictSource.CacheHit, cached.GateVerdictSource);
        Assert.Equal(0, cached.DurationMs);
        Assert.Equal(originalTime, cached.GateOriginCompletedAtUtc);
        Assert.Equal(original.OriginEvidencePath, cached.GateOriginEvidencePath);
    }
}
