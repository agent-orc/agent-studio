using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Cli;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Process-bound restart harness for the Studio-local durability boundary.
/// The running-worker case cycles two real WebApplication test hosts. The
/// remaining timing and fence cases isolate the same replacement service graph
/// so failures identify the durability boundary rather than unrelated hosted
/// services.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
[Trait("Category", "MachineBound")]
public sealed class DurableLocalCliRestartTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "studio-local-restart",
        Guid.NewGuid().ToString("N"));
    private readonly List<DurableLocalCliProcess> _workers = [];

    [Fact]
    public async Task Worker_still_running_at_reattach_completes_once_through_replacement_host()
    {
        var worker = StartWorker(delaySeconds: 8, includeResultSentinel: true);
        SeedActiveJob(worker, "AGT-running");

        await using (var stoppedHost = BuildHost())
        {
            Assert.NotNull(stoppedHost.Services.GetRequiredService<CliRouter>());
            Assert.True(worker.VerifyLive(_root, out _));
        }

        await using var replacementHost = BuildHost();
        var service = replacementHost.Services
            .GetRequiredKeyedService<GenericCliExecutionService>(CliTypes.Claude);
        var finished = CaptureFinished(service);

        service.ReattachOnStartup();
        Assert.True(service.ConfirmRecoveredExecution(JobKey("AGT-running")));

        var running = Assert.Single(service.RunningExecutions());
        Assert.Equal("AGT-running", running.Execution.JobId);
        var execution = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, execution.ExitCode);
        Assert.Equal("completed", execution.Status);
        Assert.Contains(service.GetOutput(JobKey("AGT-running")), line =>
            line.Text.Contains("[[TASK_DONE]]", StringComparison.Ordinal));
        Assert.Equal(1, finished.Count);
        await WaitUntilAsync(() => ActiveLedgerCount() == 0, TimeSpan.FromSeconds(5));
        AssertActiveLedgerEmpty();
    }

    private WebApplicationFactory<Program> BuildHost()
    {
        var projectTasks = Path.Combine(_root, "project-tasks");
        Directory.CreateDirectory(projectTasks);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
                    ["WatchPaths:0:Name"] = "Restart Harness",
                    ["WatchPaths:0:Path"] = projectTasks,
                    ["WatchPaths:0:RootPath"] = _root,
                    ["WatchPaths:0:RepositoryPath"] = _root,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["LocalCliDurability:Enabled"] = "true",
                }));
        });
    }

    [Fact]
    public async Task Worker_finished_during_backend_gap_replays_result_and_post_run_once()
    {
        var worker = StartWorker(delaySeconds: 0, includeResultSentinel: true);
        await WaitForResultAsync(worker);
        SeedActiveJob(worker, "AGT-finished-gap");
        var service = CreateReplacement();
        var finished = CaptureFinished(service);

        service.ReattachOnStartup();
        Assert.True(service.ConfirmRecoveredExecution(JobKey("AGT-finished-gap")));

        // A result-ready worker remains visible until the replacement runner
        // has had a chance to rebook the slot, then takes the normal terminal
        // callback exactly once.
        Assert.Single(service.RunningExecutions());
        var execution = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(1, finished.Count);
        await WaitUntilAsync(() => ActiveLedgerCount() == 0, TimeSpan.FromSeconds(5));
        AssertActiveLedgerEmpty();
    }

    [Fact]
    public async Task Worker_died_during_backend_gap_reports_distinct_lost_failure_once()
    {
        var worker = StartWorker(delaySeconds: 30, includeResultSentinel: false);
        worker.Kill();
        await WaitUntilAsync(
            () => !worker.VerifyLive(_root, out _),
            TimeSpan.FromSeconds(10));
        Assert.Null(worker.ReadResult());
        SeedActiveJob(worker, "AGT-lost-gap");
        var service = CreateReplacement();
        var finished = CaptureFinished(service);

        service.ReattachOnStartup();
        Assert.True(service.ConfirmRecoveredExecution(JobKey("AGT-lost-gap")));

        var execution = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(-1, execution.ExitCode);
        Assert.Equal("failed", execution.Status);
        Assert.Contains(service.GetOutput(JobKey("AGT-lost-gap")), line =>
            line.Text.Contains("run-lost-across-restart", StringComparison.Ordinal));
        Assert.Equal(1, finished.Count);
        await WaitUntilAsync(() => ActiveLedgerCount() == 0, TimeSpan.FromSeconds(5));
        AssertActiveLedgerEmpty();
    }

    [Fact]
    public async Task Superseded_authority_rejects_finished_worker_without_post_run_delivery()
    {
        var worker = StartWorker(delaySeconds: 0, includeResultSentinel: true);
        await WaitForResultAsync(worker);
        SeedActiveJob(worker, "AGT-superseded");
        var service = CreateReplacement();
        var finished = CaptureFinished(service);

        service.ReattachOnStartup();
        Assert.True(service.RejectRecoveredExecution(JobKey("AGT-superseded")));

        await WaitUntilAsync(
            () => ActiveLedgerCount() == 0,
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, finished.Count);
    }

    [Fact]
    public async Task Output_cursor_advances_only_after_out_of_order_delivery_becomes_contiguous()
    {
        var worker = StartWorker(delaySeconds: 0, includeResultSentinel: true);
        await WaitForResultAsync(worker);
        var lines = worker.ReadAfter(0);
        Assert.True(lines.Count >= 2);

        foreach (var line in lines.Skip(1))
            worker.AcknowledgeLiveLine(line.Stream, line.Text);
        var afterSecondOnly = DurableLocalCliProcess.Attach(
            worker.DirectoryPath,
            worker.ProcessId,
            worker.ProcessStartedAtUtc);
        Assert.Equal(lines.Count, afterSecondOnly.ReadUnacknowledged().Count);

        worker.AcknowledgeLiveLine(lines[0].Stream, lines[0].Text);
        var afterContiguous = DurableLocalCliProcess.Attach(
            worker.DirectoryPath,
            worker.ProcessId,
            worker.ProcessStartedAtUtc);
        Assert.Empty(afterContiguous.ReadUnacknowledged());
    }

    private DurableLocalCliProcess StartWorker(int delaySeconds, bool includeResultSentinel)
    {
        Directory.CreateDirectory(_root);
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            var delay = delaySeconds == 0 ? "" : $"ping -n {delaySeconds + 1} 127.0.0.1 >nul & ";
            start.ArgumentList.Add(delay + (includeResultSentinel
                ? "echo durable-output & echo [[TASK_DONE]]"
                : "echo started"));
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(
                $"sleep {delaySeconds}; printf 'durable-output\\n{(includeResultSentinel ? "[[TASK_DONE]]\\n" : "")} '");
        }

        var worker = DurableLocalCliProcess.Start(
            Path.Combine(_root, "workers", Guid.NewGuid().ToString("N")),
            start);
        _workers.Add(worker);
        return worker;
    }

    private GenericCliExecutionService CreateReplacement()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["LocalCliDurability:Enabled"] = "true",
            })
            .Build();
        return GenericCliExecutionService.ForClaude(
            NullLogger<GenericCliExecutionService>.Instance,
            configuration);
    }

    private void SeedActiveJob(DurableLocalCliProcess worker, string jobId)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".runtime"));
        var entry = new
        {
            TaskKey = JobKey(jobId),
            JobId = jobId,
            ProcessId = worker.ProcessId,
            ProcessName = (string?)null,
            ProcessStartTimeUtc = worker.ProcessStartedAtUtc,
            StartedAt = worker.ProcessStartedAtUtc,
            WorkerDirectory = worker.DirectoryPath,
            WorkingDirectory = _root,
            JobFolderPath = Path.Combine(_root, "3-progress", jobId),
            Model = "fake-long-running-cli",
            ThinkingLevel = (string?)null,
            PermissionMode = "yolo",
            ContextMode = "shared",
        };
        File.WriteAllText(
            ActiveJobsPath,
            JsonSerializer.Serialize(new[] { entry }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Capture CaptureFinished(GenericCliExecutionService service)
    {
        var capture = new Capture();
        service.OnFinished += (_, execution) => capture.Set(execution);
        return capture;
    }

    private string JobKey(string jobId) => $"{_root}::{jobId}";
    private string ActiveJobsPath => Path.Combine(_root, ".runtime", "active-jobs-claude.json");

    private void AssertActiveLedgerEmpty()
    {
        Assert.Equal(0, ActiveLedgerCount());
    }

    private int ActiveLedgerCount()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ActiveJobsPath));
        return document.RootElement.GetArrayLength();
    }

    private static async Task WaitForResultAsync(DurableLocalCliProcess worker)
        => await WaitUntilAsync(() => worker.ReadResult() is not null, TimeSpan.FromSeconds(10));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Restart harness condition timed out.");
            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        foreach (var worker in _workers) worker.Kill();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }

    private sealed class Capture
    {
        private readonly TaskCompletionSource<CliExecution> _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task<CliExecution> Task => _finished.Task;
        public int Count => Volatile.Read(ref _count);

        public void Set(CliExecution execution)
        {
            Interlocked.Increment(ref _count);
            _finished.TrySetResult(execution);
        }
    }
}
