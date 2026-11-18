using System.Globalization;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunbookHostExecutorTests
{
    [Fact]
    public async Task Executes_ordered_steps_with_environment_variables_and_stops_after_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-host-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        var secrets = Path.Combine(root, "secrets");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(secrets);
        var marker = Path.Combine(root, "marker");
        var secret = "fixture-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(secrets, "db-secret"), secret);
        File.WriteAllText(Path.Combine(scripts, "backup.sh"),
            "printf '%s:%s:%s:%s' \"$LOG_LEVEL\" \"$DATABASE_URL\" \"$DB_SECRET\" \"$AGENT_STUDIO_ENVIRONMENT\" > \"$MARKER\"\n");
        File.WriteAllText(Path.Combine(scripts, "rotate.sh"), "exit 7\n");
        File.WriteAllText(Path.Combine(scripts, "restart.sh"), "printf 'unexpected' >> \"$MARKER\"\n");
        try
        {
            var result = await RunbookHostExecutor.ExecuteAsync("demo", "production", "8082",
                ["backup", "rotate", "restart"],
                new Dictionary<string, string> { ["LOG_LEVEL"] = "info", ["MARKER"] = marker },
                new Dictionary<string, string> { ["DATABASE_URL"] = "db-secret" },
                ["db-secret"],
                Path.Combine(root, "scripts"), secrets, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal([(0, true), (1, false)], result.Steps.Select(step => (step.Index, step.Success)));
            Assert.Equal("Exit code 7.", result.Steps[1].Output);
            Assert.Equal($"info:{secret}:{secret}:production", File.ReadAllText(marker));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Returns_the_masked_bounded_output_tail_of_each_step()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-output-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        var secrets = Path.Combine(root, "secrets");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(secrets);
        var secret = "fixture-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(secrets, "db-secret"), secret);
        File.WriteAllText(Path.Combine(scripts, "free-disk.sh"),
            // stdout only: stderr is a second pipe, so its lines may land anywhere in the tail.
            "echo \"Removed 3 images, freed 1.2 GB with $DATABASE_URL\"\n"
            + "i=0; while [ $i -lt 450 ]; do echo \"line $i\"; i=$((i+1)); done\n"
            + "head -c 5000 /dev/zero | tr '\\0' 'y'; echo\n");
        try
        {
            var result = await RunbookHostExecutor.ExecuteAsync("demo", "stage", "8081", ["free-disk", "restart"],
                new Dictionary<string, string>(), new Dictionary<string, string> { ["DATABASE_URL"] = "db-secret" }, [],
                Path.Combine(root, "scripts"), secrets, CancellationToken.None);

            Assert.False(result.Success);
            var output = result.Steps[0].Output!.Split('\n');
            Assert.True(result.Steps[0].Success);
            Assert.DoesNotContain(secret, result.Steps[0].Output);
            Assert.Equal(RunbookHostExecutor.StepOutput.MaxLines + 1, output.Length);
            Assert.Equal("(252 earlier lines omitted)", output[0]);
            Assert.Equal("line 449", output[^2]);
            Assert.Equal(RunbookHostExecutor.StepOutput.MaxLineLength + 1, output[^1].Length);
            Assert.False(result.Steps[1].Success);
            Assert.Equal($"Script {Path.Combine(root, "scripts", "demo", "restart.sh")} is not installed on this host.",
                result.Steps[1].Output);

            File.WriteAllText(Path.Combine(scripts, "free-disk.sh"),
                "echo \"Removed 3 images, freed 1.2 GB\"\necho \"connecting with $DATABASE_URL\" >&2\n");
            var brief = await RunbookHostExecutor.ExecuteAsync("demo", "stage", "8081", ["free-disk"],
                new Dictionary<string, string>(), new Dictionary<string, string> { ["DATABASE_URL"] = "db-secret" }, [],
                Path.Combine(root, "scripts"), secrets, CancellationToken.None);
            var lines = brief.Steps[0].Output!.Split('\n');
            Assert.Contains("Removed 3 images, freed 1.2 GB", lines);
            Assert.Contains("connecting with [masked]", lines);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Refuses_path_traversal_before_starting_a_script()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
            "demo", "stage", "8081", ["../escape"], new Dictionary<string, string>(),
            new Dictionary<string, string>(), [], "/tmp", "/tmp", CancellationToken.None));
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("PATH")]
    [InlineData("BASH_ENV")]
    public async Task Refuses_variables_that_control_the_loader_or_shell_before_starting_a_script(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "runbook-reserved-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var marker = Path.Combine(root, "started");
        File.WriteAllText(Path.Combine(scripts, "backup.sh"), $"printf 'started' > '{marker}'\n");
        File.WriteAllText(Path.Combine(root, name.ToLowerInvariant()), "/tmp/preload.so");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
                "demo", "stage", "8081", ["backup"], new Dictionary<string, string> { [name] = "/tmp/preload.so" },
                new Dictionary<string, string>(), [], Path.Combine(root, "scripts"), root, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
                "demo", "stage", "8081", ["backup"], new Dictionary<string, string>(),
                new Dictionary<string, string> { [name] = name.ToLowerInvariant() }, [],
                Path.Combine(root, "scripts"), root, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
                "demo", "stage", "8081", ["backup"], new Dictionary<string, string>(),
                new Dictionary<string, string>(), [name.ToLowerInvariant()],
                Path.Combine(root, "scripts"), root, CancellationToken.None));
            Assert.False(File.Exists(marker), "The script started with a reserved variable.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Host_cancellation_kills_a_new_session_child_that_holds_output_and_keeps_writing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-session-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var ready = Path.Combine(root, "child-pid");
        var marker = Path.Combine(root, "child-writes");
        File.WriteAllText(Path.Combine(scripts, "backup.sh"),
            "setsid sh -c 'printf \"%s\" \"$$\" > \"$READY\"; while :; do printf x >> \"$MARKER\"; sleep 0.05; done' &\nwait\n");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var execution = RunbookHostExecutor.ExecuteAsync("demo", "stage", "8081", ["backup"],
                new Dictionary<string, string> { ["READY"] = ready, ["MARKER"] = marker },
                new Dictionary<string, string>(), [], Path.Combine(root, "scripts"), root, cancellation.Token);
            for (var attempt = 0; attempt < 250 && !(File.Exists(ready) && File.Exists(marker)); attempt++)
                await Task.Delay(20);
            Assert.True(File.Exists(ready) && File.Exists(marker), "The child did not start writing.");
            var childId = int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture);
            // Field 6 of /proc/<pid>/stat is the session id: the child leads its own session.
            var stat = File.ReadAllText($"/proc/{childId}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            Assert.Equal(childId, int.Parse(fields[3], CultureInfo.InvariantCulture));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            var written = new FileInfo(marker).Length;
            await Task.Delay(500);
            Assert.Equal(written, new FileInfo(marker).Length);
        }
        finally
        {
            cancellation.Cancel();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Host_cancellation_kills_the_script_and_its_child_before_it_can_write()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-cancel-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var ready = Path.Combine(root, "ready");
        var marker = Path.Combine(root, "child-wrote");
        File.WriteAllText(Path.Combine(scripts, "backup.sh"),
            "(sleep 1; printf 'escaped' > \"$MARKER\") &\nprintf 'started' > \"$READY\"\nwait\n");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var execution = RunbookHostExecutor.ExecuteAsync("demo", "stage", "8081", ["backup"],
                new Dictionary<string, string> { ["READY"] = ready, ["MARKER"] = marker },
                new Dictionary<string, string>(), [], Path.Combine(root, "scripts"), root, cancellation.Token);
            for (var attempt = 0; attempt < 250 && !File.Exists(ready); attempt++)
                await Task.Delay(20);
            Assert.True(File.Exists(ready), "The script did not start.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            await Task.Delay(1200);
            Assert.False(File.Exists(marker), "A child process kept running after cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Host_cancellation_stops_a_child_that_keeps_output_open_after_the_script_exits()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-exited-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var ready = Path.Combine(root, "parent-pid");
        var marker = Path.Combine(root, "child-wrote");
        File.WriteAllText(Path.Combine(scripts, "backup.sh"),
            "(sleep 2; printf 'escaped' > \"$MARKER\") &\nprintf '%s' \"$$\" > \"$READY\"\nexit 0\n");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var execution = RunbookHostExecutor.ExecuteAsync("demo", "stage", "8081", ["backup"],
                new Dictionary<string, string> { ["READY"] = ready, ["MARKER"] = marker },
                new Dictionary<string, string>(), [], Path.Combine(root, "scripts"), root, cancellation.Token);
            for (var attempt = 0; attempt < 100 && !File.Exists(ready); attempt++)
                await Task.Delay(20);
            Assert.True(File.Exists(ready), "The script did not start.");
            var parentId = int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture);
            for (var attempt = 0; attempt < 100 && Directory.Exists($"/proc/{parentId}"); attempt++)
                await Task.Delay(20);
            Assert.False(Directory.Exists($"/proc/{parentId}"), "The script did not exit.");
            Assert.False(execution.IsCompleted, "The child did not keep an output pipe open.");

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(1)));
            await Task.Delay(2200);
            Assert.False(File.Exists(marker), "The child continued after host cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            Directory.Delete(root, true);
        }
    }
}
