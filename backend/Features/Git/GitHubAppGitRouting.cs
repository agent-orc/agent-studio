using System.Diagnostics;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Git;

/// <summary>
/// Includes the App's generated Git config into a backend Git process. Off until
/// the config exists; then applied to every spawn by
/// <see cref="HostGitProcessHardening"/>, the one place all of them pass.
/// </summary>
internal static class GitHubAppGitRouting
{
    private static volatile string? _configPath;

    internal static void Enable(string configPath) => _configPath = configPath;

    internal static void Disable() => _configPath = null;

    internal static void Apply(ProcessStartInfo startInfo)
    {
        if (_configPath is { } path) RepositoryGitCredentialFiles.Include(startInfo.Environment, path);
    }
}
