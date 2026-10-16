using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Shared;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalFirstRunRepositoryProbeTests : IDisposable
{
    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "first-run-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();
    private const string Origin = "https://github.com/example/demo.git";

    public LocalFirstRunRepositoryProbeTests() => Directory.CreateDirectory(_checkout);

    [Fact]
    public async Task Missing_checkout_or_registration_stays_pending()
    {
        var git = new FakeGit();
        var probe = new LocalFirstRunRepositoryProbe(_clock, git);
        Assert.Null(await probe.ReadAsync(Project(null, _checkout), "main", default));
        Assert.Null(await probe.ReadAsync(Project(Origin, Path.Combine(_checkout, "missing")), "main", default));
        Assert.Empty(git.Calls);
    }

    [Theory]
    [InlineData("remote", "origin-mismatch")]
    [InlineData("read", "read-denied")]
    [InlineData("write", "write-denied")]
    [InlineData("cleanup", "probe-cleanup-required")]
    [InlineData("none", "admitted")]
    public async Task Requires_exact_origin_then_read_push_and_cleanup(string failingStep, string verdict)
    {
        var git = new FakeGit(failingStep);
        var probe = new LocalFirstRunRepositoryProbe(_clock, git);
        var result = await probe.ReadAsync(Project(Origin, _checkout), "develop", default);
        var json = JsonSerializer.SerializeToElement(result);
        var proof = json.GetProperty("probes")[0];
        Assert.Equal(verdict, proof.GetProperty("verdict").GetString());
        Assert.Equal(failingStep == "none", proof.GetProperty("admitted").GetBoolean());
        Assert.Equal(Origin, json.GetProperty("registration").GetProperty("repositoryUrl").GetString());
        if (failingStep == "none")
        {
            Assert.Contains(git.Calls, args => args[0] == "push" && args[2].StartsWith("HEAD:refs/heads/agent-studio-access-probe/"));
            Assert.Contains(git.Calls, args => args[0] == "push" && args[2].StartsWith(":refs/heads/agent-studio-access-probe/"));
        }
    }

    public void Dispose() => Directory.Delete(_checkout, recursive: true);

    private static ProjectRecord Project(string? origin, string checkout) => new()
    {
        Id = "PROJ-001",
        RepositoryPath = checkout,
        Urls = origin is null ? [] : [new ProjectUrlRecord { Id = "repo", Url = origin }],
    };

    private sealed class FakeGit(string failingStep = "none") : ILocalFirstRunGitCommand
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<(bool Success, string Output)> RunAsync(
            string directory, IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add(args);
            var step = args[0] switch
            {
                "config" => "remote",
                "ls-remote" => "read",
                "push" when args[2].StartsWith(':') => "cleanup",
                "push" => "write",
                _ => "unknown",
            };
            return Task.FromResult((step != failingStep,
                step == "remote" && failingStep != "remote" ? Origin : ""));
        }
    }
}
