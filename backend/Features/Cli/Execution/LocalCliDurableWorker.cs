using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Cli;

internal sealed record LocalCliWorkerSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput,
    IReadOnlyDictionary<string, string?> Environment);

internal sealed record LocalCliWorkerLine(
    long Sequence,
    DateTime Timestamp,
    string Stream,
    string Text);

internal sealed record LocalCliWorkerResult(
    int ExitCode,
    DateTime CompletedAtUtc,
    bool LaunchFailed = false,
    string? Error = null);

internal sealed record LocalCliWorkerIdentity(
    int ProcessId,
    DateTime ProcessStartedAtUtc,
    string WorkingDirectory);

/// <summary>
/// File-backed worker used for local CLI execution. The worker is a process
/// boundary outside the Studio host, so a host restart loses only the live
/// observer. The command, output, process generation, and terminal result stay
/// on disk and can be adopted by the replacement host.
/// </summary>
internal static class LocalCliDurableWorker
{
    internal const string WorkerArgument = "--local-cli-worker";
    internal const string SpecFileName = "spec.json";
    internal const string OutputFileName = "output.jsonl";
    internal const string ResultFileName = "result.json";
    internal const string IdentityFileName = "worker.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // output.jsonl must remain one JSON object per physical line. The same
        // compact options are valid for the small atomic spec/result files.
        WriteIndented = false,
    };

    private static readonly HashSet<string> PersistedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PYTHONIOENCODING", "LC_ALL", "LANG", "NODE_NO_WARNINGS", "NO_COLOR", "FORCE_COLOR",
        "CLAUDE_CODE_DISABLE_AUTOUPDATER", "GEMINI_NO_UPDATE_NOTIFIER", "CODEX_DISABLE_TIP_OF_THE_DAY",
        "CI", "MSBUILDDISABLENODEREUSE", "DOTNET_CLI_USE_MSBUILD_SERVER", "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO", "JOB_RESULTS_DIR", "CLAUDE_CONFIG_DIR", "CODEX_HOME",
    };

    internal static LocalCliWorkerSpec BuildSpec(ProcessStartInfo startInfo, string? stdin)
    {
        if (startInfo.ArgumentList.Count == 0 && !string.IsNullOrWhiteSpace(startInfo.Arguments))
            throw new InvalidOperationException("Durable local workers require ProcessStartInfo.ArgumentList so arguments remain lossless.");

        var environment = startInfo.Environment
            .Where(pair => PersistedEnvironment.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new LocalCliWorkerSpec(
            startInfo.FileName,
            startInfo.ArgumentList.ToArray(),
            Path.GetFullPath(startInfo.WorkingDirectory),
            stdin,
            environment);
    }

    internal static Process Start(string directory, LocalCliWorkerSpec spec)
    {
        Directory.CreateDirectory(directory);
        var specPath = Path.Combine(directory, SpecFileName);
        WriteAtomic(specPath, JsonSerializer.Serialize(spec, Json));

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the Studio executable for durable worker launch.");
        var managedHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                          || executable.Contains("testhost", StringComparison.OrdinalIgnoreCase);
        var workerExecutable = managedHost ? "dotnet" : executable;
        var arguments = new List<string>();
        if (managedHost) arguments.Add(typeof(LocalCliDurableWorker).Assembly.Location);
        arguments.Add(WorkerArgument);
        arguments.Add(specPath);

        return OperatingSystem.IsWindows()
            ? StartWindowsBreakaway(workerExecutable, arguments, spec.WorkingDirectory)
            : StartUnixDetached(workerExecutable, arguments, spec.WorkingDirectory);
    }

    private static Process StartUnixDetached(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (string.Equals(start.FileName, "/usr/bin/setsid", StringComparison.Ordinal))
            start.ArgumentList.Add(executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Failed to start the durable local worker.");
    }

    private static Process StartWindowsBreakaway(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var commandLine = QuoteWindows(executable) + " " + string.Join(" ", arguments.Select(QuoteWindows));
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        const uint createBreakawayFromJob = 0x01000000;
        const uint createNewProcessGroup = 0x00000200;
        const uint createNoWindow = 0x08000000;
        if (!CreateProcess(
                null,
                new StringBuilder(commandLine),
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                createBreakawayFromJob | createNewProcessGroup | createNoWindow,
                IntPtr.Zero,
                workingDirectory,
                ref startup,
                out var processInfo))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not launch the local CLI worker outside the Studio job object.");
        }

        try { return Process.GetProcessById(unchecked((int)processInfo.dwProcessId)); }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
    }

    private static string QuoteWindows(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    internal static async Task<int> RunAsync(string specPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(specPath))!;
        var spec = JsonSerializer.Deserialize<LocalCliWorkerSpec>(await File.ReadAllTextAsync(specPath), Json)
            ?? throw new InvalidDataException($"Durable local worker spec is empty: {specPath}");
        using (var current = Process.GetCurrentProcess())
        {
            var identity = new LocalCliWorkerIdentity(
                current.Id,
                current.StartTime.ToUniversalTime(),
                Path.GetFullPath(spec.WorkingDirectory));
            WriteAtomic(Path.Combine(directory, IdentityFileName), JsonSerializer.Serialize(identity, Json));
        }

        var outputPath = Path.Combine(directory, OutputFileName);
        var resultPath = Path.Combine(directory, ResultFileName);
        long sequence = 0;
        var writeGate = new object();
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
        void Append(string stream, string text)
        {
            var line = new LocalCliWorkerLine(Interlocked.Increment(ref sequence), DateTime.UtcNow, stream, text);
            lock (writeGate) writer.WriteLine(JsonSerializer.Serialize(line, Json));
        }

        LocalCliWorkerResult result;
        try
        {
            using var child = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = spec.FileName,
                    WorkingDirectory = spec.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = spec.StandardInput is not null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            foreach (var argument in spec.Arguments) child.StartInfo.ArgumentList.Add(argument);
            foreach (var pair in spec.Environment) child.StartInfo.Environment[pair.Key] = pair.Value;
            AgentGitCommandGuard.Apply(child.StartInfo);
            child.Start();
            async Task PumpAsync(StreamReader reader, string stream)
            {
                while (await reader.ReadLineAsync() is { } line) Append(stream, line);
            }
            var stdout = PumpAsync(child.StandardOutput, "stdout");
            var stderr = PumpAsync(child.StandardError, "stderr");
            if (spec.StandardInput is not null)
            {
                await child.StandardInput.WriteAsync(spec.StandardInput);
                await child.StandardInput.FlushAsync();
                child.StandardInput.Close();
            }
            await child.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            result = new LocalCliWorkerResult(child.ExitCode, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Append("system", $"[taskboard] Durable local worker launch failed: {ex.Message}");
            result = new LocalCliWorkerResult(125, DateTime.UtcNow, LaunchFailed: true, Error: ex.ToString());
        }

        writer.Flush();
        output.Flush(flushToDisk: true);
        WriteAtomic(resultPath, JsonSerializer.Serialize(result, Json));
        return 0;
    }

    internal static IReadOnlyList<LocalCliWorkerLine> ReadAfter(string directory, long sequence)
    {
        var path = Path.Combine(directory, OutputFileName);
        if (!File.Exists(path)) return [];
        var result = new List<LocalCliWorkerLine>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            try
            {
                var line = JsonSerializer.Deserialize<LocalCliWorkerLine>(raw, Json);
                if (line is not null && line.Sequence > sequence) result.Add(line);
            }
            catch (JsonException ex)
            {
                // The worker may be appending the final JSON object. The next
                // poll observes the complete line.
                SilentCatch.Note(ex, "LocalCliDurableWorker: tolerate a partially appended output row.");
            }
        }
        return result.OrderBy(line => line.Sequence).ToArray();
    }

    internal static LocalCliWorkerResult? ReadResult(string directory)
    {
        var path = Path.Combine(directory, ResultFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LocalCliWorkerResult>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    internal static bool VerifyLive(
        int processId,
        DateTime? processStartedAtUtc,
        string workingDirectory,
        out Process? process,
        out string detail)
    {
        process = null;
        if (processStartedAtUtc is null)
        {
            detail = "worker start time is missing";
            return false;
        }
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                process = null;
                detail = "worker exited";
                return false;
            }
            if (Math.Abs((process.StartTime.ToUniversalTime() - processStartedAtUtc.Value).TotalSeconds) > 2)
            {
                process.Dispose();
                process = null;
                detail = "worker PID was reused";
                return false;
            }
            if (OperatingSystem.IsLinux())
            {
                var cwd = new DirectoryInfo($"/proc/{processId}/cwd").ResolveLinkTarget(true)?.FullName;
                if (string.IsNullOrWhiteSpace(cwd)
                    || !string.Equals(Path.GetFullPath(cwd), Path.GetFullPath(workingDirectory), StringComparison.Ordinal))
                {
                    process.Dispose();
                    process = null;
                    detail = $"worker cwd '{cwd ?? "unavailable"}' does not match '{workingDirectory}'";
                    return false;
                }
            }
            detail = "worker PID generation and working directory match";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            process = null;
            detail = $"worker verification failed: {ex.Message}";
            return false;
        }
    }

    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
