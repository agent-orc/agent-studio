using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProtectedGitCredentialHostTests
{
    [Fact]
    public async Task Stale_expected_generation_cannot_switch_over_another_https_rotation()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "repository-https-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            const string origin = "https://github.com/example/private-workspace.git";
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(origin))).ToLowerInvariant()[..32];
            await File.WriteAllTextAsync(Path.Combine(root, "active-https-" + digest), "https-other-op");
            var host = new ProtectedGitCredentialHost(root, new FakeSession(),
                (_, _) => Task.FromResult(true), (_, _) => Task.FromResult("old"));
            var request = new RepositoryHttpsRotationRequest("op", "old",
                new("fixture", "unknown", "workspace", origin, false,
                    true, null, null, "unknown"), "old-fixture");

            Assert.Equal("https-other-op", await host.ActiveGenerationAsync(request, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.SwitchAsync(request, "https-op", CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
