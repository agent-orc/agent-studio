using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class LocalRepositoryKeyHostTests
{
    [Theory]
    [InlineData("https://github.com/example/repo.git")]
    [InlineData("git@github.com:example/repo.git")]
    [Trait("Category", "MachineBound")]
    public async Task Switch_replaces_old_exact_rewrite_for_registered_origin(string origin)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "repository-switch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var config = Path.Combine(root, "gitconfig");
            var oldUrl = "git@old-key:example/repo.git";
            var old = await ProcessRunner.RunAsync("git",
                ["config", "--file", config, "--add", $"url.{oldUrl}.insteadOf", origin]);
            Assert.True(old.Success);
            var request = new RepositoryRotationRequest("op", "host", "old", "example", "repo",
                "product", origin, true, 1, "provisioning-record");
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("host/example/repo/op"))).ToLowerInvariant()[..32];
            var keyPath = Path.Combine(root, "deploy-" + digest);
            await File.WriteAllTextAsync(keyPath, "redacted fixture");
            var host = new LocalRepositoryKeyHost(root,
                (_, _) => Task.FromResult(true), (_, _) => Task.FromResult("old"),
                config, Path.Combine(root, "ssh-config"));

            await host.SwitchAsync(request,
                new RepositoryKeyCandidate("key-" + digest, "ssh-ed25519 fixture", "SHA256:fixture", keyPath),
                CancellationToken.None);
            await host.SwitchAsync(request,
                new RepositoryKeyCandidate("key-" + digest, "ssh-ed25519 fixture", "SHA256:fixture", keyPath),
                CancellationToken.None);

            var mappings = await ProcessRunner.RunAsync("git",
                ["config", "--file", config, "--get-regexp", "^url\\..*\\.insteadof$"]);
            Assert.True(mappings.Success);
            Assert.DoesNotContain(oldUrl, mappings.StdOut);
            Assert.Contains("git@agent-studio-key-" + digest + ":example/repo.git", mappings.StdOut);
            Assert.Contains(origin, mappings.StdOut);
            Assert.Equal(4, mappings.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Stale_expected_generation_cannot_replace_another_managed_key()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "repository-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var config = Path.Combine(root, "gitconfig");
            const string origin = "git@github.com:example/repo.git";
            var priorGeneration = "key-" + new string('a', 32);
            var prior = $"git@agent-studio-{priorGeneration}:example/repo.git";
            Assert.True((await ProcessRunner.RunAsync("git",
                ["config", "--file", config, "--add", $"url.{prior}.insteadOf", origin])).Success);
            var request = new RepositoryRotationRequest("op", "host", "old", "example", "repo",
                "product", origin, true, 1, "provisioning-record");
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("host/example/repo/op"))).ToLowerInvariant()[..32];
            var path = Path.Combine(root, "deploy-" + digest);
            await File.WriteAllTextAsync(path, "redacted fixture");
            var host = new LocalRepositoryKeyHost(root,
                (_, _) => Task.FromResult(true), (_, _) => Task.FromResult("old"),
                config, Path.Combine(root, "ssh-config"));

            Assert.Equal(priorGeneration,
                await host.ActiveGenerationAsync(request, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SwitchAsync(request,
                new RepositoryKeyCandidate("key-" + digest, "ssh-ed25519 fixture",
                    "SHA256:fixture", path), CancellationToken.None));
            var resolved = await ProcessRunner.RunAsync("git", ["ls-remote", "--get-url", origin],
                environment: new Dictionary<string, string?> {
                    ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = config
                });
            Assert.Equal(prior, resolved.StdOut.Trim());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }


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
