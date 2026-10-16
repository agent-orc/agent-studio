using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerDeviceLoginAgentTests
{
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
