using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal sealed class StatusCommandGuard(string directory)
{
    private const string Python = "/usr/bin/python3";
    private string ScriptPath => Path.Combine(directory, "status_guard.py");
    private string DatabasePath => Path.Combine(directory, "calls.sqlite3");
    internal string BashEnvironment => Path.Combine(directory, "bash-env.sh");
    private string ClaudeSettings => Path.Combine(directory, "claude-hooks.json");

    public void Apply(ProcessStartInfo start, string cliType)
    {
        if (cliType == CliSelection.ClaudeCli)
        {
            SignalRecordingCliSpawner.AddArguments(start.ArgumentList, ["--settings", ClaudeSettings]);
        }
        else
        {
            // Apply the startup file to exec tools only. Inheriting BASH_ENV
            // at CLI startup would change how Codex captures its shell snapshot.
            start.ArgumentList.Insert(0, "shell_environment_policy.set.BASH_ENV=" + JsonSerializer.Serialize(BashEnvironment));
            start.ArgumentList.Insert(0, "--config");
        }
    }

    public static int WaitCapSeconds(string cliType) => CliSelection.NormalizeCliType(cliType) switch
    {
        // Claude's native hook runs before Bash's own per-call timer.
        CliSelection.ClaudeCli => 180,
        // Codex unified exec can yield a session; its one-shot tool has a 10 s deadline.
        _ => 8,
    };

    public static StatusCommandGuard? Prepare(DetachedJobSpec spec, string cliType, Action<string, string> append)
    {
        if (spec.StatusGuardDirectory is not { } directory || spec.StatusCommandIntervalSeconds <= 0)
            return null;
        if (!OperatingSystem.IsLinux() || !File.Exists(Python))
        {
            append("system", "[runner] status command guard requires Linux Bash and /usr/bin/python3");
            return null;
        }

        var guard = new StatusCommandGuard(directory);
        try
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(guard.ScriptPath, Resource("status_guard.py"));
            var interval = spec.StatusCommandIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            var cap = WaitCapSeconds(cliType).ToString(CultureInfo.InvariantCulture);
            if (cliType == CliSelection.ClaudeCli)
            {
                // Claude rebuilds the Bash tool environment after startup, so an
                // inherited BASH_ENV is seen by its snapshot probe, but not its tool.
                var command = $"{Python} -I -B {Quote(guard.ScriptPath)} claude-hook {Quote(guard.DatabasePath)} {interval} {cap}";
                File.WriteAllText(guard.ClaudeSettings, JsonSerializer.Serialize(new
                {
                    hooks = new
                    {
                        PreToolUse = new[] { new
                        {
                            matcher = "Bash",
                            hooks = new[] { new { type = "command", command, timeout = 200 } },
                        } },
                    },
                }));
            }
            else
            {
                File.WriteAllText(guard.BashEnvironment, Resource("bash-env.sh")
                    .Replace("INTERVAL", interval, StringComparison.Ordinal)
                    .Replace("CAP", cap, StringComparison.Ordinal));
            }
            return guard;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            append("system", $"[runner] status command guard could not initialize: {ex.Message}");
            return null;
        }
    }

    public StatusCommandSummary? ReadSummary(Action<string, string> append)
    {
        if (!File.Exists(DatabasePath)) return new(0, 0, 0);
        try
        {
            var start = new ProcessStartInfo(Python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                ArgumentList = { "-I", "-B", ScriptPath, "summarize", DatabasePath },
            };
            using var child = Process.Start(start);
            if (child is null) return null;
            if (!child.WaitForExit(5000))
            {
                child.Kill();
                return null;
            }
            var fields = child.StandardOutput.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (child.ExitCode == 0 && fields.Length == 3
                && long.TryParse(fields[0], out var calls) && long.TryParse(fields[1], out var delayed)
                && long.TryParse(fields[2], out var repeated))
                return new StatusCommandSummary(calls, delayed, repeated);
            append("system", "[runner] status command guard returned invalid counters");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            append("system", $"[runner] status command guard counters were unavailable: {ex.Message}");
        }
        return null;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string Resource(string name)
    {
        using var stream = typeof(StatusCommandGuard).Assembly.GetManifestResourceStream("AgentRunner.StatusGuard." + name)
            ?? throw new InvalidOperationException($"Missing status guard resource '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
