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
    /// Inherited Git variables are removed, so the probe behaves the same on a developer machine, the gate
    /// host and a review host, whose hermetic step environment sets <c>GIT_CONFIG_NOSYSTEM=1</c>.
    /// </summary>
    private sealed class IsolatedGit(string systemConfig, string home) : IReadinessGit
    {
        private readonly ProcessReadinessGit _git = new();

        public ReadinessGitResult Run(string workingDirectory, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var isolated = new Dictionary<string, string?>(environment ?? new Dictionary<string, string?>())
            {
                ["GIT_CONFIG_SYSTEM"] = systemConfig,
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["HOME"] = home,
                ["XDG_CONFIG_HOME"] = null,
                ["GIT_CONFIG_NOSYSTEM"] = null,
                ["GIT_CONFIG"] = null,
                ["GIT_CONFIG_COUNT"] = null,
                ["GIT_CONFIG_PARAMETERS"] = null,
                ["GIT_AUTHOR_NAME"] = null,
                ["GIT_AUTHOR_EMAIL"] = null,
                ["GIT_COMMITTER_NAME"] = null,
                ["GIT_COMMITTER_EMAIL"] = null,
                ["EMAIL"] = null,
            };
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
        Assert.Contains("git config --global user.name", refused.Remedy);

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
