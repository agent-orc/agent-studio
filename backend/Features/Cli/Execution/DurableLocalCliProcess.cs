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
/// mirrors stdout, stderr, and the terminal result to disk. Anonymous pipes are
/// therefore only a live-delivery optimization. A replacement backend tails
/// the files and verifies PID plus start time before adopting the same run.
/// </summary>
internal sealed class DurableLocalCliProcess
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
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
            RedirectStandardInput = spec.RedirectStandardInput,
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
        await using var logWriter = new StreamWriter(logStream, Encoding.UTF8) { AutoFlush = true };

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

            var stdout = PumpLinesAsync(child.StandardOutput, Console.Out, "stdout", Append);
            var stderr = PumpLinesAsync(child.StandardError, Console.Error, "stderr", Append);
            var stdin = spec.RedirectStandardInput
                ? PumpInputAsync(Console.OpenStandardInput(), child.StandardInput.BaseStream)
                : Task.CompletedTask;

            await child.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            try { await stdin.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { _ = ex; /* parent stdin can remain open until worker exit */ }
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

    private static async Task PumpLinesAsync(
        StreamReader source,
        TextWriter destination,
        string stream,
        Action<string, string> append)
    {
        var liveDestination = true;
        while (await source.ReadLineAsync() is { } line)
        {
            append(stream, line);
            if (!liveDestination) continue;
            try
            {
                await destination.WriteLineAsync(line);
                await destination.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The Studio pipe is an optimization. Once its reader goes
                // away, the worker must keep draining the CLI into output.jsonl.
                liveDestination = false;
                SilentCatch.Note(ex, "DurableLocalCliProcess: Studio output pipe disconnected");
            }
        }
    }

    private static async Task PumpInputAsync(Stream source, Stream destination)
    {
        try
        {
            await source.CopyToAsync(destination);
            await destination.FlushAsync();
        }
        catch (IOException ex)
        {
            // The backend can disappear after writing the one-shot prompt. EOF
            // closes the child input while the detached worker keeps running.
            SilentCatch.Note(ex, "DurableLocalCliProcess: parent stdin disconnected");
        }
        finally
        {
            try { destination.Close(); }
            catch (Exception ex) { _ = ex; }
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

    private static void WriteAtomic(string path, string content)
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
        Worker = DurableLocalCliProcess.Start(workerDirectory, startInfo);
        var process = Worker.OpenProcess();
        SpawnedProcess = process;
        onSpawned?.Invoke(process);
        return new CliSpawn(
            process,
            startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null,
            process.StandardOutput,
            process.StandardError);
    }
}
