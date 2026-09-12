using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Process-backed restart harness for the local durable worker. Each test
/// creates a first Studio-side observer, drops it while the fake CLI is in
/// flight, then creates a fresh service over the same TaskRepository.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class LocalCliRestartHarnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "local-restart-harness-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _workerPids = [];

    public LocalCliRestartHarnessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WorkerStillRunning_NewServiceReattachesAndCompletesPostRunOnce()
    {
        var script = CreateFakeCli(delaySeconds: 2);
        using var observer = new CancellationTokenSource();
        var first = Service(script);
        var started = await first.StartAsync(
            "job-live", "workspace::job-live", "prompt", _root,
            executionEngine: CliExecutionEngines.Legacy, ct: observer.Token);
        Assert.NotNull(started.Execution);
        _workerPids.Add(started.Execution!.ProcessId);
        observer.Cancel();
        await Task.Delay(200);

        var replacement = Service(script);
        var completion = new TaskCompletionSource<CliExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRuns = 0;
        replacement.OnFinished += (_, execution) =>
        {
            Interlocked.Increment(ref postRuns);
            completion.TrySetResult(execution);
        };
        Assert.True(replacement.CanReattach("workspace::job-live"));
        replacement.ReattachOnStartup();

        var final = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, final.ExitCode);
        var workerJournals = Directory.EnumerateFiles(
                _root, LocalCliDurableWorker.OutputFileName, SearchOption.AllDirectories)
            .Select(path => path + "=" + File.ReadAllText(path))
            .ToArray();
        Assert.True(
            string.Equals(RunStatuses.Completed, final.Status, StringComparison.Ordinal),
            $"Expected completed but got {final.Status}/{final.RunOutcome}. Output: " +
            string.Join(" | ", replacement.GetOutput("workspace::job-live").Select(line => $"{line.Stream}:{line.Text}")) +
            ". Worker journals: " + string.Join(" | ", workerJournals));
        Assert.Equal(1, postRuns);
        Assert.Single(Directory.EnumerateFiles(
            _root, LocalCliDurableWorker.SpecFileName, SearchOption.AllDirectories));
        Assert.Contains(replacement.GetOutput("workspace::job-live"), line =>
            line.Text.Contains("Continuing after restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebApplicationHostRestart_ReattachesTheSameWorkerAndCompletesOnce()
    {
        var script = CreateFakeCli(delaySeconds: 5);
        using var observer = new CancellationTokenSource();
        var firstHost = HostFactory();
        CliExecution startedExecution;
        try
        {
            _ = firstHost.CreateClient();
            var first = firstHost.Services
                .GetRequiredKeyedService<GenericCliExecutionService>("gemini");
            first.SetCliPath(script);
            var started = await first.StartAsync(
                "job-host", "workspace::job-host", "prompt", _root,
                executionEngine: CliExecutionEngines.Legacy, ct: observer.Token);
            startedExecution = Assert.IsType<CliExecution>(started.Execution);
            _workerPids.Add(startedExecution.ProcessId);
            observer.Cancel();
            await Task.Delay(100);
        }
        finally
        {
            await firstHost.DisposeAsync();
        }

        await using var replacementHost = HostFactory();
        _ = replacementHost.CreateClient();
        var replacement = replacementHost.Services
            .GetRequiredKeyedService<GenericCliExecutionService>("gemini");
        var completion = new TaskCompletionSource<CliExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRuns = 0;
        replacement.OnFinished += (_, execution) =>
        {
            Interlocked.Increment(ref postRuns);
            completion.TrySetResult(execution);
        };

        await WaitUntilAsync(
            () => replacement.GetExecution("workspace::job-host")?.ContinuedAfterRestart == true,
            "replacement WebApplication did not adopt the durable worker");
        var final = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(startedExecution.ProcessId, final.ProcessId);
        Assert.Equal(RunStatuses.Completed, final.Status);
        Assert.True(final.ContinuedAfterRestart);
        Assert.Equal(1, postRuns);
        Assert.Single(Directory.EnumerateFiles(
            _root, LocalCliDurableWorker.SpecFileName, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WorkerFinishedDuringGap_ResultIsReplayedAndPostRunRunsOnce()
    {
        var script = CreateFakeCli(delaySeconds: 1);
        using var observer = new CancellationTokenSource();
        var first = Service(script);
        var started = await first.StartAsync(
            "job-finished", "workspace::job-finished", "prompt", _root,
            executionEngine: CliExecutionEngines.Legacy, ct: observer.Token);
        Assert.NotNull(started.Execution);
        _workerPids.Add(started.Execution!.ProcessId);
        observer.Cancel();
        await WaitForWorkerResultAsync();

        var replacement = Service(script);
        var completion = new TaskCompletionSource<CliExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRuns = 0;
        replacement.OnFinished += (_, execution) =>
        {
            Interlocked.Increment(ref postRuns);
            completion.TrySetResult(execution);
        };
        replacement.ReattachOnStartup();

        var final = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, final.ExitCode);
        Assert.Equal(1, postRuns);
        Assert.Single(Directory.EnumerateFiles(
            _root, LocalCliDurableWorker.SpecFileName, SearchOption.AllDirectories));
        await WaitUntilAsync(
            () => !replacement.CanReattach("workspace::job-finished"),
            "completed run remained reattachable after post-run handoff");
    }

    [Fact]
    public async Task WorkerDiedDuringGap_IsClassifiedAsRunLostAcrossRestart()
    {
        var script = CreateFakeCli(delaySeconds: 10);
        var jobFolder = Path.Combine(_root, TaskStates.Progress, "job-lost");
        Directory.CreateDirectory(jobFolder);
        using var observer = new CancellationTokenSource();
        var first = Service(script);
        var started = await first.StartAsync(
            "job-lost", "workspace::job-lost", "prompt", _root,
            jobFolderPath: jobFolder,
            executionEngine: CliExecutionEngines.Legacy, ct: observer.Token);
        Assert.NotNull(started.Execution);
        observer.Cancel();
        using (var worker = Process.GetProcessById(started.Execution!.ProcessId))
        {
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync();
        }

        var replacement = Service(script);
        replacement.ReattachOnStartup();

        Assert.False(replacement.CanReattach("workspace::job-lost"));
        Assert.Contains(replacement.GetOutput("workspace::job-lost"), line =>
            line.Text.Contains("run lost across restart", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "run lost across restart",
            File.ReadAllText(Path.Combine(jobFolder, "logs", "cli-output.log")),
            StringComparison.OrdinalIgnoreCase);
    }

    private GenericCliExecutionService Service(string script)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["CliExecution:DurableLocalWorkers"] = "true",
            })
            .Build();
        var service = GenericCliExecutionService.ForAntigravity(
            NullLogger<GenericCliExecutionService>.Instance,
            configuration);
        service.SetCliPath(script);
        return service;
    }

    private WebApplicationFactory<Program> HostFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["CliExecution:DurableLocalWorkers"] = "true",
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "host-logs"),
                }));
        });

    private string CreateFakeCli(int delaySeconds)
    {
        var path = Path.Combine(_root, $"fake-cli-{delaySeconds}-{Guid.NewGuid():N}" + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
        var body = OperatingSystem.IsWindows()
            ? $"@echo off\r\nping -n {delaySeconds + 1} 127.0.0.1 >nul\r\necho [[TASK_DONE]]\r\nexit /b 0\r\n"
            : $"#!/usr/bin/env bash\nsleep {delaySeconds}\necho '[[TASK_DONE]]'\nexit 0\n";
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            using var chmod = Process.Start(new ProcessStartInfo("chmod")
            {
                ArgumentList = { "+x", path },
                UseShellExecute = false,
            });
            chmod!.WaitForExit();
        }
        return path;
    }

    private async Task WaitForWorkerResultAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.EnumerateFiles(_root, LocalCliDurableWorker.ResultFileName, SearchOption.AllDirectories).Any())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The fake durable worker did not persist its result.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(failure);
    }

    public void Dispose()
    {
        foreach (var pid in _workerPids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "LocalCliRestartHarnessTests: worker already exited during cleanup.");
            }
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliRestartHarnessTests: best-effort temp cleanup."); }
    }
}
