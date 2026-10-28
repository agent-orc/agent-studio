using AgentRunner.WorkContainers;
using Xunit;

namespace AgentRunner.Tests.WorkContainers;

public class WorkProcessExecutorTests
{
    private static readonly WorkContainerLimits Limits = new(2, 1L << 30, 1024);

    [SkippableFact]
    public async Task Windows_runs_a_windows_only_fixture_in_a_job_object_without_docker()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only fixture.");
        // The selection never consults a runtime on Windows, even in canary mode.
        var selection = WorkExecutorSelectionPolicy.Select(true, false, WorkContainerMode.Canary, runtimeAvailable: false, roleImagePinned: false);
        Assert.Equal(WorkExecutorKind.Process, selection.Kind);

        var result = await WorkProcessExecutor.RunAsync(Path.GetTempPath(), "cmd.exe", ["/d", "/c", "ver"], Limits,
            null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Windows", result.Output);
        Assert.Equal("windows-job-object", result.Bound);
    }

    [SkippableFact]
    public async Task Linux_process_rollback_runs_without_a_container_runtime()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux process rollback.");
        var result = await WorkProcessExecutor.RunAsync(Path.GetTempPath(), "/bin/sh", ["-c", "echo process-executor"], Limits,
            new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" }, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("process-executor", result.Output);
        Assert.NotEqual("windows-job-object", result.Bound);
    }

    [Fact]
    public void Shadow_mode_records_the_container_verdict_while_the_worker_keeps_the_process_executor()
    {
        var worker = Path.Combine(Path.GetTempPath(), $"wc-shadow-{Guid.NewGuid():N}");
        try
        {
            WorkContainerShadow.Record(worker, "coding", worker, null, name => name switch
            {
                WorkContainerSettings.ModeVariable => "shadow",
                WorkContainerSettings.ImageManifestVariable => Path.Combine(worker, "missing.json"),
                _ => null,
            });

            var record = File.ReadAllText(Path.Combine(worker, WorkContainerShadow.FileName));
            Assert.Contains("\"mode\": \"shadow\"", record);
            Assert.Contains("\"selectedExecutor\": \"process\"", record);
            Assert.Contains("\"workerExecutor\": \"process\"", record);
        }
        finally
        {
            if (Directory.Exists(worker)) Directory.Delete(worker, true);
        }
    }

    [Fact]
    public void Shadow_recording_is_silent_when_the_switch_is_off_or_invalid()
    {
        var worker = Path.Combine(Path.GetTempPath(), $"wc-shadow-{Guid.NewGuid():N}");
        var messages = new List<string>();
        WorkContainerShadow.Record(worker, "review", worker, messages.Add, _ => null);
        WorkContainerShadow.Record(worker, "review", worker, messages.Add,
            name => name == WorkContainerSettings.ModeVariable ? "off" : null);
        WorkContainerShadow.Record(worker, "review", worker, messages.Add,
            name => name == WorkContainerSettings.ModeVariable ? "everywhere" : null);

        Assert.False(File.Exists(Path.Combine(worker, WorkContainerShadow.FileName)));
        Assert.Single(messages);
        Assert.StartsWith("[work-container] shadow skipped", messages[0]);
    }
}
