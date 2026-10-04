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
    /// <summary>Starts an arbitrary long-lived child command under the same captured, exact-PID lifetime contract as repository binaries.</summary>
    public static ManagedProcess StartProcess(
        string file,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {file}.");
        var managed = new ManagedProcess(process);
        process.OutputDataReceived += (_, eventArgs) => managed.Append(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => managed.Append(eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return managed;
    }

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
        var candidates = BuiltAssemblyCandidates(repositoryRoot, projectDirectory, assemblyName);
        var assembly = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "Built component was not found. Build the solution before running this harness: "
                + string.Join(" or ", candidates),
                candidates[0]);

        return StartProcess(
            "dotnet",
            new[] { assembly }.Concat(arguments).ToArray(),
            repositoryRoot,
            environment);
    }

    /// <summary>
    /// Where the build put a sibling component, for both output layouts: the
    /// classic <c>&lt;project&gt;/bin/&lt;Config&gt;/net10.0/</c> and the artifacts layout
    /// <c>&lt;ArtifactsPath&gt;/bin/&lt;ProjectName&gt;/&lt;config&gt;/</c> that
    /// <c>dotnet test -p:ArtifactsPath=...</c> (the documented isolation recipe on a
    /// host with a running backend) produces. The layout is read from where the
    /// running test assembly itself was built.
    /// </summary>
    private static IReadOnlyList<string> BuiltAssemblyCandidates(
        string repositoryRoot,
        string projectDirectory,
        string assemblyName)
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var candidates = new List<string>();
        if (testOutput.Parent?.Parent is { Name: "bin" } artifactsBin
            && !testOutput.Name.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var projectFile = Directory.Exists(Path.Combine(repositoryRoot, projectDirectory))
                ? Directory.EnumerateFiles(Path.Combine(repositoryRoot, projectDirectory), "*.csproj").FirstOrDefault()
                : null;
            if (projectFile is not null)
                candidates.Add(Path.Combine(
                    artifactsBin.FullName, Path.GetFileNameWithoutExtension(projectFile), testOutput.Name, assemblyName));
        }
        var configuration = testOutput.Parent?.Name ?? "Debug";
        candidates.Add(Path.Combine(repositoryRoot, projectDirectory, "bin", configuration, "net10.0", assemblyName));
        return candidates;
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
