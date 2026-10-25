using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class SshDeployExecutorTests
{
    [Fact]
    public async Task Tenant_compose_jobs_use_distinct_files_and_project_names()
    {
        var payloads = new List<JsonDocument>();
        var executor = new SshDeployExecutor((_, _, input, _) =>
        {
            payloads.Add(JsonDocument.Parse(input!));
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        try
        {
            foreach (var tenant in new[] { "acme", "beta" })
            {
                var step = Step("container") with
                {
                    ComposeFile = $"/srv/apps/web/tenants/{tenant}/compose.yml", Service = "web",
                };
                await executor.ExecuteAsync("web-vm", step, "image@sha256:" + new string('a', 64), "/unused",
                    new Dictionary<string, string>(), _ => Task.CompletedTask, CancellationToken.None,
                    composeProject: "tenant-" + tenant);
            }
            Assert.Equal(2, payloads.Count);
            Assert.Equal("/srv/apps/web/tenants/acme/compose.yml", payloads[0].RootElement.GetProperty("file").GetString());
            Assert.Equal("/srv/apps/web/tenants/beta/compose.yml", payloads[1].RootElement.GetProperty("file").GetString());
            Assert.Equal("tenant-acme", payloads[0].RootElement.GetProperty("project").GetString());
            Assert.Equal("tenant-beta", payloads[1].RootElement.GetProperty("project").GetString());
        }
        finally
        {
            foreach (var payload in payloads) payload.Dispose();
        }
    }

    [Fact]
    public async Task Container_values_travel_on_stdin_and_commands_require_a_known_host()
    {
        var calls = new List<(string File, IReadOnlyList<string> Args, string? Input)>();
        var executor = new SshDeployExecutor((file, args, input, _) =>
        {
            calls.Add((file, args, input));
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        var step = Step("container") with { ComposeFile = "/srv/apps/web/compose.yml", Service = "web" };
        var value = "$(touch /tmp/should-not-run)";

        await executor.ExecuteAsync("web-vm", step, "image@sha256:" + new string('a', 64), "/unused",
            new Dictionary<string, string> { ["MESSAGE"] = value }, _ => Task.CompletedTask, CancellationToken.None);

        var call = Assert.Single(calls);
        Assert.Equal("ssh", call.File);
        Assert.Contains("StrictHostKeyChecking=yes", call.Args);
        Assert.DoesNotContain(value, string.Join(' ', call.Args));
        Assert.Contains(value, call.Input);
        Assert.Contains("image@sha256:" + new string('a', 64), call.Input);
        Assert.DoesNotContain("/srv/apps/web/compose.yml", string.Join(' ', call.Args));
    }

    [Fact]
    public async Task Unknown_host_key_refuses_deployment_before_any_later_step()
    {
        var calls = 0;
        var executor = new SshDeployExecutor((_, args, _, _) =>
        {
            calls++;
            Assert.Contains("StrictHostKeyChecking=yes", args);
            return Task.FromResult(new ProcessResult(255, "", "Host key verification failed."));
        });
        var step = Step("container") with { ComposeFile = "/srv/apps/web/compose.yml", Service = "web" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("web-vm", step,
            "image@sha256:" + new string('a', 64), "/unused", new Dictionary<string, string>(),
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Changed_host_registration_cannot_execute_a_snapshotted_target()
    {
        var calls = 0;
        var executor = new SshDeployExecutor((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        var step = Step("container") with { ComposeFile = "/srv/apps/web/compose.yml", Service = "web" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("new-host", step,
            "image@sha256:" + new string('a', 64), "/unused", new Dictionary<string, string>(),
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Static_bundle_uses_scp_then_switches_only_after_copy_succeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), "ssh-deploy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "bundle.tar");
            await using (var output = File.Create(archive))
            using (var writer = new TarWriter(output))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "index.html")
                { DataStream = new MemoryStream("hello"u8.ToArray()) });
            }
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive))).ToLowerInvariant();
            File.Move(archive, Path.Combine(root, digest));
            var calls = new List<string>();
            var copies = 0;
            var executor = new SshDeployExecutor((file, _, input, _) =>
            {
                calls.Add(file + ":" + input);
                return Task.FromResult(new ProcessResult(file == "scp" && ++copies == 1 ? 1 : 0, "", ""));
            });
            var step = Step("static-site") with { Directory = "/srv/www/site" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("web-vm", step,
                "bundle@sha256:" + digest, root, new Dictionary<string, string>(),
                _ => Task.CompletedTask, CancellationToken.None));
            Assert.Equal(3, calls.Count);
            Assert.StartsWith("ssh:", calls[0]);
            Assert.StartsWith("scp:", calls[1]);
            Assert.StartsWith("ssh:", calls[2]);
            Assert.Contains("cleanup", calls[2]);

            await executor.ExecuteAsync("web-vm", step, "bundle@sha256:" + digest, root,
                new Dictionary<string, string>(), _ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(2, copies);
            Assert.Equal(6, calls.Count);
            Assert.Contains("switch", calls[^1]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ProjectDeployStep Step(string kind)
        => new(kind, "site", "web-vm", null, null, null, [], ["staging"]);
}
