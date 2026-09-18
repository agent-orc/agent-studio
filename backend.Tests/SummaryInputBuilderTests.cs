using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SummaryInputBuilderTests
{
    [Fact]
    public void Build_UsesIndependentBudgetsAndPreservesTaskPromptHead()
    {
        var root = TempFolder();
        try
        {
            var head = "HEAD-GOAL: preserve this acceptance criterion.";
            File.WriteAllText(Path.Combine(root, "prompt.md"), head + new string('x', 40_000) + "TAIL-EVIDENCE");
            var inputs = SummaryInputBuilder.Build(Task(root, "Budget task"), new string('l', 30_000));

            Assert.StartsWith(head, inputs.TaskPrompt, StringComparison.Ordinal);
            Assert.Contains("[task prompt middle truncated]", inputs.TaskPrompt, StringComparison.Ordinal);
            Assert.EndsWith("TAIL-EVIDENCE", inputs.TaskPrompt, StringComparison.Ordinal);
            Assert.True(inputs.TaskPrompt.Length <= SummaryInputBuilder.TaskPromptBudget);
            Assert.True(inputs.Log.Length <= SummaryInputBuilder.LastRunLogBudget);
            Assert.Equal(78_500, SummaryInputBuilder.TotalEvidenceBudget);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("AGT-2840", "ConnectorProfileTests fail only inside the Windows integration gate", "ASPNETCORE_URLS")]
    [InlineData("AGT-2836", "Coding daemon restart must keep detached coding workers alive", "worker handoff mechanism")]
    [InlineData("AGT-2839", "Local integration gate reuses the passed remote review when the merge base is unchanged", "reuse the passed remote review")]
    public void AcceptanceFixtures_PutTaskGoalBeforeLastRoundLog(
        string key,
        string goal,
        string expectedSolutionEvidence)
    {
        var root = TempFolder();
        try
        {
            var prompt = $"# Goal\n{goal}\n\n# Acceptance\nThe solution must name {expectedSolutionEvidence}.";
            File.WriteAllText(Path.Combine(root, "prompt.md"), prompt);
            var info = Task(root, goal) with { Id = key, TaskKey = key };
            var inputs = SummaryInputBuilder.Build(info,
                "[taskboard] Started codex CLI\nResolved a merge conflict and reapplied a commit.");
            var rendered = Prompts().Render(RuntimePromptService.SummaryProtocol,
                SummaryGenerationService.BuildSummarySlots(info, inputs, "Success"));

            Assert.True(rendered.IndexOf(goal, StringComparison.Ordinal)
                < rendered.IndexOf("Resolved a merge conflict", StringComparison.Ordinal));
            Assert.Contains(expectedSolutionEvidence, inputs.TaskPrompt, StringComparison.Ordinal);
            Assert.Contains("`Problem` must come from the task prompt", rendered, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Build_IncludesRoundLedgerAgentStatusAndDeliveryFacts()
    {
        var root = TempFolder();
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "results"));
        try
        {
            File.WriteAllText(Path.Combine(root, "prompt.md"), "Fix the listener inheritance defect.");
            File.WriteAllText(Path.Combine(root, "results", "status.md"), "Agent delivery: listener fence added.");
            File.WriteAllText(TaskPaths.SessionEventsLog(root), JsonSerializer.Serialize(new SessionEvent
            {
                Ts = DateTime.UtcNow.AddMinutes(-15), Kind = "start", Result = "done", DurationSeconds = 600,
            }) + "\n" + JsonSerializer.Serialize(new SessionEvent
            {
                Ts = DateTime.UtcNow.AddMinutes(-5), Kind = "recovery", Result = "done",
                DurationSeconds = 300, Reason = "integration conflict recovery",
            }) + "\n");
            var task = Task(root, "Listener fix") with
            {
                Commits = [new TaskCommitInfo
                {
                    Sha = "abcdef123", ShortSha = "abcdef1", Message = "Fence inherited listener",
                    FilesChanged = 1, Files = ["backend/Program.cs"], At = DateTime.UtcNow,
                }],
            };

            var inputs = SummaryInputBuilder.Build(task, "last log");

            Assert.Contains("Run 1, initial, 10 min", inputs.Rounds, StringComparison.Ordinal);
            Assert.Contains("Run 2, integration recovery, 5 min", inputs.Rounds, StringComparison.Ordinal);
            Assert.Contains("Agent delivery: listener fence added.", inputs.AgentStatus, StringComparison.Ordinal);
            Assert.Contains("git diff --stat against merge base: 1 files changed", inputs.Delivery, StringComparison.Ordinal);
            Assert.Contains("`backend/Program.cs`", inputs.Delivery, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RenderedPrompt_OrdersAllEvidenceSectionsBeforeTheLastRunLog()
    {
        var info = Task("unused", "TITLE-MARKER");
        var inputs = new SummaryInputs(
            "TITLE-MARKER", "PROMPT-MARKER", "ROUNDS-MARKER",
            "STATUS-MARKER", "DELIVERY-MARKER", "LOG-MARKER");
        var rendered = Prompts().Render(RuntimePromptService.SummaryProtocol,
            SummaryGenerationService.BuildSummarySlots(info, inputs, "Success"));
        var markers = new[]
        {
            "TITLE-MARKER", "PROMPT-MARKER", "ROUNDS-MARKER",
            "STATUS-MARKER", "DELIVERY-MARKER", "LOG-MARKER",
        };
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = rendered.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"{marker} was out of evidence order.");
            previous = current;
        }
    }

    private static RuntimePromptService Prompts()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PromptTemplates:RuntimePath"] = Path.Combine(RepoRoot(), "prompts", "runtime"),
        }).Build();
        return new RuntimePromptService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<RuntimePromptService>.Instance);
    }

    private static TaskInfo Task(string root, string title) => new()
    {
        Id = "fixture", TaskKey = "FIX-1", Title = title, TaskType = TaskTypes.Bug,
        Mode = TaskModes.Coding, State = TaskStates.Progress, FolderPath = root,
        WatchPath = root, ProjectName = "test",
    };

    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "summary-input-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln not found.");
    }
}
