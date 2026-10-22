using System.Formats.Tar;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class SshDeployExecutorTests
{
    [Fact]
    public void Local_step_cap_exceeds_two_remote_compose_timeouts()
    {
        var script = (string)typeof(SshDeployExecutor).GetField("RemoteScript", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Equal(2, Regex.Matches(script, "timeout=120").Count);
        Assert.True(SshDeployExecutor.StepCap > SshDeployExecutor.RemoteCommandBudget + TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("DOCKER_HOST")]
    [InlineData("DOCKER_CONFIG")]
    [InlineData("COMPOSE_PROJECT_NAME")]
    [InlineData("LD_PRELOAD")]
    public async Task Reserved_remote_environment_names_are_refused(string name)
    {
        var calls = 0;
        var executor = new SshDeployExecutor((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        var step = Step("container") with { ComposeFile = "/srv/apps/web/compose.yml", Service = "web" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("web-vm", step,
            "image@sha256:" + new string('a', 64), "/unused", new Dictionary<string, string> { [name] = "bad" },
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Failed_ssh_stderr_tail_is_bounded_and_masks_variables()
    {
        var secret = "secret-" + Guid.NewGuid().ToString("N");
        var executor = new SshDeployExecutor((_, _, _, _) => Task.FromResult(
            new ProcessResult(1, "", new string('x', 500) + secret)));
        var step = Step("container") with { ComposeFile = "/srv/apps/web/compose.yml", Service = "web" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("web-vm", step,
            "image@sha256:" + new string('a', 64), "/unused", new Dictionary<string, string> { ["TOKEN"] = secret },
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("[masked]", error.Message);
        Assert.DoesNotContain(secret, error.Message);
        Assert.True(error.Message.Length < 460);
    }

    [Fact]
    public async Task Remote_switch_replaces_real_current_directory_and_prunes_old_entries()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "remote-switch-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "site");
        var releases = Path.Combine(target, "releases");
        Directory.CreateDirectory(releases);
        Directory.CreateDirectory(Path.Combine(target, "current"));
        var version = Path.Combine(releases, "new");
        var staging = version + ".tmp-test";
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "index.html"), "new");
        for (var i = 0; i < 5; i++)
        {
            var old = Path.Combine(releases, "old" + i);
            Directory.CreateDirectory(old);
            Directory.SetLastWriteTimeUtc(old, DateTime.UnixEpoch.AddDays(i + 1));
        }
        Directory.CreateDirectory(Path.Combine(releases, "abandoned.tmp-old"));
        Directory.SetLastWriteTimeUtc(Path.Combine(releases, "abandoned.tmp-old"), DateTime.UnixEpoch);
        try
        {
            var script = (string)typeof(SshDeployExecutor).GetField("RemoteScript", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;
            var start = new ProcessStartInfo("python3")
            { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                { action = "switch", path = target, staging, version }));
            process.StandardInput.Close();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "current", "index.html")));
            Assert.False(Directory.Exists(Path.Combine(releases, "abandoned.tmp-old")));
            Assert.True(Directory.GetDirectories(releases).Length <= 3);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Remote_compose_uses_minimal_environment_and_removes_override_file()
    {
        if (!OperatingSystem.IsLinux()) return;
        var capture = Path.Combine(Path.GetTempPath(), "remote-compose-" + Guid.NewGuid().ToString("N"));
        try
        {
            var script = (string)typeof(SshDeployExecutor).GetField("RemoteScript", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;
            var fake = "import subprocess, json\n" +
                "capture = " + JsonSerializer.Serialize(capture) + "\n" +
                "def fake_run(args, **kwargs):\n" +
                "    with open(capture, 'a') as out:\n" +
                "        out.write(json.dumps({'args': args, 'env': kwargs['env'], 'timeout': kwargs['timeout']}) + '\\n')\n" +
                "    return subprocess.CompletedProcess(args, 0)\n" +
                "subprocess.run = fake_run\n";
            var start = new ProcessStartInfo("python3")
            { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(fake + script);
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                action = "compose", file = "/srv/apps/demo/compose.yml", service = "demo",
                image = "image@sha256:" + new string('a', 64), variables = new { MESSAGE = "hello" },
            }));
            process.StandardInput.Close();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            var calls = File.ReadAllLines(capture).Select(line => JsonDocument.Parse(line)).ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal("pull", calls[0].RootElement.GetProperty("args")[6].GetString());
            Assert.Equal("up", calls[1].RootElement.GetProperty("args")[6].GetString());
            Assert.All(calls, call =>
            {
                Assert.Equal(120, call.RootElement.GetProperty("timeout").GetInt32());
                var env = call.RootElement.GetProperty("env");
                Assert.Equal("/usr/bin:/bin", env.GetProperty("PATH").GetString());
                Assert.Equal("hello", env.GetProperty("MESSAGE").GetString());
                Assert.False(env.TryGetProperty("DOCKER_HOST", out _));
            });
            var overrideFile = calls[0].RootElement.GetProperty("args")[5].GetString()!;
            Assert.False(File.Exists(overrideFile));
            foreach (var call in calls) call.Dispose();
        }
        finally { File.Delete(capture); }
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
