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
            Assert.Equal([new RunbookHostStepResult(0, true), new RunbookHostStepResult(1, false)], result.Steps);
            Assert.Equal($"info:{secret}:{secret}:production", File.ReadAllText(marker));
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

    [Fact]
    public async Task Host_cancellation_stops_a_new_session_child_that_keeps_writing_and_holds_output_open()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-session-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var ready = Path.Combine(root, "parent-pid");
        var child = Path.Combine(root, "child-pid");
        var marker = Path.Combine(root, "child-wrote");
        // setsid leaves the script's session and process group, and the child inherits stdout.
        File.WriteAllText(Path.Combine(scripts, "backup.sh"),
            "setsid sh -c 'printf \"%s\" \"$$\" > \"$CHILD\"; while :; do printf x >> \"$MARKER\"; sleep 0.05; done' &\n"
            + "printf '%s' \"$$\" > \"$READY\"\nexit 0\n");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var execution = RunbookHostExecutor.ExecuteAsync("demo", "stage", "8081", ["backup"],
                new Dictionary<string, string> { ["READY"] = ready, ["CHILD"] = child, ["MARKER"] = marker },
                new Dictionary<string, string>(), [], Path.Combine(root, "scripts"), root, cancellation.Token);
            for (var attempt = 0; attempt < 250 && !(File.Exists(ready) && File.Exists(marker)); attempt++)
                await Task.Delay(20);
            Assert.True(File.Exists(marker), "The new-session child did not start writing.");
            var parentId = int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture);
            var childId = int.Parse(File.ReadAllText(child), CultureInfo.InvariantCulture);
            // The script leads its own session, so its session id is its process id.
            Assert.NotEqual(parentId.ToString(CultureInfo.InvariantCulture), Session(childId));
            for (var attempt = 0; attempt < 100 && Directory.Exists($"/proc/{parentId}"); attempt++)
                await Task.Delay(20);
            Assert.False(Directory.Exists($"/proc/{parentId}"), "The script did not exit.");
            Assert.False(execution.IsCompleted, "The child did not keep an output pipe open.");

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
            var written = new FileInfo(marker).Length;
            await Task.Delay(500);
            Assert.Equal(written, new FileInfo(marker).Length);
        }
        finally
        {
            cancellation.Cancel();
            StopLeftover(child);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("LD_PRELOAD", null)]
    [InlineData("ld_library_path", null)]
    [InlineData("PATH", null)]
    [InlineData("BASH_ENV", null)]
    [InlineData(null, "ld-preload")]
    public async Task Refuses_loader_and_shell_variables_before_starting_a_script(string? variable, string? environmentSecret)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-loader-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var marker = Path.Combine(root, "started");
        File.WriteAllText(Path.Combine(root, "ld-preload"), Path.Combine(root, "evil.so"));
        File.WriteAllText(Path.Combine(scripts, "backup.sh"), "printf 'started' > \"$MARKER\"\n");
        var variables = new Dictionary<string, string> { ["MARKER"] = marker };
        if (variable is not null) variables[variable] = Path.Combine(root, "evil.so");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
                "demo", "stage", "8081", ["backup"], variables, new Dictionary<string, string>(),
                environmentSecret is null ? [] : [environmentSecret], Path.Combine(root, "scripts"), root,
                CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
                "demo", "stage", "8081", ["backup"], new Dictionary<string, string> { ["MARKER"] = marker },
                new Dictionary<string, string> { ["LD_AUDIT"] = "ld-preload" }, [], Path.Combine(root, "scripts"), root,
                CancellationToken.None));
            Assert.False(File.Exists(marker), "A script started with a loader or shell variable.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Session(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[3];
    }

    private static void StopLeftover(string pidFile)
    {
        if (!File.Exists(pidFile)
            || !int.TryParse(File.ReadAllText(pidFile), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return;
        try
        {
            using var leftover = System.Diagnostics.Process.GetProcessById(id);
            leftover.Kill();
        }
        catch (ArgumentException)
        {
            // The child is already gone, which is the expected outcome.
            return;
        }
    }
}
