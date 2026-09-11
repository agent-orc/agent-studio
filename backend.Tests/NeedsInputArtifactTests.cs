using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class NeedsInputArtifactTests
{
    [Fact]
    public void Persists_message_attempt_and_salvage_branch_and_references_file_from_park_marker()
    {
        var folder = Path.Combine(Path.GetTempPath(), "needs-input-artifact-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            const string message = "Which deployment strategy?\n\n- A: connector\n- B: LAN";
            var status = NeedsInputArtifact.Write(
                folder, message, "run_4e53d6d6", "runner/agent-runner-01/AGT-2736", NullLogger.Instance);
            ParkedBlockerMarker.Write(folder, new ParkedBlockerRecord
            {
                BlockerType = HumanReviewEscalationCategories.NeedsHumanInput,
                Lane = TaskStates.Escalated,
                ParkedAt = DateTime.UtcNow,
                Reason = "Which deployment strategy?",
            });
            NeedsInputArtifact.ReferenceFromParkedBlocker(folder, NullLogger.Instance);

            Assert.NotNull(status);
            Assert.Equal(message, NeedsInputArtifact.TryRead(folder)!.Message);
            var markdown = File.ReadAllText(Path.Combine(folder, "results", "needs-input.md"));
            Assert.Contains("run_4e53d6d6", markdown);
            Assert.Contains("runner/agent-runner-01/AGT-2736", markdown);
            Assert.Contains(message, markdown);
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "parked-blocker.json")));
            Assert.Equal("results/needs-input.md", marker.RootElement.GetProperty("needsInputFile").GetString());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
