using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class StatusCommandGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("status-command-tests-").FullName;

    [Fact]
    public async Task Bash_preserves_output_exit_and_cwd_and_counts_only_repeats_inside_interval()
    {
        if (!OperatingSystem.IsLinux()) return;
        const int interval = 180;
        var guard = Prepare(CliSelection.CodexCli, interval);
        var sample = "cat ./sample; test -f ./missing";
        File.WriteAllText(Path.Combine(_root, "sample"), "test output\n");
        var original = await Bash(sample);
        var first = await Bash(sample, guard);
        var waiting = Stopwatch.StartNew();
        var repeated = await Bash(sample, guard);
        Assert.True(waiting.Elapsed >= TimeSpan.FromSeconds(5));
        var different = await Bash("pwd", guard);

        Assert.Equal(original, first);
        Assert.Equal(1, repeated.Exit);
        Assert.Equal(original.Out, repeated.Out);
        Assert.Single(repeated.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("wait capped for the command tool timeout", repeated.Error);
        Assert.Contains("this status command repeats", repeated.Error);
        Assert.Equal((_root + "\n", "", 0), different);
        Assert.Equal(new StatusCommandSummary(3, 1, 1), Read(guard));

        var database = Path.Combine(Path.GetDirectoryName(guard.BashEnvironment)!, "calls.sqlite3");
        var advance = await Run("/usr/bin/python3",
            ["-I", "-B", "-c", """
                import sqlite3, sys, time
                with sqlite3.connect(sys.argv[1]) as connection:
                    connection.execute("UPDATE status SET last = ?", (time.monotonic() - int(sys.argv[2]) - 1,))
                """, database, interval.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        Assert.Equal(("", "", 0), advance);
        Assert.Equal(original, await Bash(sample, guard));
        Assert.Equal(new StatusCommandSummary(4, 1, 1), Read(guard));
    }

    [Fact]
    public async Task Mutating_commands_and_shell_overrides_bypass_delay()
    {
        if (!OperatingSystem.IsLinux()) return;
        var guard = Prepare(CliSelection.CodexCli, 5);
        const string write = "cat ./sample > ./output";
        File.WriteAllText(Path.Combine(_root, "sample"), "test output\n");
        Assert.Equal(0, (await Bash(write, guard)).Exit);
        Assert.Equal(("", "", 0), await Bash(write, guard));

        var overrideResult = await Run("/bin/bash", ["-c", "cat ./sample"], start =>
        {
            start.Environment["BASH_ENV"] = guard.BashEnvironment;
            start.Environment["BASH_FUNC_cat%%"] = """() { printf 'override\n'; }""";
        });
        Assert.Equal(("override\n", "", 0), overrideResult);
        Assert.Equal(new StatusCommandSummary(3, 0, 0), Read(guard));
    }

    [Fact]
    public async Task Codex_only_installs_the_hook_in_tool_shells_and_counts_the_snapshot_wrapper_once()
    {
        if (!OperatingSystem.IsLinux()) return;
        var guard = Prepare(CliSelection.CodexCli, 2);
        var start = new ProcessStartInfo("codex") { ArgumentList = { "exec" } };
        start.Environment.Remove("BASH_ENV");
        guard.Apply(start, CliSelection.CodexCli);
        Assert.Equal("--config", start.ArgumentList[0]);
        Assert.StartsWith("shell_environment_policy.set.BASH_ENV=", start.ArgumentList[1]);
        Assert.False(start.Environment.ContainsKey("BASH_ENV"));

        var snapshot = Path.Combine(_root, "shell_snapshots", "snapshot.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        File.WriteAllText(snapshot, "export FROM_SNAPSHOT=1\n");
        var command = $"if . '{snapshot}' >/dev/null 2>&1; then :; fi\n"
            + "exec '/bin/bash' -c 'test -z \"${BASH_ENV-}\" && test \"$FROM_SNAPSHOT\" = 1'";
        Assert.Equal(("", "", 0), await Bash(command, guard));
        Assert.Equal(new StatusCommandSummary(1, 0, 0), Read(guard));
    }

    [Theory]
    [InlineData("tail -4 ./build.log | grep -E ' error |Build FAILED' || test ! -f ./build.log", true)]
    [InlineData("rg -n 'token' \"$JOB_RESULTS_DIR/build.log\" | tail -5; ps -p 12 -o etime,stat", true)]
    [InlineData("test -f ./log\n[ -r ./log ] && wc -l ./log", true)]
    [InlineData("date --utc '+%Y-%m-%d %H:%M:%S'", true)]
    [InlineData("rg '$(touch output)' ./log", true)]
    [InlineData("cat ./sample; dotnet build --no-restore", false)]
    [InlineData("rg -n 'pattern' ./log >> ./output", false)]
    [InlineData("cat ./log | tee ./output", false)]
    [InlineData("cat \"$(touch output)\"", false)]
    [InlineData("rg --pre=./tool 'x' ./log", false)]
    [InlineData("rg -e -- --pre=./tool ./log", false)]
    [InlineData("rg x *", false)]
    [InlineData("rg x ./logs/*", true)]
    [InlineData("rg --p=./tool x ./log", false)]
    [InlineData("date --set '12:00'", false)]
    [InlineData("date -us12:00", false)]
    [InlineData("tail -f ./log", false)]
    [InlineData("tail --pid=12 ./log", false)]
    [InlineData("for f in ./logs/*; do tail -1 \"$f\"; done", false)]
    [InlineData("ls & ps", false)]
    [InlineData("ls |", false)]
    public async Task Classifier_is_conservative(string command, bool expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        var guard = Prepare(CliSelection.CodexCli, 10);
        var script = Path.Combine(Path.GetDirectoryName(guard.BashEnvironment)!, "status_guard.py");
        var result = await Run("/usr/bin/python3",
            ["-I", "-B", "-c", "import runpy, sys; print(bool(runpy.run_path(sys.argv[1])['status_tools'](sys.argv[2])))",
                script, command], start => start.Environment["JOB_RESULTS_DIR"] = _root);
        Assert.Equal(0, result.Exit);
        Assert.Equal(expected ? "True\n" : "False\n", result.Out);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Claude_hook_reports_wait_without_rewriting_tool_input_or_consuming_its_timeout()
    {
        if (!OperatingSystem.IsLinux()) return;
        var guard = Prepare(CliSelection.ClaudeCli, 10);
        var start = new ProcessStartInfo("claude") { ArgumentList = { "-p" } };
        guard.Apply(start, CliSelection.ClaudeCli);
        Assert.Equal("--settings", start.ArgumentList[1]);
        using var settings = JsonDocument.Parse(File.ReadAllText(start.ArgumentList[2]));
        var hook = settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        var args = new[] { "-c", hook.GetProperty("command").GetString()! };
        var input = JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse", tool_name = "Bash", cwd = _root,
            tool_input = new { command = "head -1 ./log", timeout = 100 },
        });

        Assert.Equal(("", "", 0), await Run("/bin/bash", args, stdin: input));
        var repeated = await Run("/bin/bash", args, stdin: input);
        Assert.Empty(repeated.Error);
        Assert.Equal(0, repeated.Exit);
        using var result = JsonDocument.Parse(repeated.Out);
        var output = result.RootElement.GetProperty("hookSpecificOutput");
        Assert.Contains("this status command repeats", output.GetProperty("additionalContext").GetString());
        Assert.False(output.TryGetProperty("updatedInput", out _));
        Assert.Equal(new StatusCommandSummary(2, 1, 1), Read(guard));
    }

    [Fact]
    public async Task Reopening_an_attempt_preserves_the_counters_and_last_execution()
    {
        if (!OperatingSystem.IsLinux()) return;
        var guard = Prepare(CliSelection.CodexCli, 180);
        Assert.Empty((await Bash("ls", guard)).Error);

        var resumed = Prepare(CliSelection.CodexCli, 180);
        Assert.Contains("this status command repeats", (await Bash("ls", resumed)).Error);
        Assert.Equal(new StatusCommandSummary(2, 1, 1), Read(resumed));
    }

    [Fact]
    public void Explicit_off_and_non_coding_specs_do_not_install_the_hook()
    {
        var spec = Spec(CliSelection.CodexCli, 0);
        Assert.Null(StatusCommandGuard.Prepare(spec, CliSelection.CodexCli, (_, _) => { }));
        Assert.Null(StatusCommandGuard.Prepare(spec with { StatusGuardDirectory = null, StatusCommandIntervalSeconds = 180 },
            CliSelection.CodexCli, (_, _) => { }));
    }

    [Fact]
    public void Run_summary_roundtrips_and_uses_all_observed_calls_as_the_denominator()
    {
        var summary = new StatusCommandRunSummary("attempt-1", new StatusCommandSummary(20, 4, 5));
        Assert.Equal(summary, StatusCommandRunSummary.FromLogLine(summary.ToLogLine()));
        Assert.Equal(0.25, summary.Commands.RepeatedStatusShare);
        Assert.Null(StatusCommandRunSummary.FromLogLine(
            new StatusCommandRunSummary("attempt-2", new StatusCommandSummary(4, 5, 5)).ToLogLine()));
        Assert.Null(StatusCommandRunSummary.FromLogLine(StatusCommandRunSummary.LogPrefix + "{"));
    }

    private DetachedJobSpec Spec(string cli, int interval) => new(
        cli, [], _root, "", _root, 30, CliType: cli,
        StatusCommandIntervalSeconds: interval, StatusGuardDirectory: Path.Combine(_root, "guard"));

    private StatusCommandGuard Prepare(string cli, int interval)
        => Assert.IsType<StatusCommandGuard>(StatusCommandGuard.Prepare(Spec(cli, interval), cli, (_, _) => { }));

    private static StatusCommandSummary Read(StatusCommandGuard guard)
        => Assert.IsType<StatusCommandSummary>(guard.ReadSummary((_, _) => { }));

    private Task<(string Out, string Error, int Exit)> Bash(string command, StatusCommandGuard? guard = null)
        => Run("/bin/bash", ["-c", command], start =>
        {
            if (guard is not null) start.Environment["BASH_ENV"] = guard.BashEnvironment;
        });

    private async Task<(string Out, string Error, int Exit)> Run(string executable, IReadOnlyList<string> args,
        Action<ProcessStartInfo>? customize = null, string? stdin = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment.Remove("BASH_ENV");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        customize?.Invoke(start);
        using var process = Process.Start(start)!;
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return (await stdout, await stderr, process.ExitCode);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
