using System.Text.RegularExpressions;
using AgentStudio.Readiness;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The API image sets a default Git identity for its service user (AGT-3412),
/// so a new server needs no manual step before its first direct merge. Before,
/// the image set only <c>safe.directory</c>, and on 1 November 2026 every
/// direct merge failed with "Committer identity unknown".
/// </summary>
public sealed partial class ServerImageGitIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "image-git-identity-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public ServerImageGitIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [GeneratedRegex(@"git config --system (?<key>[\w.]+) '(?<value>[^']*)'")]
    private static partial Regex SystemConfig();

    private static IReadOnlyDictionary<string, string> ImageSystemConfig()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "backend", "Dockerfile"));
        // Only the runtime stage: the build stage never commits.
        var runtime = dockerfile[dockerfile.IndexOf("FROM mcr.microsoft.com/dotnet/aspnet", StringComparison.Ordinal)..];
        Assert.True(runtime.IndexOf("git config --system user.name", StringComparison.Ordinal)
            < runtime.IndexOf("USER agentstudio", StringComparison.Ordinal), "the identity is set before the image drops root");
        return SystemConfig().Matches(runtime).ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value);
    }

    [Fact]
    public void The_api_image_sets_the_identity_the_readiness_report_calls_the_image_default()
    {
        var config = ImageSystemConfig();

        Assert.Equal(ServerReadinessPolicy.ImageGitUserName, config["user.name"]);
        Assert.Equal(ServerReadinessPolicy.ImageGitUserEmail, config["user.email"]);
        Assert.Equal("*", config["safe.directory"]);
    }

    /// <summary>
    /// Git as the service user sees it: only the given system file, no global file, no identity variables.
    /// Every inherited <c>GIT_*</c> variable is removed first, so a host that sets
    /// <c>GIT_CONFIG_NOSYSTEM</c> (the hermetic review step environment), <c>GIT_CONFIG_COUNT</c>
    /// or an author identity cannot hide or replace the system file; the probe's own variables stay.
    /// </summary>
    private sealed class IsolatedGit(string systemConfig, string home) : IReadinessGit
    {
        private readonly ProcessReadinessGit _git = new();

        public ReadinessGitResult Run(string workingDirectory, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var isolated = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>())
                if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) isolated[key] = null;
            foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
                isolated[key] = value;
            isolated["GIT_CONFIG_SYSTEM"] = systemConfig;
            isolated["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            isolated["HOME"] = home;
            isolated["GIT_COMMITTER_NAME"] = null;
            isolated["GIT_COMMITTER_EMAIL"] = null;
            isolated["EMAIL"] = null;
            return _git.Run(workingDirectory, arguments, isolated);
        }
    }

    [Fact]
    public void A_commit_tree_probe_fails_without_the_image_identity_and_passes_with_it()
    {
        var system = Path.Combine(_root, "gitconfig");
        // useConfigOnly stands in for a container whose user has no mail domain to guess from.
        File.WriteAllText(system, "[user]\n\tuseConfigOnly = true\n");
        var bare = new ReadinessGitFacts(new IsolatedGit(system, _root), _clock).ServiceUser();

        Assert.Equal(ReadinessStates.Fail, ServerReadinessPolicy.GitIdentity(bare.Identity).State);
        var refused = ServerReadinessPolicy.CommitterIdentity(bare.Commit);
        Assert.Equal(ReadinessStates.Fail, refused.State);
        Assert.Contains("git config --global user.name", refused.Command);

        foreach (var (key, value) in ImageSystemConfig())
            ReadinessGitFactsTests.Git(_root, "config", "--file", system, key, value);
        _clock.Advance(ReadinessGitFacts.CacheLifetime);
        var image = new ReadinessGitFacts(new IsolatedGit(system, _root), _clock).ServiceUser();

        var identity = ServerReadinessPolicy.GitIdentity(image.Identity);
        Assert.Equal(ReadinessStates.Pass, identity.State);
        Assert.StartsWith("Commits are made as Agent Studio <studio@agent-studio.invalid>", identity.Meaning);
        Assert.Equal(ReadinessStates.Pass, ServerReadinessPolicy.CommitterIdentity(image.Commit).State);
        Assert.Matches("^[0-9a-f]{40}$", image.Commit.Output);
    }
}
