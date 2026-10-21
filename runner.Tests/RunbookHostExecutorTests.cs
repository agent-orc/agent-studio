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

    [Theory]
    [InlineData("LD_PRELOAD", "variable")]
    [InlineData("LD_LIBRARY_PATH", "secret")]
    [InlineData("BASH_ENV", "variable")]
    [InlineData("PATH", "variable")]
    [InlineData("ld-preload", "environment-secret")]
    public async Task Refuses_loader_and_shell_variables_before_starting_a_script(string name, string source)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-loader-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var marker = Path.Combine(root, "started");
        File.WriteAllText(Path.Combine(scripts, "backup.sh"), $"printf 'started' > '{marker}'\n");
        File.WriteAllText(Path.Combine(root, "loader"), "/tmp/evil.so");
        File.WriteAllText(Path.Combine(root, "ld-preload"), "/tmp/evil.so");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunbookHostExecutor.ExecuteAsync(
                "demo", "stage", "8081", ["backup"],
                source == "variable" ? new Dictionary<string, string> { [name] = "/tmp/evil.so" } : [],
                source == "secret" ? new Dictionary<string, string> { [name] = "loader" } : [],
                source == "environment-secret" ? [name] : [],
                Path.Combine(root, "scripts"), root, CancellationToken.None));
            Assert.False(File.Exists(marker), "The script started with a loader or shell variable.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Host_cancellation_kills_a_child_in_a_new_session_that_holds_output_open()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "runbook-session-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "demo");
        Directory.CreateDirectory(scripts);
        var ready = Path.Combine(root, "child-pid");
        var marker = Path.Combine(root, "child-wrote");
        File.WriteAllText(Path.Combine(scripts, "backup.sh"),
            "setsid /bin/sh -c 'printf \"%s\" \"$$\" > \"$READY\"; sleep 2; printf escaped > \"$MARKER\"; sleep 30' &\nexit 0\n");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var execution = RunbookHostExecutor.ExecuteAsync("demo", "stage", "8081", ["backup"],
                new Dictionary<string, string> { ["READY"] = ready, ["MARKER"] = marker },
                new Dictionary<string, string>(), [], Path.Combine(root, "scripts"), root, cancellation.Token);
            for (var attempt = 0; attempt < 100 && !File.Exists(ready); attempt++)
                await Task.Delay(20);
            Assert.True(File.Exists(ready), "The child did not start.");
            await Task.Delay(100);
            var childId = int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture);
            Assert.Equal(childId.ToString(CultureInfo.InvariantCulture), StatusFields(childId)?[3]);
            Assert.False(execution.IsCompleted, "The child did not keep an output pipe open.");

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(StatusFields(childId) is null or ["Z", ..], "The child in a new session survived host cancellation.");
            await Task.Delay(2200);
            Assert.False(File.Exists(marker), "The child in a new session continued after host cancellation.");
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

    /// <summary>The <c>/proc/&lt;id&gt;/stat</c> fields after the command name: state, ppid, pgrp, session; null once reaped.</summary>
    private static string[]? StatusFields(int processId)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{processId}/stat");
        }
        catch (IOException)
        {
            return null;
        }
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
    }
}
