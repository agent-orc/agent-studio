using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerDeviceLoginAgentTests
{
    [Fact]
    public async Task Setup_launcher_runs_only_bounded_login_and_status_commands()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "provider-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "fake-provider");
            await File.WriteAllTextAsync(script,
                "#!/bin/sh\ncase \"$*\" in\n"
                + "  'login --device-auth') printf 'https://auth.openai.com/codex/device\\nABCD-EFGH\\n';;\n"
                + "  'login status') exit 0;;\n"
                + "  'setup-token') printf 'https://claude.ai/oauth/authorize\\n';;\n"
                + "  'auth status --text') exit 0;;\n"
                + "  *) exit 9;;\n"
                + "esac\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var launcher = new RunnerProviderSetupLauncher(script, script);
            var lines = new List<string>();
            Assert.True(await launcher.RunAsync("codex", false, line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            }, CancellationToken.None));
            Assert.Contains("ABCD-EFGH", lines);
            Assert.True(await launcher.RunAsync("codex", true, _ => Task.CompletedTask, CancellationToken.None));
            Assert.True(await launcher.RunAsync("claude", false, _ => Task.CompletedTask, CancellationToken.None));
            Assert.True(await launcher.RunAsync("claude", true, _ => Task.CompletedTask, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                launcher.RunAsync("unknown", false, _ => Task.CompletedTask, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Extracts_only_provider_verification_urls_and_short_codes()
    {
        Assert.Equal("https://auth.openai.com/codex/device",
            RunnerDeviceLoginAgent.SafeVerificationUrl(
                "Visit https://auth.openai.com/codex/device to continue"));
        Assert.Equal("ABCD-EFGH", RunnerDeviceLoginAgent.DeviceCode("Enter ABCD-EFGH"));
        Assert.Equal("https://auth.openai.com/codex/device",
            RunnerDeviceLoginAgent.SafeVerificationUrl("\u001b[32mhttps://auth.openai.com/codex/device\u001b[0m"));
        Assert.Null(RunnerDeviceLoginAgent.SafeVerificationUrl(
            "Visit https://evil.example/codex/device to continue"));
        Assert.Null(RunnerDeviceLoginAgent.DeviceCode("test-" + new string('x', 48)));
        var claudeToken = "sk-ant-" + new string('x', 32);
        Assert.Equal(claudeToken, RunnerDeviceLoginAgent.ClaudeToken(claudeToken));
    }
}
