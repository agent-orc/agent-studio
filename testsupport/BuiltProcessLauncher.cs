using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AgentStudio.TestSupport;

/// <summary>
/// Starts already-built repository binaries (<c>dotnet &lt;project&gt;/bin/&lt;config&gt;/net10.0/&lt;dll&gt;</c>)
/// as sibling OS processes, the way a real deployment would run them, instead of
/// hosting them in-process. Shared by <c>task-server.Tests/TopologyTests.cs</c>
/// and the deployment regression scenario harness so both topology proofs boot
/// components identically.
/// </summary>
public static class BuiltProcessLauncher
{
    public static ManagedProcess StartBuilt(
        string repositoryRoot,
        string projectDirectory,
        string assemblyName,
        params string[] arguments)
        => StartBuilt(repositoryRoot, projectDirectory, assemblyName, environment: null, arguments);

    public static ManagedProcess StartBuilt(
        string repositoryRoot,
        string projectDirectory,
        string assemblyName,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var assembly = Path.Combine(repositoryRoot, projectDirectory, "bin", configuration, "net10.0", assemblyName);
        if (!File.Exists(assembly))
            throw new FileNotFoundException(
                $"Built component was not found. Build the solution before running this harness: {assembly}",
                assembly);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {assemblyName}.");
        var managed = new ManagedProcess(process);
        process.OutputDataReceived += (_, eventArgs) => managed.Append(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => managed.Append(eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return managed;
    }

    /// <summary>Runs a short-lived command to completion (e.g. a `git` step seeding a fixture) and throws with captured output on a non-zero exit.</summary>
    public static async Task RunAsync(string file, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {file}.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{file} {string.Join(' ', arguments)} exited {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    /// <summary>Binds an ephemeral loopback port and releases it immediately; a best-effort probe, same race as any "find a free port" helper.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
