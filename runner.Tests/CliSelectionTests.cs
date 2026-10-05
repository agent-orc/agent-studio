using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class CliSelectionTests
{
    [Fact]
    public void CardSelectionUsesProviderSpecificBinaryAndTypedPins()
    {
        var options = Options(codexPath: "/tools/codex");

        var selected = CliSelection.Resolve(
            options,
            new RunSpecDto(CliSelection.CodexCli, "gpt-test", "high", null, null));

        Assert.Equal("/tools/codex", selected.FileName);
        Assert.Equal(CliSelection.CodexCli, selected.CliType);
        Assert.Equal("gpt-test", selected.Model);
        Assert.Equal("high", selected.ThinkingLevel);
        Assert.Equal("card", selected.Source);
    }

    [Fact]
    public void HostDefaultIsUsedWithoutRunSpec()
    {
        var selected = CliSelection.Resolve(Options(), null);

        Assert.Equal(CliSelection.ClaudeCli, selected.CliType);
        Assert.Equal("claude", selected.FileName);
        Assert.Equal("runner-options", selected.Source);
    }

    private static RunnerOptions Options(string codexPath = "codex") => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner",
        RunnerName = "runner",
        Hostname = "host",
        BackendName = "test",
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        CodexCliBin = codexPath,
    };
}
