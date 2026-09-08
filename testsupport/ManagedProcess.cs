using System.Diagnostics;

namespace AgentStudio.TestSupport;

/// <summary>
/// Wraps a child <see cref="Process"/> started by a harness that owns its exact
/// lifetime (a topology or scenario test spawning built binaries as sibling
/// processes). Captures interleaved stdout/stderr for assertions and failure
/// messages, and kills the whole process tree on <see cref="Stop"/>/<see cref="Dispose"/>
/// so a crashed child never leaks past the test that started it.
/// </summary>
public sealed class ManagedProcess(Process process) : IDisposable
{
    private readonly List<string> _output = [];

    public Process Process { get; } = process;

    public IReadOnlyList<string> OutputLines
    {
        get
        {
            lock (_output) return _output.ToArray();
        }
    }

    public void Append(string? line)
    {
        if (line is null) return;
        lock (_output) _output.Add(line);
    }

    public void Stop()
    {
        if (Process.HasExited) return;
        Process.Kill(entireProcessTree: true);
        Process.WaitForExit(5000);
    }

    public bool Contains(string text)
    {
        lock (_output)
            return _output.Any(line => line.Contains(text, StringComparison.Ordinal));
    }

    public void Dispose() => Stop();

    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await Process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Process {Process.Id} did not exit within {timeout}. Output: {this}");
        }
    }

    /// <summary>Throws if the process already exited, so a poll loop fails fast instead of timing out.</summary>
    public void EnsureRunning()
    {
        if (Process.HasExited)
            throw new InvalidOperationException(
                $"Process exited early with code {Process.ExitCode}. Output:{Environment.NewLine}{this}");
    }

    public override string ToString()
    {
        lock (_output) return string.Join(Environment.NewLine, _output.TakeLast(80));
    }
}
