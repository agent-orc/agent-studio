using System.Net;

namespace AgentStudio.Setup;

internal sealed class StudioDockerInstaller(string root, ProcessRunner processes, bool dryRun)
{
    private string EnvironmentPath => Path.Combine(root, ".env");
    private string CurrentPath => Path.Combine(root, "current-version.txt");
    private string PreviousPath => Path.Combine(root, "previous-version.txt");
    private string VersionRoot(string version) => Path.Combine(root, "versions", version);

    public async Task InstallAsync(string version, string? releaseDirectory, int port)
    {
        if (File.Exists(CurrentPath))
            throw new InvalidOperationException("Studio is already installed. Run 'agent-studio-setup update' to change its version.");
        await StageAsync(version, releaseDirectory);
        if (!dryRun)
        {
            Directory.CreateDirectory(root);
            CopyBundle(version);
            await File.WriteAllTextAsync(EnvironmentPath, InitialEnvironment(version, port));
            ProtectFile(EnvironmentPath);
        }
        else Console.WriteLine($"[dry-run] install Compose bundle in {root}");
        await StartAsync();
        if (!dryRun) await File.WriteAllTextAsync(CurrentPath, version);
        Console.WriteLine(dryRun
            ? $"[dry-run] would verify http://localhost:{port}/healthz and open the browser"
            : $"Agent Studio is ready at http://localhost:{port}. Configure a project and CLI credentials to run coding tasks.");
    }

    public async Task UpdateAsync(string version, string? releaseDirectory)
    {
        var current = ReadInstalledVersion();
        if (version == current)
        {
            Console.WriteLine($"Agent Studio {version} is already installed.");
            return;
        }
        await StageAsync(version, releaseDirectory);
        await ComposeAsync(["pull"], new Dictionary<string, string?> { ["AGENT_STUDIO_VERSION"] = version });
        if (!dryRun)
        {
            CopyBundle(version);
            await File.WriteAllTextAsync(EnvironmentPath, ReplaceVersion(await File.ReadAllTextAsync(EnvironmentPath), version));
        }
        try
        {
            await StartAsync();
            if (!dryRun)
            {
                await File.WriteAllTextAsync(PreviousPath, current);
                await File.WriteAllTextAsync(CurrentPath, version);
            }
        }
        catch
        {
            if (!dryRun)
            {
                CopyBundle(current);
                await File.WriteAllTextAsync(EnvironmentPath, ReplaceVersion(await File.ReadAllTextAsync(EnvironmentPath), current));
                await StartAsync();
            }
            throw;
        }
        Console.WriteLine($"Updated Agent Studio to {version}. Previous image tag {current} remains available for rollback.");
    }

    public async Task RollbackAsync()
    {
        var current = ReadInstalledVersion();
        if (!File.Exists(PreviousPath))
            throw new InvalidOperationException("There is no previous installed version to roll back to.");
        var previous = File.ReadAllText(PreviousPath).Trim();
        if (!Directory.Exists(VersionRoot(previous)))
            throw new InvalidOperationException($"The previous Compose bundle {previous} is missing.");
        if (!dryRun)
        {
            CopyBundle(previous);
            await File.WriteAllTextAsync(EnvironmentPath, ReplaceVersion(await File.ReadAllTextAsync(EnvironmentPath), previous));
        }
        await StartAsync();
        if (!dryRun)
        {
            await File.WriteAllTextAsync(CurrentPath, previous);
            await File.WriteAllTextAsync(PreviousPath, current);
        }
        Console.WriteLine($"Rolled back Agent Studio to {previous}.");
    }

    public async Task UninstallAsync(bool purge)
    {
        _ = ReadInstalledVersion();
        await ComposeAsync(purge ? ["down", "--volumes", "--remove-orphans"] : ["down", "--remove-orphans"]);
        if (!dryRun)
        {
            File.Delete(CurrentPath);
            File.Delete(PreviousPath);
            if (purge) Directory.Delete(root, recursive: true);
        }
        Console.WriteLine(purge
            ? "Studio containers and persistent volumes were removed."
            : "Studio containers were removed. Data volumes and configuration remain in place.");
    }

    private async Task StageAsync(string version, string? releaseDirectory)
    {
        await using var artifacts = new ReleaseArtifacts(version, releaseDirectory);
        var bundle = await artifacts.ExtractComposeAsync(default);
        if (dryRun) return;
        var destination = VersionRoot(version);
        if (Directory.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        CopyDirectory(bundle, destination);
    }

    private void CopyBundle(string version)
    {
        var source = VersionRoot(version);
        foreach (var path in new[]
                 {
                     "docker-compose.yml", "scripts/compose-secret-bootstrap.sh",
                     "scripts/compose-rotate-credentials.sh", "deploy/compose/Caddyfile.edge",
                     "deploy/compose/empty-credentials/git-credentials",
                 })
        {
            var from = Path.Combine(source, path);
            if (!File.Exists(from)) throw new InvalidDataException($"Compose bundle is missing {path}.");
            var to = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
        }
    }

    private async Task StartAsync()
    {
        await ComposeAsync(["up", "-d", "--wait"]);
        if (dryRun) return;
        var env = await File.ReadAllTextAsync(EnvironmentPath);
        var portLine = env.Split('\n').FirstOrDefault(line => line.StartsWith("STUDIO_UI_PORT=", StringComparison.Ordinal));
        var port = int.Parse(portLine?["STUDIO_UI_PORT=".Length..] ?? "4011");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException($"Compose started but Studio did not answer http://127.0.0.1:{port}/healthz.");
    }

    private Task ComposeAsync(IEnumerable<string> command, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var args = new List<string> { "compose", "--project-directory", root, "--env-file", EnvironmentPath,
            "-f", Path.Combine(root, "docker-compose.yml") };
        args.AddRange(command);
        return processes.RequireAsync("docker", args, environment);
    }

    private string ReadInstalledVersion()
    {
        if (!File.Exists(CurrentPath))
            throw new InvalidOperationException($"No Studio installation was found at {root}.");
        return SetupOptions.NormalizeVersion(File.ReadAllText(CurrentPath))!;
    }

    internal static string InitialEnvironment(string version, int port)
        => $"AGENT_STUDIO_VERSION={version}\nSTUDIO_UI_BIND=127.0.0.1\nSTUDIO_UI_PORT={port}\nSTUDIO_TASKSERVER_PORT=5071\n";

    internal static string ReplaceVersion(string content, string version)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var found = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("AGENT_STUDIO_VERSION=", StringComparison.Ordinal)) continue;
            lines[i] = $"AGENT_STUDIO_VERSION={version}";
            found = true;
        }
        if (!found) throw new InvalidDataException("Installed .env has no AGENT_STUDIO_VERSION entry.");
        return string.Join('\n', lines);
    }

    private static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
}
