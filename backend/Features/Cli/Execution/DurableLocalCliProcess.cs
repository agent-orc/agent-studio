using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodingAgentRunner.Abstractions;

namespace AgentStudio.Cli;

internal sealed record DurableLocalCliSpec(
    string FileName,
    string? Arguments,
    IReadOnlyList<string> ArgumentList,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    bool RedirectStandardInput);

internal sealed record DurableLocalCliIdentity(
    int ProcessId,
    DateTime ProcessStartedAtUtc,
    string WorkingDirectory);

internal sealed record DurableLocalCliLogLine(
    long Sequence,
    DateTime Timestamp,
    string Stream,
    string Text);

internal sealed record DurableLocalCliResult(
    int ExitCode,
    DateTime CompletedAtUtc,
    bool LaunchFailed = false,
    string? Error = null);

internal sealed record DurableLocalCliObservation(
    bool IsLive,
    DurableLocalCliResult? Result,
    string Detail);

/// <summary>
/// Durable process boundary for a Studio-local CLI invocation. The worker is
/// the process recorded in the active-jobs ledger; it owns the real CLI and
/// mirrors stdout, stderr, and the terminal result to disk. The worker
/// directory is the only transport between the two processes: the backend
/// writes the prompt into <c>input.bin</c> and reads live output back from
/// <c>output.jsonl</c>, so no anonymous pipe has to outlive the backend that
/// started the worker (AGT-2821). A replacement backend tails the same files
/// and verifies PID plus start time before adopting the run.
/// </summary>
internal sealed class DurableLocalCliProcess
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly object _cursorGate = new();
    private readonly HashSet<long> _deliveredBeyondCursor = [];
    private long _acknowledgedSequence;

    private DurableLocalCliProcess(string directory, int processId, DateTime processStartedAtUtc)
    {
        _directory = directory;
        ProcessId = processId;
        ProcessStartedAtUtc = processStartedAtUtc;
        _acknowledgedSequence = ReadCursor();
    }

    public int ProcessId { get; }
    public DateTime ProcessStartedAtUtc { get; }
    public string DirectoryPath => _directory;
    public string LogPath => Path.Combine(_directory, "output.jsonl");
    public string ResultPath => Path.Combine(_directory, "result.json");
    public string IdentityPath => Path.Combine(_directory, "worker.json");
    public string CursorPath => Path.Combine(_directory, "cursor.json");
    public string InputPath => Path.Combine(_directory, "input.bin");
    public string InputCompletedPath => Path.Combine(_directory, "input.done");

    /// <summary>
    /// Writable prompt channel for CAR. Writes land in the worker's input file
    /// and closing the stream publishes the completion marker, which is the
    /// worker's signal to close the CLI's stdin.
    /// </summary>
    public Stream OpenInput() => new DurableLocalCliInputStream(InputPath, InputCompletedPath);

    /// <summary>
    /// Live reader for one worker stream ("stdout" or "stderr"), tailing the
    /// same append-only log a replacement backend would read after a restart.
    /// </summary>
    public StreamReader OpenOutput(string stream)
        => new(
            new DurableLocalCliOutputStream(LogPath, ResultPath, ProcessId, stream),
            new UTF8Encoding(false));

    public static DurableLocalCliProcess Start(string workerDirectory, ProcessStartInfo cliStartInfo)
    {
        if (Directory.Exists(workerDirectory))
            Directory.Delete(workerDirectory, recursive: true);
        Directory.CreateDirectory(workerDirectory);

        var spec = new DurableLocalCliSpec(
            cliStartInfo.FileName,
            cliStartInfo.ArgumentList.Count == 0 ? cliStartInfo.Arguments : null,
            cliStartInfo.ArgumentList.ToArray(),
            Path.GetFullPath(cliStartInfo.WorkingDirectory),
            cliStartInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value),
            cliStartInfo.RedirectStandardInput);
        WriteAtomic(
            Path.Combine(workerDirectory, "spec.json"),
            JsonSerializer.Serialize(spec, Json));

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the Studio executable for durable worker launch.");
        var managedHost = string.Equals(
                              Path.GetFileNameWithoutExtension(executable),
                              "dotnet",
                              StringComparison.OrdinalIgnoreCase)
                          || executable.Contains("testhost", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = managedHost ? "dotnet" : executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory,
            // The worker talks to the backend through its directory, never
            // through these handles. They are redirected only so the detached
            // worker cannot inherit (or write to) the Studio console.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (managedHost) start.ArgumentList.Add(typeof(DurableLocalCliProcess).Assembly.Location);
        start.ArgumentList.Add("--durable-local-cli-worker");
        start.ArgumentList.Add(Path.Combine(workerDirectory, "spec.json"));

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start the durable local CLI worker.");
        var processId = process.Id;
        var processStartedAtUtc = process.StartTime.ToUniversalTime();
        process.Dispose();
        var worker = new DurableLocalCliProcess(workerDirectory, processId, processStartedAtUtc);
        var identityDeadline = DateTime.UtcNow.AddSeconds(5);
        while (worker.ReadIdentity() is null && worker.ReadResult() is null)
        {
            if (DateTime.UtcNow >= identityDeadline)
            {
                worker.Kill();
                throw new InvalidOperationException(
                    "Durable local CLI worker did not persist its identity within five seconds.");
            }
            Thread.Sleep(25);
        }
        return worker;
    }

    public static DurableLocalCliProcess Attach(
        string workerDirectory,
        int processId,
        DateTime processStartedAtUtc)
        => new(workerDirectory, processId, processStartedAtUtc);

    public DurableLocalCliObservation Inspect(string expectedWorkingDirectory)
    {
        var result = ReadResult();
        if (result is not null)
            return new DurableLocalCliObservation(false, result, "durable result ready");

        var live = VerifyLive(expectedWorkingDirectory, out var detail);
        if (live)
            return new DurableLocalCliObservation(true, null, detail);

        // Close the worker-exit race: result.json can appear between the first
        // read and the process-table observation.
        result = ReadResult();
        return result is not null
            ? new DurableLocalCliObservation(false, result, "durable result ready")
            : new DurableLocalCliObservation(false, null, detail);
    }

    public bool VerifyLive(string expectedWorkingDirectory, out string detail)
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (process.HasExited)
            {
                detail = "worker exited without a durable result";
                return false;
            }
            if (Math.Abs((process.StartTime.ToUniversalTime() - ProcessStartedAtUtc).TotalSeconds) > 2)
            {
                detail = "worker PID was reused (start time differs)";
                return false;
            }

            var identity = ReadIdentity();
            if (identity is null)
            {
                detail = "worker identity is not yet durable";
                return false;
            }
            if (identity.ProcessId != ProcessId
                || Math.Abs((identity.ProcessStartedAtUtc - ProcessStartedAtUtc).TotalSeconds) > 2
                || !PathsEqual(identity.WorkingDirectory, expectedWorkingDirectory))
            {
                detail = "worker identity does not match the recorded generation and working directory";
                return false;
            }

            detail = "live durable worker generation and working directory match";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or IOException)
        {
            detail = $"worker verification failed: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<DurableLocalCliLogLine> ReadUnacknowledged()
        => ReadAfter(Interlocked.Read(ref _acknowledgedSequence));

    public IReadOnlyList<DurableLocalCliLogLine> ReadAfter(long sequence)
    {
        if (!File.Exists(LogPath)) return [];
        var lines = new List<DurableLocalCliLogLine>();
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            try
            {
                var line = JsonSerializer.Deserialize<DurableLocalCliLogLine>(raw, Json);
                if (line is not null && line.Sequence > sequence) lines.Add(line);
            }
            catch (JsonException ex)
            {
                // The worker may be appending the final line. The next poll
                // observes it after the newline and JSON object are complete.
                SilentCatch.Note(ex, "DurableLocalCliProcess: partial output line");
            }
        }
        return lines.OrderBy(line => line.Sequence).ToArray();
    }

    public void Acknowledge(long sequence)
    {
        var current = Interlocked.Read(ref _acknowledgedSequence);
        if (sequence <= current) return;
        Interlocked.Exchange(ref _acknowledgedSequence, sequence);
        WriteAtomic(CursorPath, JsonSerializer.Serialize(sequence, Json));
    }

    public void AcknowledgeLiveLine(string stream, string text)
    {
        lock (_cursorGate)
        {
            var current = Interlocked.Read(ref _acknowledgedSequence);
            var delivered = ReadAfter(current).FirstOrDefault(line =>
                !_deliveredBeyondCursor.Contains(line.Sequence)
                && string.Equals(line.Stream, stream, StringComparison.OrdinalIgnoreCase)
                && string.Equals(line.Text, text, StringComparison.Ordinal));
            if (delivered is null) return;

            _deliveredBeyondCursor.Add(delivered.Sequence);
            var contiguous = current;
            while (_deliveredBeyondCursor.Remove(contiguous + 1)) contiguous++;
            if (contiguous == current) return;
            Interlocked.Exchange(ref _acknowledgedSequence, contiguous);
            WriteAtomic(CursorPath, JsonSerializer.Serialize(contiguous, Json));
        }
    }

    public DurableLocalCliResult? ReadResult()
    {
        if (!File.Exists(ResultPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<DurableLocalCliResult>(File.ReadAllText(ResultPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Process OpenProcess() => Process.GetProcessById(ProcessId);

    public void Kill()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Cancellation is already represented by the host stop reason.
            _ = ex;
        }
    }

    public void Delete()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex)
        {
            // Retention and the next identity-checked start can retry cleanup.
            _ = ex;
        }
    }

    public static async Task<int> RunWorkerAsync(string specPath)
    {
        var spec = JsonSerializer.Deserialize<DurableLocalCliSpec>(
                       await File.ReadAllTextAsync(specPath),
                       Json)
                   ?? throw new InvalidDataException($"Durable local CLI spec is empty: {specPath}");
        var directory = Path.GetDirectoryName(specPath)!;
        using (var current = Process.GetCurrentProcess())
        {
            WriteAtomic(
                Path.Combine(directory, "worker.json"),
                JsonSerializer.Serialize(
                    new DurableLocalCliIdentity(
                        current.Id,
                        current.StartTime.ToUniversalTime(),
                        Path.GetFullPath(spec.WorkingDirectory)),
                    Json));
        }

        long sequence = 0;
        var logGate = new object();
        await using var logStream = new FileStream(
            Path.Combine(directory, "output.jsonl"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous);
        // No byte-order mark: the log is a JSON-lines stream that the backend
        // parses line by line, not a document.
        await using var logWriter = new StreamWriter(logStream, new UTF8Encoding(false)) { AutoFlush = true };

        void Append(string stream, string text)
        {
            var line = new DurableLocalCliLogLine(
                Interlocked.Increment(ref sequence),
                DateTime.UtcNow,
                stream,
                text);
            lock (logGate) logWriter.WriteLine(JsonSerializer.Serialize(line, Json));
        }

        DurableLocalCliResult result;
        try
        {
            using var child = new Process
            {
                StartInfo = BuildChildStartInfo(spec),
                EnableRaisingEvents = true,
            };
            if (!child.Start())
                throw new InvalidOperationException("The durable worker could not start the CLI process.");

            var stdout = PumpLinesAsync(child.StandardOutput, "stdout", Append);
            var stderr = PumpLinesAsync(child.StandardError, "stderr", Append);
            var stdin = spec.RedirectStandardInput
                ? PumpInputAsync(directory, child)
                : Task.CompletedTask;

            await child.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            try { await stdin.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { _ = ex; /* the backend may never close the prompt file */ }
            result = new DurableLocalCliResult(child.ExitCode, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Append("system", $"[taskboard] durable local CLI worker failed: {ex.Message}");
            result = new DurableLocalCliResult(125, DateTime.UtcNow, true, ex.ToString());
        }

        WriteAtomic(
            Path.Combine(directory, "result.json"),
            JsonSerializer.Serialize(result, Json));
        return result.ExitCode;
    }

    private static ProcessStartInfo BuildChildStartInfo(DurableLocalCliSpec spec)
    {
        var start = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = spec.RedirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (spec.ArgumentList.Count > 0)
        {
            foreach (var argument in spec.ArgumentList) start.ArgumentList.Add(argument);
        }
        else
        {
            start.Arguments = spec.Arguments ?? string.Empty;
        }
        start.Environment.Clear();
        foreach (var pair in spec.Environment) start.Environment[pair.Key] = pair.Value;
        return start;
    }

    /// <summary>
    /// Drains one CLI stream into the append-only worker log. The log is the
    /// only output transport, so a backend that disappears mid-run costs
    /// nothing here and a replacement reads the same lines.
    /// </summary>
    private static async Task PumpLinesAsync(
        StreamReader source,
        string stream,
        Action<string, string> append)
    {
        while (await source.ReadLineAsync() is { } line) append(stream, line);
    }

    /// <summary>
    /// Forwards the backend's prompt file to the CLI's stdin. The backend
    /// appends bytes to <c>input.bin</c> and publishes <c>input.done</c> with
    /// the total byte count when it closes the stream; until that count is
    /// forwarded (or the CLI exits) the worker keeps tailing, so a prompt that
    /// is still being written is never truncated into an early EOF.
    /// </summary>
    private static async Task PumpInputAsync(string directory, Process child)
    {
        var inputPath = Path.Combine(directory, "input.bin");
        var completedPath = Path.Combine(directory, "input.done");
        var destination = child.StandardInput.BaseStream;
        var started = DateTime.UtcNow;
        long forwarded = 0;
        try
        {
            while (true)
            {
                forwarded += await ForwardInputAsync(inputPath, destination, forwarded);
                var expected = ReadExpectedInputLength(completedPath);
                if (expected is not null && forwarded >= expected) break;
                if (child.HasExited) break;
                // The one-shot prompt lands within milliseconds. A CLI that
                // keeps stdin open for a whole run does not need that cadence.
                await Task.Delay(DateTime.UtcNow - started < TimeSpan.FromSeconds(5)
                    ? TimeSpan.FromMilliseconds(20)
                    : TimeSpan.FromMilliseconds(250));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The CLI can close its own stdin (one-shot prompt already read).
            SilentCatch.Note(ex, "DurableLocalCliProcess: CLI stdin closed early");
        }
        finally
        {
            try { destination.Close(); }
            catch (Exception ex) { SilentCatch.Note(ex, "DurableLocalCliProcess: CLI stdin close"); }
        }
    }

    private static async Task<long> ForwardInputAsync(string path, Stream destination, long offset)
    {
        if (!File.Exists(path)) return 0;
        await using var source = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (source.Length <= offset) return 0;
        source.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[8192];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read));
            copied += read;
        }
        await destination.FlushAsync();
        return copied;
    }

    private static long? ReadExpectedInputLength(string completedPath)
    {
        if (!File.Exists(completedPath)) return null;
        try
        {
            return long.TryParse(
                File.ReadAllText(completedPath).Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var expected)
                ? expected
                : 0;
        }
        catch (IOException ex)
        {
            SilentCatch.Note(ex, "DurableLocalCliProcess: prompt completion marker unreadable");
            return null;
        }
    }

    private DurableLocalCliIdentity? ReadIdentity()
    {
        if (!File.Exists(IdentityPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<DurableLocalCliIdentity>(File.ReadAllText(IdentityPath), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private long ReadCursor()
    {
        if (!File.Exists(CursorPath)) return 0;
        try { return JsonSerializer.Deserialize<long>(File.ReadAllText(CursorPath), Json); }
        catch (Exception ex) when (ex is IOException or JsonException) { return 0; }
    }

    internal static void WriteAtomic(string path, string content)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            stream.Flush(flushToDisk: true);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}

/// <summary>CAR spawner decorator that substitutes the durable worker process.</summary>
internal sealed class DurableLocalCliProcessSpawner(
    string workerDirectory,
    Action<Process>? onSpawned = null) : ICliProcessSpawner
{
    public DurableLocalCliProcess? Worker { get; private set; }
    public Process? SpawnedProcess { get; private set; }

    public CliSpawn Spawn(ProcessStartInfo startInfo)
    {
        var worker = DurableLocalCliProcess.Start(workerDirectory, startInfo);
        Worker = worker;
        try
        {
            // The reopened worker handle never has redirected pipes, so the
            // run's streams come from the worker directory instead (AGT-2821).
            var process = worker.OpenProcess();
            SpawnedProcess = process;
            onSpawned?.Invoke(process);
            return new CliSpawn(
                process,
                startInfo.RedirectStandardInput ? worker.OpenInput() : Stream.Null,
                worker.OpenOutput("stdout"),
                worker.OpenOutput("stderr"));
        }
        catch
        {
            // The worker already owns a live CLI child; a spawn CAR never
            // receives must not leave that subtree running.
            worker.Kill();
            worker.Delete();
            Worker = null;
            SpawnedProcess = null;
            throw;
        }
    }
}
