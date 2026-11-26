using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: journal dates are inert fixture provenance.
public sealed class CodingSelectionContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Follow_up_prefers_the_captured_session_route_instead_of_the_cards_original_provider(bool hardPin)
    {
        var folder = Path.Combine(Path.GetTempPath(), "session-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(TaskPaths.SessionEventsLog(folder))!);
        try
        {
            File.WriteAllText(TaskPaths.SessionEventsLog(folder), JsonSerializer.Serialize(new SessionEvent
            {
                Ts = DateTime.UnixEpoch, Cli = "codex", Model = "gpt-6-sol", ThinkingLevel = "medium",
                CapturedSessionId = "fixture-session",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
            var info = new TaskInfo { FolderPath = folder, SessionName = "fixture-session",
                CliType = "claude", Model = "claude-sonnet-5", ThinkingLevel = "high",
                PinMode = hardPin ? "hard" : "preference" };
            var continuation = CodingSelectionCalls.ContinuationFor(info);
            if (hardPin) Assert.Null(continuation);
            else
            {
                Assert.Equal("codex", continuation?.SessionCli);
                Assert.Equal("gpt-6-sol", continuation?.SessionModel);
                Assert.Equal("medium", continuation?.SessionThinkingLevel);
            }
        }
        finally { Directory.Delete(folder, true); }
    }
}
