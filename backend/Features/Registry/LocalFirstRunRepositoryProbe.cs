using System.Diagnostics;
using System.Collections.Concurrent;

namespace AgentStudio.Registry;

/// <summary>Live checkout and origin proof for the local Studio profile.</summary>
public interface ILocalFirstRunGitCommand
{
    Task<(bool Success, string Output)> RunAsync(
        string directory, IReadOnlyList<string> args, CancellationToken ct);

    Task<(bool Success, string Output)> RunAsync(string directory, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> environment, CancellationToken ct)
        => RunAsync(directory, args, ct);
}

public sealed class LocalFirstRunRepositoryProbe(TimeProvider clock, ILocalFirstRunGitCommand? git = null)
{
    private readonly ILocalFirstRunGitCommand _git = git ?? new LocalFirstRunGitCommand();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _verified = new(StringComparer.Ordinal);

    public async Task<object?> ReadAsync(ProjectRecord project, string integrationRef,
        CancellationToken ct, bool force = false, string? keyPath = null)
    {
        var origin = project.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase))?.Url;
        var path = project.RepositoryPath;
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(path)
            || !Directory.Exists(path)) return null;

        var cacheKey = string.Join('\n', project.Id, origin, path, integrationRef, keyPath);
        var now = clock.GetUtcNow();
        if (!force && _verified.TryGetValue(cacheKey, out var verifiedAt)
            && now - verifiedAt < TimeSpan.FromMinutes(5))
            return Status(project, origin, integrationRef, true, "admitted", verifiedAt);

        var (remoteOk, remote) = await _git.RunAsync(path, ["config", "--get", "remote.origin.url"], ct);
        if (!remoteOk || !string.Equals(remote.Trim(), origin.Trim(), StringComparison.Ordinal))
            return Status(project, origin, integrationRef, false, "origin-mismatch", now);

        var transport = keyPath is null ? "origin" : "git@github.com:" + new Uri(origin).AbsolutePath.TrimStart('/');
        IReadOnlyDictionary<string, string?>? environment = keyPath is null ? null
            : new Dictionary<string, string?>
            {
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_SSH_COMMAND"] = $"ssh -i '{keyPath.Replace('\\', '/')}' -o IdentitiesOnly=yes -o IdentityAgent=none "
                    + "-o BatchMode=yes -o StrictHostKeyChecking=accept-new "
                    + $"-o UserKnownHostsFile='{Path.Combine(Path.GetDirectoryName(keyPath)!, "known_hosts").Replace('\\', '/')}'",
            };
        async Task<(bool Success, string Output)> GitAsync(IReadOnlyList<string> args)
            => environment is null ? await _git.RunAsync(path, args, ct)
                : await _git.RunAsync(path, args, environment, ct);
        var (readOk, _) = await GitAsync(["ls-remote", "--heads", transport]);
        var temporaryRef = "refs/heads/agent-studio-access-probe/" + Guid.NewGuid().ToString("N");
        var (pushOk, _) = readOk
            ? await GitAsync(["push", transport, "HEAD:" + temporaryRef])
            : (false, string.Empty);
        var (deleteOk, _) = pushOk
            ? await GitAsync(["push", transport, ":" + temporaryRef])
            : (false, string.Empty);
        var writeOk = pushOk && deleteOk;
        if (readOk && writeOk) _verified[cacheKey] = now;
        return Status(project, origin, integrationRef, readOk && writeOk,
            !readOk ? "read-denied" : !pushOk ? "write-denied"
                : !deleteOk ? "probe-cleanup-required" : "admitted", now);
    }

    private object Status(ProjectRecord project, string origin, string integrationRef, bool admitted,
        string verdict, DateTimeOffset observedAt)
        => new
        {
            registration = new
            {
                projectId = project.Id,
                repositoryUrl = origin,
                integrationRef,
                releaseRef = (string?)null,
                deliveryPolicy = "reviewed-publication",
            },
            probes = new[] { new
            {
                runnerId = "local",
                admitted,
                verdict,
                detail = (string?)null,
                observedAt,
            } },
        };

}

public sealed class LocalFirstRunGitCommand : ILocalFirstRunGitCommand
{
    public async Task<(bool Success, string Output)> RunAsync(
        string directory, IReadOnlyList<string> args, CancellationToken ct)
        => await RunAsync(directory, args, new Dictionary<string, string?>(), ct);

    public async Task<(bool Success, string Output)> RunAsync(string directory,
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> environment, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var (key, value) in environment)
            process.StartInfo.Environment[key] = value;
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            if (!process.Start()) return (false, string.Empty);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
            return (process.ExitCode == 0, await stdout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return (false, string.Empty);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, string.Empty);
        }
    }
}

public static class LocalFirstRunRepositoryEndpoints
{
    public static void MapLocalFirstRunRepositoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/first-run/projects/{projectId}/repository", async (
            string projectId, ProjectRegistry projects, AgentStudio.Projects.ProjectSettingsService settings,
            LocalFirstRunRepositoryProbe probe, CancellationToken ct) =>
        {
            var project = projects.FindById(projectId);
            return project is null ? Results.NotFound()
                : await probe.ReadAsync(project,
                    settings.Get(project.DisplayName).IntegrationBranch ?? "main", ct) is { } status
                    ? Results.Ok(status) : Results.NotFound();
        });
    }
}
