using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class LocalRepositoryKeyHostTests
{
    [Fact]
    public async Task Refuses_private_key_custody_inside_a_repository_checkout()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "repository-key-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var host = new LocalRepositoryKeyHost(Path.Combine(root, "secrets"),
                (_, _) => Task.FromResult(false), (_, _) => Task.FromResult("old"));
            var request = new RepositoryRotationRequest("op", "host", "old", "example", "repo",
                "product", "git@github.com:example/repo.git", true, null, "provisioning-record");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.GenerateOrLoadAsync(request, CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(root, "secrets")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Target_host_generation_is_private_and_idempotent()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "repository-key-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new LocalRepositoryKeyHost(root,
                (_, _) => Task.FromResult(false), (_, _) => Task.FromResult("old"));
            var request = new RepositoryRotationRequest("op-redacted", "host-fixture", "old",
                "example", "private-workspace", "workspace",
                "git@github.com:example/private-workspace.git", true, null, "provisioning-fixture");

            var first = await host.GenerateOrLoadAsync(request, CancellationToken.None);
            var retry = await host.GenerateOrLoadAsync(request, CancellationToken.None);

            Assert.Equal(first, retry);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(first.HostLocalRef));
            Assert.StartsWith("SHA256:", first.Fingerprint);
            Assert.DoesNotContain("PRIVATE KEY", System.Text.Json.JsonSerializer.Serialize(first));
            Assert.False(await host.DrainTransportsAsync(request, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
