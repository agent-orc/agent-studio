using System.Diagnostics;
using System.Net.Sockets;

namespace AgentStudio.Scenario;

/// <summary>
/// Thrown when a target could not be brought up. The process maps it to
/// <see cref="ScenarioExitCode.TargetUnavailable"/> so a missing build or a
/// missing Docker host never reads as a product regression.
/// </summary>
public sealed class ScenarioTargetException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// One child process of the scenario with its captured output. The scenario
/// starts the deployables as siblings of itself, exactly as an operator host
/// supervises them, so nothing is hosted inside the runner process.
/// </summary>
public sealed class ScenarioProcess : IDisposable
{
    private readonly List<string> _output = [];

    private ScenarioProcess(Process process, string name)
    {
        Process = process;
        Name = name;
    }

    public Process Process { get; }
    public string Name { get; }

    public static ScenarioProcess StartBuilt(
        string repositoryRoot,
        string projectDirectory,
        string assemblyName,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var assembly = Path.Combine(
            repositoryRoot, projectDirectory, "bin", configuration, "net10.0", assemblyName);
        if (!File.Exists(assembly))
            throw new ScenarioTargetException(
                $"Built component not found: {assembly}. Build the solution before the scenario "
                + "(dotnet build agent-taskboard.sln).");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Child request logs would bury the scenario's own step output and the
        // captured tail that a failure reports. Warnings and errors survive.
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;

        var process = Process.Start(start)
            ?? throw new ScenarioTargetException($"Could not start {assemblyName}.");
        var running = new ScenarioProcess(process, assemblyName);
        process.OutputDataReceived += (_, args) => running.Append(args.Data);
        process.ErrorDataReceived += (_, args) => running.Append(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return running;
    }

    public IReadOnlyList<string> OutputLines
    {
        get
        {
            lock (_output) return _output.ToArray();
        }
    }

    public bool IsRunning => !Process.HasExited;

    public void EnsureRunning()
    {
        if (Process.HasExited)
            throw new ScenarioTargetException(
                $"{Name} exited with code {Process.ExitCode}.{Environment.NewLine}{this}");
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await Process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ScenarioTargetException(
                $"{Name} did not exit within {timeout}.{Environment.NewLine}{this}");
        }
        return Process.ExitCode;
    }

    public void Stop()
    {
        if (Process.HasExited) return;
        Process.Kill(entireProcessTree: true);
        Process.WaitForExit(5000);
    }

    public void Dispose() => Stop();

    public override string ToString()
    {
        lock (_output) return string.Join(Environment.NewLine, _output.TakeLast(60));
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_output) _output.Add(line);
    }

    public static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
