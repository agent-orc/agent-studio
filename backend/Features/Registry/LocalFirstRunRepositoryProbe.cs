using System.Diagnostics;
using System.Collections.Concurrent;
using AgentStudio.Security;

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
        CancellationToken ct, bool force = false, string? keyPath = null, string? credentialConfig = null)
    {
        var origin = project.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase))?.Url;
        var path = project.RepositoryPath;
        if (string.IsNullOrWhiteSpace(origin)) return null;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Status(project, origin, integrationRef, false, "checkout-missing", clock.GetUtcNow(), false);

        var cacheKey = string.Join('\n', project.Id, origin, path, integrationRef, keyPath, credentialConfig);
        var now = clock.GetUtcNow();
        if (!force && _verified.TryGetValue(cacheKey, out var verifiedAt)
            && now - verifiedAt < TimeSpan.FromMinutes(5))
            return Status(project, origin, integrationRef, true, "admitted", verifiedAt);

        var (remoteOk, remote) = await _git.RunAsync(path, ["config", "--get", "remote.origin.url"], ct);
        var transport = keyPath is null ? origin : "git@github.com:" + new Uri(origin).AbsolutePath.TrimStart('/');
        if (!remoteOk || !string.Equals(remote.Trim(), transport, StringComparison.Ordinal))
            return Status(project, origin, integrationRef, false, "origin-mismatch", now);

        var environment = TransportEnvironment(keyPath, credentialConfig);
        var temporary = Path.Combine(Path.GetTempPath(), "agent-studio-git-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        bool readOk, pushOk, deleteOk;
        try
        {
            // A fresh clone has no checkout-local URL rewrites. Git must use
            // the registered URL with only the credential selected here.
            var (cloneOk, _) = await _git.RunAsync(temporary,
                ["clone", "--shared", "--no-checkout", "--", path, "source"], environment, ct);
            if (!cloneOk)
                return Status(project, origin, integrationRef, false, "checkout-unavailable", now);
            var proofPath = Path.Combine(temporary, "source");
            var (fetched, _) = await _git.RunAsync(proofPath, ["ls-remote", "--heads", transport], environment, ct);
            readOk = fetched;
            var temporaryRef = "refs/heads/agent-studio-access-probe/" + Guid.NewGuid().ToString("N");
            var (pushed, _) = readOk
                ? await _git.RunAsync(proofPath, ["push", transport, "HEAD:" + temporaryRef], environment, ct)
                : (false, string.Empty);
            pushOk = pushed;
            var (deleted, _) = pushOk
                ? await _git.RunAsync(proofPath, ["push", transport, ":" + temporaryRef], environment, ct)
                : (false, string.Empty);
            deleteOk = deleted;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
        var writeOk = pushOk && deleteOk;
        if (readOk && writeOk) _verified[cacheKey] = now;
        return Status(project, origin, integrationRef, readOk && writeOk,
            !readOk ? "read-denied" : !pushOk ? "write-denied"
                : !deleteOk ? "probe-cleanup-required" : "admitted", now);
    }

    internal static IReadOnlyDictionary<string, string?> TransportEnvironment(string? keyPath,
        string? credentialConfig = null)
    {
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = credentialConfig ?? (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            ["GIT_CONFIG_COUNT"] = "0",
            ["GIT_CONFIG_PARAMETERS"] = null,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never",
            ["GIT_ASKPASS"] = null,
            ["SSH_ASKPASS"] = null,
        };
        if (keyPath is not null) environment["GIT_SSH_COMMAND"] = SshCommand(keyPath);
        return environment;
    }

    internal static string SshCommand(string keyPath)
        => $"ssh -i '{keyPath.Replace('\\', '/')}' -o IdentitiesOnly=yes -o IdentityAgent=none "
            + "-o BatchMode=yes -o StrictHostKeyChecking=accept-new "
            + $"-o UserKnownHostsFile='{Path.Combine(Path.GetDirectoryName(keyPath)!, "known_hosts").Replace('\\', '/')}'";

    private object Status(ProjectRecord project, string origin, string integrationRef, bool admitted,
        string verdict, DateTimeOffset observedAt, bool hasProbe = true)
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
            probes = hasProbe ? new[] { new
            {
                runnerId = "local",
                admitted,
                verdict,
                detail = (string?)null,
                observedAt,
            } } : [],
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
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var (key, value) in environment)
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = await AgentStudio.Git.GitProcessBoundary.RunAsync(
            start, stdin: null, TimeSpan.FromSeconds(15), ct);
        ct.ThrowIfCancellationRequested();
        return result.FailureKind == AgentStudio.Git.GitProcessFailureKind.None
            ? (result.ExitCode == 0, result.StandardOutput)
            : (false, string.Empty);
    }
}

public static class LocalFirstRunRepositoryEndpoints
{
    public static void MapLocalFirstRunRepositoryEndpoints(this WebApplication app)
    {
        // Reads the installation's Git credential state: Owner only when networked.
        app.MapGet("/api/first-run/projects/{projectId}/repository", async (
            string projectId, ProjectRegistry projects, AgentStudio.Projects.ProjectSettingsService settings,
            LocalFirstRunRepositoryProbe probe, LocalFirstRunGitSetup setup, CancellationToken ct) =>
        {
            var project = projects.FindById(projectId);
            if (project is null) return Results.NotFound();
            var origin = project.Urls.FirstOrDefault(url => url.Id == "repo")?.Url;
            var (keyPath, credentialConfig) = origin is null ? (null, null)
                : setup.ResolveCredential(project.Id, origin);
            return await probe.ReadAsync(project,
                settings.Get(project.DisplayName).IntegrationBranch ?? "main", ct,
                keyPath: keyPath, credentialConfig: credentialConfig) is { } status
                ? Results.Ok(status) : Results.NotFound();
        }).RequireStudioRole(StudioRoles.Owner);
    }
}
