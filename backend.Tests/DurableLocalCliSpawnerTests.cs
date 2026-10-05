using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CarRunInfo = CodingAgentRunner.Model.CliRunInfo;

namespace AgentStudio.Tests;

/// <summary>
/// Live spawn contract for the Studio-local durable worker (AGT-2821).
/// <see cref="DurableLocalCliProcessSpawner"/> is the object CAR calls at the
/// <c>ICliProcessSpawner</c> seam, so the pipes it hands back must carry the
/// real run: the prompt has to reach the CLI on stdin and the CLI's stdout and
/// stderr have to reach CAR's stream readers. The regression this pins is the
/// 0.3.0 Stable failure where the spawner returned the reopened worker handle
/// and every access threw <c>StandardIn has not been redirected</c>.
///
/// <para>The fake CLI is a real child process on the current OS, so these
/// tests are machine-bound by construction.</para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class DurableLocalCliSpawnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "studio-durable-spawn",
        Guid.NewGuid().ToString("N"));

    public DurableLocalCliSpawnerTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// End-to-end through the real CAR engine: CAR builds the launch, writes
    /// the one-shot prompt to <see cref="CliSpawn.Stdin"/> and reads both
    /// streams through <see cref="CliSpawn.Stdout"/> / <see cref="CliSpawn.Stderr"/>.
    /// The fake CLI echoes the prompt it received on stdin, so a passing run
    /// proves the whole round trip, not just that a process started.
    /// </summary>
    [Fact]
    public async Task Durable_spawn_delivers_the_prompt_on_stdin_and_streams_output_back_to_car()
    {
        var prompt = "durable-prompt-" + Guid.NewGuid().ToString("N");
        var spawner = new DurableLocalCliProcessSpawner(Path.Combine(_root, "worker"));
        var driver = BuildDriver(spawner, out var output, out var finished);

        var (run, error) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "durable-spawn",
            Prompt = prompt,
            WorkingDirectory = _root,
            ContextMode = CliContextModes.Shared,
        });

        Assert.Null(error);
        Assert.NotNull(run);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var lines = output.Select(line => line.Text).ToList();

        Assert.Contains(lines, text => text.Contains("fake-stdout: " + prompt, StringComparison.Ordinal));
        Assert.Contains(lines, text => text.Contains("fake-stderr: started", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same round trip must also be mirrored into the worker's own
    /// <c>output.jsonl</c>: that file is what a replacement backend tails
    /// after a restart, so live delivery and durable delivery cannot diverge.
    /// </summary>
    [Fact]
    public async Task Durable_spawn_mirrors_the_same_output_into_the_worker_log()
    {
        var prompt = "durable-prompt-" + Guid.NewGuid().ToString("N");
        var spawner = new DurableLocalCliProcessSpawner(Path.Combine(_root, "worker"));
        var driver = BuildDriver(spawner, out _, out var finished);

        var (run, error) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "durable-mirror",
            Prompt = prompt,
            WorkingDirectory = _root,
            ContextMode = CliContextModes.Shared,
        });

        Assert.Null(error);
        Assert.NotNull(run);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var worker = spawner.Worker;
        Assert.NotNull(worker);
        var durable = worker!.ReadAfter(0);
        Assert.Contains(durable, line =>
            line.Stream == "stdout" && line.Text.Contains("fake-stdout: " + prompt, StringComparison.Ordinal));
        Assert.Contains(durable, line =>
            line.Stream == "stderr" && line.Text.Contains("fake-stderr: started", StringComparison.Ordinal));
    }

    /// <summary>
    /// A local run's exit code comes from the worker's own <c>result.json</c>.
    /// CAR only holds a reopened handle for the worker, and .NET refuses to
    /// report an exit code for a process another object started, so the
    /// durable result is what the host classifies the run from.
    /// </summary>
    [Fact]
    public async Task Durable_result_carries_the_cli_exit_code_for_the_reopened_worker_handle()
    {
        var spawner = new DurableLocalCliProcessSpawner(Path.Combine(_root, "worker"));
        var driver = BuildDriver(spawner, out _, out var finished, exitCode: 7);

        var (run, error) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "durable-exit",
            Prompt = "durable-prompt",
            WorkingDirectory = _root,
            ContextMode = CliContextModes.Shared,
        });

        Assert.Null(error);
        Assert.NotNull(run);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var result = spawner.Worker?.ReadResult();
        Assert.NotNull(result);
        Assert.Equal(7, result!.ExitCode);
    }

    /// <summary>
    /// The whole Studio boundary with the durable path enabled: the shape the
    /// Stable 0.3.0 report took ("every local Claude run fails at spawn"). It
    /// asserts a started run, the prompt echoed back through the streamed
    /// output, and the CLI's real exit code on the finished execution - the
    /// last one is what the host classifies the card from, and a reopened
    /// worker handle cannot supply it.
    /// </summary>
    [Fact]
    public async Task Studio_local_run_through_the_durable_worker_streams_output_and_reports_the_exit_code()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["LocalCliDurability:Enabled"] = "true",
            })
            .Build();
        var service = GenericCliExecutionService.ForClaude(
            NullLogger<GenericCliExecutionService>.Instance,
            configuration);
        var fakeCli = WriteFakeCli(exitCode: 7);
        service.SetCliPath(fakeCli);
        service.CarOptionsCustomizer = options => options with { ClaudePath = fakeCli };

        var finished = new TaskCompletionSource<CliExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnFinished += (_, execution) => finished.TrySetResult(execution);

        var prompt = "durable-prompt-" + Guid.NewGuid().ToString("N");
        var jobKey = _root + "::AGT-durable";
        var (execution, error) = await service.StartAsync(
            jobId: "AGT-durable",
            jobKey: jobKey,
            prompt: prompt,
            workingDirectory: _root);

        Assert.Null(error);
        Assert.NotNull(execution);

        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(7, final.ExitCode);
        Assert.Equal(RunStatuses.Failed, final.Status);
        Assert.Contains(
            service.GetOutput(jobKey),
            line => line.Text.Contains("fake-stdout: " + prompt, StringComparison.Ordinal));
    }

    private ICliDriver BuildDriver(
        DurableLocalCliProcessSpawner spawner,
        out ConcurrentQueue<CliOutputLine> output,
        out TaskCompletionSource<CarRunInfo> finished,
        int exitCode = 0)
    {
        var options = new CliOptions
        {
            ClaudePath = WriteFakeCli(exitCode),
            ClaudePromptTransport = ClaudePromptTransport.Stdin,
            Spawner = spawner,
            AllowAgentGitMutation = false,
            Delegation = new CodingAgentRunner.Delegation.DelegationOptions { Enabled = false },
        };
        var runner = new CliRunner(options, NullLogger.Instance, new TestRunLogPaths(_root));
        var driver = runner.Get(CliTypes.Claude);

        var lines = new ConcurrentQueue<CliOutputLine>();
        driver.OnOutput += (_, line) => lines.Enqueue(line);
        output = lines;

        var completion = new TaskCompletionSource<CarRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, info) => completion.TrySetResult(info);
        finished = completion;
        return driver;
    }

    /// <summary>
    /// Minimal stand-in for a coding CLI: answers the pre-spawn
    /// <c>--version</c> probe, then reads the whole prompt from stdin and
    /// writes one stdout and one stderr line.
    /// </summary>
    private string WriteFakeCli(int exitCode = 0)
    {
        var directory = Path.Combine(_root, "fake-cli");
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(directory, "fake-cli.cmd");
            File.WriteAllText(batch, string.Join(Environment.NewLine,
            [
                "@echo off",
                "if \"%1\"==\"--version\" (echo 0.0.0-fake& exit /b 0)",
                "echo fake-stderr: started 1>&2",
                "set /p PROMPT=",
                "echo fake-stdout: %PROMPT%",
                $"exit /b {exitCode}",
            ]) + Environment.NewLine);
            return batch;
        }

        var script = Path.Combine(directory, "fake-cli.sh");
        File.WriteAllText(script, string.Join('\n',
        [
            "#!/bin/sh",
            "if [ \"$1\" = \"--version\" ]; then echo 0.0.0-fake; exit 0; fi",
            "echo 'fake-stderr: started' 1>&2",
            "prompt=$(cat)",
            "echo \"fake-stdout: $prompt\"",
            $"exit {exitCode}",
        ]) + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }

    private sealed class TestRunLogPaths(string root) : IRunLogPathProvider
    {
        public string GetRunLogDirectory(string runId) => Path.Combine(root, "car-logs", runId);
        public string GetActiveJobsFile() => Path.Combine(root, "car-logs", "active-runs.json");
    }
}
