using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteTaskRunnerProviderAuthTests
{
    private const string Refusal =
        "usage limit reached; resets at 2026-09-19T12:40:00Z";

    [Theory]
    [InlineData(137, null, false, ProviderAuthProbe.Limited)]
    [InlineData(137, 9, false, ProviderAuthProbe.Ready)]
    [InlineData(137, null, true, ProviderAuthProbe.Ready)]
    [InlineData(1, null, false, ProviderAuthProbe.Limited)]
    public async Task Production_result_adapter_uses_recorded_termination_facts_only(
        int exitCode,
        int? recordedSignal,
        bool operatorStopped,
        string expectedStatus)
    {
        var probe = Probe();
        await probe.RefreshAsync("claude", CancellationToken.None);
        var facts = Facts(exitCode, recordedSignal);

        var status = RemoteTaskRunner.RecordProviderProcessResult(
            probe,
            "claude",
            new ProcessResult(exitCode, string.Empty, Refusal),
            facts,
            evidenceId: "run-provider-evidence",
            stopDirectiveRecorded: operatorStopped);

        Assert.Equal(expectedStatus, status.Status);
        if (expectedStatus == ProviderAuthProbe.Limited)
        {
            Assert.Equal("run-provider-evidence", status.EvidenceId);
            Assert.Equal(
                new DateTimeOffset(2026, 9, 19, 12, 40, 0, TimeSpan.Zero),
                status.LimitedUntil);
        }
        else
        {
            Assert.Null(status.EvidenceId);
            Assert.Null(status.LimitedUntil);
        }
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Production_process_wait_distinguishes_signals_from_matching_exit_codes()
    {
        PlatformGate.LinuxOnly("the production signal record uses Unix wait semantics");
        const string refusal = "usage limit reached; resets at 2099-09-19T12:40:00Z";
        var root = Path.Combine(Path.GetTempPath(), "runner-provider-auth-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var killedResult = await RunDurableAsync(
                Path.Combine(root, "killed"),
                $"printf '%s\\n' '{refusal}' >&2; exec /bin/kill -TERM $$");
            var sigkillResult = await RunDurableAsync(
                Path.Combine(root, "sigkill"),
                $"printf '%s\\n' '{refusal}' >&2; exec /bin/kill -KILL $$");
            var exit137Result = await RunDurableAsync(
                Path.Combine(root, "exit-137"),
                $"printf '%s\\n' '{refusal}' >&2; exit 137");
            var normalResult = await RunDurableAsync(
                Path.Combine(root, "normal"),
                $"printf '%s\\n' '{refusal}' >&2; exit 1");
            var killed = RemoteTaskRunner.ProcessResultFrom(killedResult);
            var sigkill = RemoteTaskRunner.ProcessResultFrom(sigkillResult);
            var exit137 = RemoteTaskRunner.ProcessResultFrom(exit137Result);
            var normal = RemoteTaskRunner.ProcessResultFrom(normalResult);
            var (lease, workspace) = ProductionContext(root);

            var killedFacts = RemoteTaskRunner.BuildProcessFacts(lease, workspace, killed);
            var sigkillFacts = RemoteTaskRunner.BuildProcessFacts(lease, workspace, sigkill);
            var exit137Facts = RemoteTaskRunner.BuildProcessFacts(lease, workspace, exit137);
            var normalFacts = RemoteTaskRunner.BuildProcessFacts(lease, workspace, normal);
            Assert.Equal(15, killedResult.Signal);
            Assert.Equal(15, killed.Signal);
            Assert.Equal(15, killedFacts.Signal);
            Assert.Equal(9, sigkillResult.Signal);
            Assert.Equal(9, sigkill.Signal);
            Assert.Equal(9, sigkillFacts.Signal);
            Assert.Equal(137, exit137Result.ExitCode);
            Assert.Null(exit137Result.Signal);
            Assert.Null(exit137.Signal);
            Assert.Null(exit137Facts.Signal);
            Assert.Null(normalResult.Signal);
            Assert.Null(normal.Signal);
            Assert.Null(normalFacts.Signal);

            var probe = Probe();
            await probe.RefreshAsync("claude", CancellationToken.None);
            var afterKilled = RemoteTaskRunner.RecordProviderProcessResult(
                probe,
                "claude",
                killed,
                killedFacts,
                evidenceId: "run-killed");
            Assert.Equal(ProviderAuthProbe.Ready, afterKilled.Status);

            var afterSigkill = RemoteTaskRunner.RecordProviderProcessResult(
                probe,
                "claude",
                sigkill,
                sigkillFacts,
                evidenceId: "run-sigkill");
            Assert.Equal(ProviderAuthProbe.Ready, afterSigkill.Status);

            var afterExit137 = RemoteTaskRunner.RecordProviderProcessResult(
                probe,
                "claude",
                exit137,
                exit137Facts,
                evidenceId: "run-exit-137");
            Assert.Equal(ProviderAuthProbe.Limited, afterExit137.Status);
            Assert.Equal("run-exit-137", afterExit137.EvidenceId);

            await probe.RefreshAsync("claude", CancellationToken.None);
            var afterNormal = RemoteTaskRunner.RecordProviderProcessResult(
                probe,
                "claude",
                normal,
                normalFacts,
                evidenceId: "run-normal");
            Assert.Equal(ProviderAuthProbe.Limited, afterNormal.Status);
            Assert.Equal("run-normal", afterNormal.EvidenceId);
        }
        finally
        {
            ResilientDirectory.TryDelete(root);
        }
    }

    private static ProviderAuthProbe Probe()
        => new(
            launcher: (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", string.Empty)),
            executableExists: _ => true,
            credentialFreshness: _ => new ProviderCredentialFreshness(
                null,
                null,
                "Credential metadata fixture has no expiry."));

    private static ExecutionRawFacts Facts(int exitCode, int? signal)
        => new(
            "attempt-provider-auth",
            ExecutionAttemptKind.Coding,
            StdErr: Refusal,
            ExitCode: exitCode,
            Signal: signal,
            DurableOutputState: DurableOutputState.LocalOnly);

    private static async Task<DetachedJobResult> RunDurableAsync(string directory, string command)
    {
        Directory.CreateDirectory(directory);
        var results = Path.Combine(directory, "results");
        Directory.CreateDirectory(results);
        var specPath = Path.Combine(directory, "spec.json");
        var spec = new DetachedJobSpec(
            PosixShell.RequirePath(),
            ["-c", command],
            directory,
            string.Empty,
            results,
            TimeoutSeconds: 10,
            Engine: RunnerOptions.ExecEngineLegacy);
        await File.WriteAllTextAsync(
            specPath,
            JsonSerializer.Serialize(spec, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await DurableAgentProcess.RunWorkerAsync(specPath);
        return JsonSerializer.Deserialize<DetachedJobResult>(
                   await File.ReadAllTextAsync(Path.Combine(directory, "result.json")),
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException("The durable worker result was empty.");
    }

    private static (RunLeaseInfoDto Lease, GitWorkspace Workspace) ProductionContext(string root)
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner-provider-auth-test",
            RunnerName = "runner-provider-auth-test",
            Hostname = "test-host",
            BackendName = "test",
            WorkDir = root,
            BaseBranch = "develop",
            CliBin = PosixShell.RequirePath(),
            CliArgs = string.Empty,
        };
        var lease = new RunLeaseInfoDto(
            "AGT-PROVIDER-AUTH",
            options.RunnerId,
            options.RunnerId,
            "test-host",
            Environment.ProcessId,
            "test",
            "lease-provider-auth",
            1,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            AttemptId: "attempt-provider-auth");
        return (lease, new GitWorkspace(options, lease.TaskKey, _ => { }));
    }
}
