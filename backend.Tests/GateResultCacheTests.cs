using System.Diagnostics;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class GateResultCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-verdict-test-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private string Counter => Path.Combine(_root, "runs.txt");
    private string CacheRoot => Path.Combine(_root, "cache");
    private readonly string _sha;

    public GateResultCacheTests()
    {
        Directory.CreateDirectory(Repo);
        Git("init");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "exact subject\n");
        Git("add README.md");
        Git("-c user.name=Test -c user.email=test@example.org commit -m initial");
        _sha = Git("rev-parse HEAD").Trim();
    }

    [Fact]
    public async Task Same_sha_and_profile_reuses_original_verdict_and_evidence()
    {
        var first = await Run();
        var second = await Run();

        Assert.Equal(BuildTestGateVerdict.Ok, second.Verdict);
        Assert.Equal(GateVerdictSource.Executed, first.VerdictSource);
        Assert.Equal(GateVerdictSource.CacheHit, second.VerdictSource);
        Assert.Equal(first.GateRunId, second.GateRunId);
        Assert.Equal(first.GateCompletedAtUtc, second.GateCompletedAtUtc);
        Assert.Equal(first.PipelineDefinitionVersion, second.PipelineDefinitionVersion);
        Assert.Equal(first.ToolchainIdentity, second.ToolchainIdentity);
        Assert.Equal(first.OriginEvidencePath, second.OriginEvidencePath);
        Assert.True(File.Exists(second.OriginEvidencePath));
        Assert.Single(File.ReadAllLines(Counter));
        var report = new GateResultCache(Path.Combine(CacheRoot, "gate-results")).Report("test-project");
        Assert.Equal(1, report.Executed);
        Assert.Equal(1, report.CacheHits);
    }

    [Fact]
    public async Task Profile_pipeline_version_and_toolchain_changes_all_miss()
    {
        await Run();
        Assert.Equal(GateVerdictSource.Executed,
            (await Run(profile: new BuildProfile { Stack = "other", BuildCmds = [Command] })).VerdictSource);
        Assert.Equal(GateVerdictSource.Executed,
            (await Run(version: 2)).VerdictSource);
        Assert.Equal(GateVerdictSource.Executed,
            (await Run(toolchain: "different-toolchain")).VerdictSource);
        Assert.Equal(4, File.ReadAllLines(Counter).Length);
        var report = new GateResultCache(Path.Combine(CacheRoot, "gate-results")).Report("test-project");
        Assert.Equal(4, report.Executed);
        Assert.Equal(3, report.SameShaRetests);
        Assert.Equal(0.75, report.SameShaRetestRate);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_key_execute_once()
    {
        var first = Run();
        var second = Run();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.VerdictSource == GateVerdictSource.Executed);
        Assert.Single(results, result => result.VerdictSource == GateVerdictSource.CacheHit);
        Assert.Single(File.ReadAllLines(Counter));
    }

    [Fact]
    public async Task Cached_pass_has_distinct_step_classification_and_original_time()
    {
        var first = await Run();
        var cached = await Run();
        var now = DateTime.UtcNow;
        var executedStep = BuildTestGateStepProjection.Create(
            PipelineStepStatus.Passed, first.DurationMs, "ok", first.Reason, first, now);
        var cachedStep = BuildTestGateStepProjection.Create(
            PipelineStepStatus.Passed, cached.DurationMs, "ok", cached.Reason, cached, now);

        Assert.Equal(GateVerdictSource.Executed, executedStep.GateVerdictSource);
        Assert.Equal(GateVerdictSource.CacheHit, cachedStep.GateVerdictSource);
        Assert.Equal(0, cachedStep.DurationMs);
        Assert.Equal("ok", executedStep.Verdict);
        Assert.Equal("cached-ok", cachedStep.Verdict);
        Assert.Equal(first.GateCompletedAtUtc, cachedStep.GateOriginCompletedAtUtc);
        Assert.Equal(first.OriginEvidencePath, cachedStep.GateOriginEvidencePath);
    }

    [Fact]
    public async Task Operator_invalidation_forces_a_fresh_execution()
    {
        await Run();
        var cache = new GateResultCache(Path.Combine(CacheRoot, "gate-results"));
        await cache.InvalidateAsync("test-project");
        var result = await Run();

        Assert.Equal(GateVerdictSource.Executed, result.VerdictSource);
        Assert.Equal(2, File.ReadAllLines(Counter).Length);
    }

    [Fact]
    public async Task Integration_receipt_recovery_keeps_cache_provenance()
    {
        await Run();
        var cached = await Run();
        var folder = Path.Combine(_root, "task");
        IntegrationGateReceipts.Record(folder, "pre-main-test-gate", cached);

        var recovered = IntegrationGateReceipts.ReadExact(folder, "pre-main-test-gate", _sha);
        Assert.NotNull(recovered);
        Assert.Equal(GateVerdictSource.CacheHit, recovered.VerdictSource);
        Assert.Equal(cached.GateCompletedAtUtc, recovered.GateCompletedAtUtc);
        Assert.Equal(cached.OriginEvidencePath, recovered.OriginEvidencePath);
        Assert.Equal(cached.PipelineDefinitionVersion, recovered.PipelineDefinitionVersion);
    }

    private string Command => $"echo x >> {Counter}";

    private Task<BuildTestGateResult> Run(
        BuildProfile? profile = null, int version = 1, string? toolchain = null)
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            CacheRoot);
        return runner.RunAsync(new BuildTestGateRequest(Repo, _sha, "test")
        {
            Project = "test-project",
            PipelineDefinitionVersion = version,
            ToolchainIdentity = toolchain,
        }, null, profile ?? new BuildProfile { BuildCmds = [Command] },
            PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None);
    }

    private string Git(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = Repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temporary test files */ }
    }
}
