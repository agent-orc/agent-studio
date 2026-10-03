using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProtectedGitCredentialHostTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Concurrent_retry_uses_one_redacted_staged_credential()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "repository-credential-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new FakeSession();
            var host = new ProtectedGitCredentialHost(root, session,
                (_, _) => Task.FromResult(true), (_, _) => Task.FromResult("old"));
            var request = new RepositoryHttpsRotationRequest("op-test", "old",
                new("fixture", "unknown", "workspace",
                    "https://github.com/example/private-workspace.git", true, true, true,
                    null, "unknown"), "old-fixture");

            var stages = await Task.WhenAll(
                host.StageAsync(request, CancellationToken.None),
                host.StageAsync(request, CancellationToken.None));

            Assert.Equal(stages[0], stages[1]);
            Assert.Equal(1, session.Calls);
            Assert.NotNull(host.StoreForGeneration(stages[0]!));
            Assert.DoesNotContain("redacted-fixture-bearer", System.Text.Json.JsonSerializer.Serialize(request));
            var staged = await ProcessRunner.RunAsync("git",
                ["credential-store", "--file", host.StoreForGeneration(stages[0]!)!, "get"],
                stdin: "protocol=https\nhost=github.com\npath=example/private-workspace.git\n\n",
                ct: CancellationToken.None);
            Assert.True(staged.Success);
            Assert.Contains("password=redacted-fixture-bearer", staged.StdOut);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(host.StoreForGeneration(stages[0]!)!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeSession : IProtectedGitHubTokenSession
    {
        public int Calls;
        public Task<(string Username, string Bearer)?> ObtainAsync(
            RepositoryHttpsRotationRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<(string, string)?>(new("fixture", "redacted-fixture-bearer"));
        }
    }
}
