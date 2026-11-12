using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3420: an agent aspect whose CLI refuses its model is a configuration
/// fault of the project, so it never drains the host's semantic-review
/// capability the way a genuine <c>ToolUnavailable</c> does.
/// </summary>
public sealed class ReviewModelConfigurationFaultTests
{
    private static ReviewCommandDto Aspect(string cli, string model)
        => new("aspect-code-quality", "code-quality", cli, [],
            ExecutionKind: ReviewCommandKinds.AgentAspect, Prompt: "review", CliType: cli, Model: model);

    [Fact]
    public void Claude_cli_with_a_gpt_model_is_a_configuration_fault()
    {
        var failed = new ProcessResult(1, string.Empty, "error: something went wrong");

        Assert.True(RemoteReviewWorkspace.AgentModelUnknownToCli(Aspect("claude", "gpt-5.5"), failed));
    }

    [Fact]
    public void Cli_output_naming_an_unknown_model_is_a_configuration_fault()
    {
        var failed = new ProcessResult(1, string.Empty,
            "There's an issue with the selected model (claude-typo). It may not exist.");

        Assert.True(RemoteReviewWorkspace.AgentModelUnknownToCli(Aspect("claude", "claude-typo"), failed));
    }

    [Fact]
    public void Unknown_model_remains_a_configuration_fault_when_cli_also_reports_a_refused_turn()
    {
        var failed = new ProcessResult(1, string.Empty,
            "turn/start failed: unknown model claude-typo");
        var command = Aspect("claude", "claude-typo");

        Assert.True(RemoteReviewWorkspace.AgentModelUnknownToCli(command, failed));
        Assert.Equal(ReviewCliRefusalPolicy.TurnRefused, ReviewCliRefusalPolicy.Classify(command, failed));
    }

    [Fact]
    public void Missing_cli_binary_and_unrelated_failures_stay_tool_unavailable()
    {
        Assert.False(RemoteReviewWorkspace.AgentModelUnknownToCli(
            Aspect("claude", "gpt-5.5"), new ProcessResult(127, string.Empty, "claude: not found")));
        Assert.False(RemoteReviewWorkspace.AgentModelUnknownToCli(
            Aspect("claude", "claude-opus-5"), new ProcessResult(1, string.Empty, "connection reset")));
        Assert.False(RemoteReviewWorkspace.AgentModelUnknownToCli(
            Aspect("codex", "gpt-5.5"), new ProcessResult(0, "[[ASPECT_VERDICT:pass]]", string.Empty)));
    }

    [Fact]
    public void Configuration_fault_drains_no_host_capability()
    {
        Assert.Null(RemoteReviewExecutor.FailedCapability(
            ReviewModelConfigurationPolicy.Classification, null, out var readKey));
        Assert.Null(readKey);
        Assert.Equal(ReviewCapabilities.SemanticReview,
            RemoteReviewExecutor.FailedCapability("ToolUnavailable", null, out _));
    }
}
