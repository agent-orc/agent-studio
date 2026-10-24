using AgentStudio.CliHosting;
using Xunit;

namespace AgentRunner.Tests;

public sealed class WindowsProviderBridgeTests
{
    [Theory]
    [InlineData("C:\\Studio\\provider-codex.exe", "codex")]
    [InlineData("C:\\Studio\\provider-claude.exe", "claude")]
    public void Packaged_provider_executable_selects_the_expected_cli(string path, string expected)
    {
        Assert.True(WindowsProviderBridge.IsBridgeProcess(path, out var provider, windows: true));
        Assert.Equal(expected, provider);
    }

    [Fact]
    public void Agent_host_executable_does_not_enter_the_bridge()
        => Assert.False(WindowsProviderBridge.IsBridgeProcess("agent-host.exe", out _, windows: true));
}
