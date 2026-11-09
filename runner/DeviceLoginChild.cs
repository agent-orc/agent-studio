using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Porta.Pty;

namespace AgentRunner;

/// <summary>
/// The provider CLI process of one device sign-in. Output arrives line by line on the callback
/// and is never logged; only the executor parses it.
/// </summary>
internal interface IDeviceLoginChild : IDisposable
{
    /// <summary>Starts the CLI; false when the process could not start.</summary>
    Task<bool> StartAsync(Action<string> onLine, CancellationToken ct);

    /// <summary>Waits for the exit, then for the remaining output, and returns the exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>Types the authorization code once and presses Enter.</summary>
    Task SubmitCodeAsync(string code, CancellationToken ct);

    /// <summary>Stops the CLI and its process tree.</summary>
    void Stop();
}

/// <summary>
/// Redirected pipes for a CLI that needs no input. Stdin is closed at start, so the CLI never
/// reads the daemon's stdin.
/// </summary>
internal sealed class PipedDeviceLoginChild(string binary, IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string> environment, Action<string>? log) : IDeviceLoginChild
{
    private readonly Process _process = new();
    private bool _started;

    public Task<bool> StartAsync(Action<string> onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo(binary)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) info.Environment[key] = value;
        _process.StartInfo = info;
        if (!_process.Start()) return Task.FromResult(false);
        _started = true;
        _process.StandardInput.Close();
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.FromResult(true);
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        _process.WaitForExit(); // Drain redirected DataReceived callbacks before the caller reads results.
        return _process.ExitCode;
    }

    public Task SubmitCodeAsync(string code, CancellationToken ct)
        => throw new NotSupportedException("A piped device sign-in takes no authorization code.");

    public void Stop()
    {
        if (!_started) return;
        DeviceLoginChildStop.KillTree(_process, log);
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// A pseudo-terminal for <c>claude setup-token</c> (AGT-3243). The CLI renders its code prompt
/// with Ink, which puts stdin into raw mode and refuses a stdin that is not a terminal, so a plain
/// pipe cannot deliver the pasted code. The terminal is wide enough that the long authorize URL and
/// the setup token stay on one line each. Terminal output merges stdout and stderr.
/// </summary>
internal sealed class TerminalDeviceLoginChild(string binary, IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string> environment, TimeProvider clock, Action<string>? log) : IDeviceLoginChild
{
    internal const int Columns = 4000;
    internal const int Rows = 50;

    /// <summary>
    /// Separates the typed code from Enter, so the CLI reads a paste and then a key press instead
    /// of one paste that contains a line break.
    /// </summary>
    internal static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Bounds the wait for the last output after the exit, in case a grandchild keeps the terminal open.</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private const int MaxLineLength = 64 * 1024;

    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPtyConnection? _connection;
    private Task _reader = Task.CompletedTask;

    public async Task<bool> StartAsync(Action<string> onLine, CancellationToken ct)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) variables[key] = value;
        variables["TERM"] = "xterm-256color";
        variables["NO_COLOR"] = "1";
        variables["CLAUDE_CODE_DISABLE_AUTOUPDATER"] = "1";
        variables["DISABLE_AUTOUPDATER"] = "1";
        foreach (var (key, value) in environment) variables[key] = value;
        var connection = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "xterm-256color", Cols = Columns, Rows = Rows, Cwd = Environment.CurrentDirectory,
            App = binary, CommandLine = [.. arguments], Environment = variables,
        }, ct);
        _connection = connection;
        connection.ProcessExited += (_, e) => _exited.TrySetResult(e.ExitCode);
        if (connection.WaitForExit(0)) _exited.TrySetResult(connection.ExitCode);
        _reader = Task.Run(() => ReadAsync(connection.ReaderStream, onLine), CancellationToken.None);
        return true;
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        var exitCode = await _exited.Task.WaitAsync(ct);
        try
        {
            await _reader.WaitAsync(DrainTimeout, clock, ct);
        }
        catch (TimeoutException)
        {
            log?.Invoke("device-login terminal output did not close after the CLI exited");
        }
        return exitCode;
    }

    public async Task SubmitCodeAsync(string code, CancellationToken ct)
    {
        var writer = _connection?.WriterStream
            ?? throw new InvalidOperationException("The sign-in terminal is not running.");
        await writer.WriteAsync(Encoding.UTF8.GetBytes(code), ct);
        await writer.FlushAsync(ct);
        await Task.Delay(EnterDelay, clock, ct);
        await writer.WriteAsync("\r"u8.ToArray(), ct);
        await writer.FlushAsync(ct);
    }

    public void Stop()
    {
        if (_connection is not { } connection || _exited.Task.IsCompleted) return;
        Process process;
        try
        {
            process = Process.GetProcessById(connection.Pid);
        }
        catch (ArgumentException)
        {
            return; // The CLI already exited.
        }
        using (process) DeviceLoginChildStop.KillTree(process, log);
    }

    public void Dispose() => _connection?.Dispose();

    /// <summary>
    /// Splits the terminal stream into lines on CR and LF. The terminal reports the end of the
    /// stream as an I/O error once the CLI side is closed.
    /// </summary>
    private static async Task ReadAsync(Stream stream, Action<string> onLine)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var line = new StringBuilder();
        while (true)
        {
            int count;
            try
            {
                count = await stream.ReadAsync(bytes);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                count = 0; // EIO: the CLI closed its side of the terminal.
            }
            if (count <= 0) break;
            var decoded = decoder.GetChars(bytes, 0, count, characters, 0);
            for (var index = 0; index < decoded; index++)
            {
                var character = characters[index];
                if (character is '\r' or '\n' || line.Length >= MaxLineLength)
                {
                    if (line.Length > 0) onLine(line.ToString());
                    line.Clear();
                    if (character is '\r' or '\n') continue;
                }
                line.Append(character);
            }
        }
        if (line.Length > 0) onLine(line.ToString());
    }
}

internal static class DeviceLoginChildStop
{
    public static void KillTree(Process process, Action<string>? log)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { return; } // Exit raced the stop request.
        catch (Exception ex) when (ex is Win32Exception or AggregateException or NotSupportedException)
        {
            log?.Invoke($"device-login process stop failed: {ex.GetType().Name}");
        }
    }
}
