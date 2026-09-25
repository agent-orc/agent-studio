using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class BuildTestGateDiagnosisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-diagnosis-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(_root, "repo");

    [Fact]
    public async Task New_guard_failure_charges_only_after_green_baseline_and_red_clean_repeat()
    {
        Directory.CreateDirectory(Repository);
        Git("init");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Write(".agent-studio/project.yml", """
            schemaVersion: 1
            stack: [node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - sh .agent-studio/verify
              test:
              lint:
            testSuites:
            cachePaths:
            capabilities: [linux]
            environment:
            """);
        Write(".agent-studio/prepare", "#!/bin/sh\nexit 0\n");
        Write(".agent-studio/verify", "#!/bin/sh\nif [ -f broken.txt ]; then echo broken.txt >&2; exit 1; fi\n");
        Git("add", ".");
        Git("commit", "-m", "baseline");
        var baseline = Git("rev-parse", "HEAD");
        Write("broken.txt", "regression");
        Git("add", ".");
        Git("commit", "-m", "delivery");
        var delivery = Git("rev-parse", "HEAD");

        var runner = new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest, Path.Combine(_root, "cache"));
        var result = await runner.RunAsync(new BuildTestGateRequest(Repository, delivery, "test")
        {
            IntegrationRef = baseline,
            JobId = "card-1",
        }, null, null, PostStepMode.Fail, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(DeliveryFailureClass.Product, result.Diagnosis?.Class);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.Contains("baseline sha=", result.Output);
        Assert.Contains("uncached repeat verdict=Fail", result.Output);

        var shared = await runner.RunAsync(new BuildTestGateRequest(Repository, delivery, "test")
        {
            IntegrationRef = baseline,
            JobId = "card-2",
        }, null, null, PostStepMode.Fail, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(shared.Diagnosis?.Class == DeliveryFailureClass.Environment, shared.Output);
        Assert.Contains("cache=hit", shared.Output);

        var repeated = await runner.RunAsync(new BuildTestGateRequest(Repository, delivery, "test")
        {
            IntegrationRef = baseline,
            JobId = "card-3",
        }, null, null, PostStepMode.Fail, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(DeliveryFailureClass.Environment, repeated.Diagnosis?.Class);
        Assert.Contains("24h same fingerprint on other card=True", repeated.Output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* test directory */ }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }
}
