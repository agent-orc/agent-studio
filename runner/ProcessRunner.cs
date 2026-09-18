using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentRunner;

/// <summary>Result of running a child process to completion.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Minimal cross-platform process spawner. Used for git plumbing and for the
/// agent CLI. Streaming callbacks let the caller tee output to the console and
/// ship it to the server as it arrives, rather than buffering the whole run.
/// </summary>
public static class ProcessRunner
{
    // A single agent run can stream unbounded output (full-file dumps, large
    // diffs). The daemon lives for days across many such runs, so the retained
    // copy must be capped: keep only the tail of each stream under a hard byte
    // budget. The tail is all the caller needs - the terminal sentinel and the
    // final summary an agent signs off with are emitted last, so a bounded tail
    // keeps SentinelScanner's final-agent-reply scan available while a runaway run
    // can no longer grow the runner's heap without bound.
    private const int StdOutBudgetChars = 2 * 1024 * 1024; // ~2 MB tail
    private const int StdErrBudgetChars = 256 * 1024;      // ~256 KB tail (diagnostics only)

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? stdin = null,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool clearEnvironment = false,
        bool isolateProcessGroup = false,
        Action<int>? onStarted = null,
        CancellationToken ct = default)
    {
        var actualFileName = fileName;
        IReadOnlyList<string> actualArguments = arguments;
        if (isolateProcessGroup && OperatingSystem.IsLinux())
        {
            var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid"
                : File.Exists("/bin/setsid") ? "/bin/setsid"
                : throw new InvalidOperationException(
                    "Agent CLI process-group isolation requires the Linux 'setsid' utility.");
            actualFileName = setsid;
            actualArguments = [fileName, .. arguments];
        }

        var psi = new ProcessStartInfo
        {
            FileName = actualFileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        if (clearEnvironment) psi.Environment.Clear();
        foreach (var arg in actualArguments) psi.ArgumentList.Add(arg);
        if (environment != null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        // Each stream's DataReceived events are serialised by the runtime and the
        // two buffers are independent, so no locking is needed; WaitForExit() below
        // drains both readers before the result is materialised.
        var outBuf = new BoundedOutputBuffer(StdOutBudgetChars);
        var errBuf = new BoundedOutputBuffer(StdErrBudgetChars);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            outBuf.Append(e.Data);
            onStdOut?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            errBuf.Append(e.Data);
            onStdErr?.Invoke(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Handed out before the first await so a caller-owned watchdog can watch
        // the whole tree from the moment it exists.
        onStarted?.Invoke(process.Id);

        if (stdin != null)
        {
            try
            {
            await process.StandardInput.WriteAsync(stdin);
            }
            catch (IOException)
            {
                // A child may close stdin as it exits after already producing a
                // terminal response. Preserve its captured output and exit code
                // instead of replacing that truthful result with a pipe error.
            }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { /* the child already closed its pipe */ }
            }
        }

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process, isolateProcessGroup);
            throw;
        }

        // WaitForExitAsync returns before the async readers have flushed the last
        // buffered lines; a bare WaitForExit() here drains them deterministically.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, outBuf.ToString(), errBuf.ToString());
    }

    private static void TryKill(Process process, bool isolatedProcessGroup)
    {
        if (isolatedProcessGroup && OperatingSystem.IsLinux())
        {
            try
            {
                // AGT-2870: the process group is this child's own, because the
                // caller asked for an isolated one, but kill(-pgid) is a
                // broadcast for any pgid below 2 and Process.Id is 0 for a
                // start that failed. The guard refuses both; the runtime's
                // descendant-tree kill below still runs.
                if (!process.HasExited)
                    ProcessSignalGuard.TrySignalProcessGroup(
                        process.Id, SigKill, "process-runner-group");
            }
            catch (InvalidOperationException)
            {
                // Fall through to the runtime's descendant-tree kill.
            }
        }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort: the run is already being torn down */ }
    }

    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}

/// <summary>
/// Retains the tail of a child process stream under a hard character budget so a
/// long-running or runaway agent cannot grow the runner's heap without bound.
/// Oldest lines are evicted first; <see cref="ToString"/> prepends a one-line
/// elision notice when anything was dropped. At least the most recent line is
/// always kept, even if that single line exceeds the budget.
/// </summary>
internal sealed class BoundedOutputBuffer
{
    private readonly int _maxChars;
    private readonly Queue<string> _lines = new();
    private int _chars;
    private long _dropped;

    public BoundedOutputBuffer(int maxChars) => _maxChars = maxChars;

    public long DroppedLines => _dropped;

    public void Append(string line)
    {
        line ??= string.Empty;
        _lines.Enqueue(line);
        _chars += line.Length + 1; // account for the newline re-added on render
        while (_chars > _maxChars && _lines.Count > 1)
        {
            var evicted = _lines.Dequeue();
            _chars -= evicted.Length + 1;
            _dropped++;
        }
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder(_chars + 96);
        if (_dropped > 0)
            sb.Append("[runner] ").Append(_dropped)
              .Append(" earlier output line(s) elided to bound runner memory\n");
        foreach (var line in _lines) sb.Append(line).Append('\n');
        return sb.ToString();
    }
}
