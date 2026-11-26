extern alias Runner;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using RunnerHost = Runner::AgentRunner;

namespace AgentStudio.Tests;

public sealed class TestEnvelopeParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "test-envelope-parity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Review_gate_and_post_merge_report_the_same_allowlist_and_resource_shape()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = new FakeTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(Path.Combine(seed, ".agent-studio"));
        File.WriteAllText(Path.Combine(seed, ".agent-studio/project.yml"), """
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
                - sh probe.sh
              lint:
            testSuites:
            cachePaths:
            capabilities: [git]
            environment:
              PROJECT_VALUE: fixture
            """);
        File.WriteAllText(Path.Combine(seed, ".agent-studio/prepare"), "#!/bin/sh\ntrue\n");
        File.SetUnixFileMode(Path.Combine(seed, ".agent-studio/prepare"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(seed, "probe.sh"), """
            set -eu
            test -z "${RUNNER_SERVER_URL+x}"
            test "$HOME" = "$TEST_ENVELOPE_HOME"
            test "$TMPDIR" = "$TEST_ENVELOPE_TEMP"
            test -n "$DOTNET_PROCESSOR_COUNT"
            printf 'processor=%s cgroup=%s\n' "$DOTNET_PROCESSOR_COUNT" "$TEST_ENVELOPE_CGROUP"
            cat /proc/self/cgroup
            """);
        await Git(seed, "init", "-b", "main");
        await Git(seed, "add", ".");
        await Git(seed, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
        var sha = (await Git(seed, "rev-parse", "HEAD")).Trim();
        var command = new ReviewCommandDto("verify", "build-tests", "sh", ["probe.sh"]);
        var subject = new AgentStudio.TaskServer.Contracts.ReviewSubjectDto("subject", "task", "run", RunnerHost.TaskServerClient.RepositoryIdentity(seed)!, seed, sha, "refs/heads/main",
            null, null, "coding", "policy", new([command], ["build-tests"]), now);
        var lease = new ReviewLeaseDto("lease", "attempt", "subject", "review", "instance", "host", 1,
            now, now.AddMinutes(2), "active", "parity", 26550);
        var options = new RunnerHost.RunnerOptions { ServerUrl = "http://fixture/", RunnerId = "review", RunnerName = "review",
            Hostname = "host", BackendName = "remote-review", Role = "review", WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = Path.Combine(_root, "review"), ReviewTempBase = Path.Combine(_root, "temp"), BaseBranch = "main",
            ClaudeCliBin = "unused", CodexCliBin = "unused", TtlSeconds = 120, HeartbeatSeconds = 30 };
        var workspace = new RunnerHost.RemoteReviewWorkspace(options, subject, lease, _ => { }, utcNow: () => clock.GetUtcNow().UtcDateTime);
        await workspace.PrepareAsync(null!, default);
        var review = await workspace.ExecutePlanAsync(default);
        Assert.Equal("Pass", review.Outcome);
        var reviewEnvelope = workspace.EnvironmentEvidence().Envelope!;
        Assert.NotNull(reviewEnvelope);

        var gate = new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest, Path.Combine(_root, "cache"));
        var gateResult = await gate.RunAsync(new(seed, null, "fixture", RequireExactSubject: false), null, null,
            PostStepMode.Fail, TimeSpan.FromMinutes(2), default);
        Assert.Equal(BuildTestGateVerdict.Ok, gateResult.Verdict);
        var gateEnvelope = gateResult.Processes.Last().Envelope!;
        Assert.NotNull(gateEnvelope);

        using var http = new HttpClient();
        var postMerge = new RunnerHost.PostMergeRunCommand(new("fixture", seed, "http://fixture/", "main", "origin", "unused",
            "window", 1, TimeSpan.Zero, 1, [], false), TextWriter.Null, clock, http);
        var postMergeResult = await postMerge.RunCommand("sh probe.sh", new Dictionary<string, string>
            { ["PROJECT_VALUE"] = "fixture" }, default);
        Assert.Equal(0, postMergeResult.ExitCode);
        Assert.True(TestEnvelope.MatchesReference(reviewEnvelope, gateEnvelope));
        Assert.True(TestEnvelope.MatchesReference(gateEnvelope, postMerge.LastCommandEnvelope!));
        await workspace.CleanupAsync();
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var result = await RunnerHost.ProcessRunner.RunAsync("git", arguments, directory);
        Assert.True(result.Success, result.StdErr);
        return result.StdOut;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
