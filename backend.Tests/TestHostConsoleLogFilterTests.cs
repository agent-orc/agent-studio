using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestHostConsoleLogFilterTests
{
    [Fact]
    public void Suppresses_expected_fixture_noise_but_keeps_actionable_failures()
    {
        Assert.False(Visible(LogEventLevel.Information, "git-index-run repository={Repository}"));
        Assert.False(Visible(LogEventLevel.Warning, "result-scaffold-created project={Project}"));
        Assert.False(Visible(LogEventLevel.Warning, "git-index-run-failed repository={Repository}",
            new DirectoryNotFoundException("repository-unavailable")));
        Assert.False(Visible(LogEventLevel.Warning,
            "Claude one-shot non-zero exit: agent-studio test guard: the real claude CLI is not available in tests"));

        Assert.True(Visible(LogEventLevel.Warning, "git-index-run-failed repository={Repository}",
            new IOException("unexpected git failure")));
        Assert.True(Visible(LogEventLevel.Warning, "unexpected test-host warning"));
        Assert.True(Visible(LogEventLevel.Error,
            "agent-studio test guard: the real claude CLI is not available in tests"));
    }

    private static bool Visible(LogEventLevel level, string template, Exception? exception = null)
        => TestHostConsoleLogFilter.ShouldWrite(new LogEvent(
            DateTimeOffset.UnixEpoch,
            level,
            exception,
            new MessageTemplateParser().Parse(template),
            []));
}
