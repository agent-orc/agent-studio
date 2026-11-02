using AgentStudio.TaskServer.Contracts;
using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AOW-15: the failure reason of an invalid repository execution definition
/// names each issue with path, code and message, stays on one line, and is
/// bounded. The review-time check reports the same issues to the agent.
/// </summary>
public sealed class ProjectDefinitionIssueTextTests : IDisposable
{
    private const string WithoutPrepare = """
        schemaVersion: 1
        stack: [shell]
        commands:
          build: []
          test: []
          lint: []
        """;

    private const string Minimal = """
        schemaVersion: 1
        stack: [shell]
        commands:
          prepare: .agent-studio/prepare
          build: []
          test: []
          lint: []
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "definition-text-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void InvalidReason_ListsEveryIssueWithPathCodeAndMessage()
    {
        var reason = ProjectDefinitionIssueText.InvalidReason(
        [
            new("commands.prepare", "prepare-path-invalid", "Prepare must be a repository-relative path without traversal.", 3),
            new("stack", "stack-required", "At least one stack is required."),
        ]);

        Assert.Equal(
            "Repository execution definition is invalid (.agent-studio/project.yml): "
            + "commands.prepare (line 3) [prepare-path-invalid] Prepare must be a repository-relative path without traversal; "
            + "stack [stack-required] At least one stack is required.",
            reason);
        Assert.Equal(["commands.prepare [prepare-path-invalid]", "stack [stack-required]"],
            ProjectDefinitionIssueText.IssueKeys(reason));
    }

    [Fact]
    public void InvalidReason_IsOneLine_AndBounded_CountingWhatDoesNotFit()
    {
        var issues = Enumerable.Range(0, 40)
            .Select(index => new ProjectDefinitionIssue(
                $"testSuites[{index}].id", "suite-id-invalid", "Test suite ids are required\nand unique."))
            .ToArray();

        var reason = ProjectDefinitionIssueText.InvalidReason(issues, maxLength: 300);

        Assert.True(reason.Length <= 300, reason);
        Assert.DoesNotContain('\n', reason);
        Assert.Contains("testSuites[0].id [suite-id-invalid] Test suite ids are required and unique", reason);
        Assert.Matches(@"; and \d+ more issue\(s\)\.$", reason);
        Assert.Contains("testSuites[0].id [suite-id-invalid]", ProjectDefinitionIssueText.IssueKeys(reason));
    }

    [Fact]
    public void InvalidReason_ShortensASingleOverlongIssueInsteadOfDroppingIt()
    {
        var reason = ProjectDefinitionIssueText.InvalidReason(
            [new("commands.prepare", "prepare-path-invalid", new string('x', 5000))], maxLength: 200);

        Assert.True(reason.Length <= 200, reason);
        Assert.Contains("commands.prepare [prepare-path-invalid]", reason);
    }

    [Fact]
    public async Task Preparation_OfADefinitionWithoutPrepare_ReturnsTheIssuesInItsReason()
    {
        var workspace = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(workspace, ".agent-studio"));
        File.WriteAllText(Path.Combine(workspace, ".agent-studio", "project.yml"), WithoutPrepare);

        var result = await ProjectPreparationExecutor.RunAsync(
            workspace, Path.Combine(_root, "cache"), Path.Combine(_root, "manifest.json"), null, null,
            TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PreparationFailureKind.Definition, result.FailureKind);
        Assert.Contains(result.DefinitionIssues, issue => issue.Code == "prepare-path-invalid");
        Assert.StartsWith(ProjectDefinitionIssueText.InvalidPrefix, result.FailureReason);
        Assert.Contains("commands.prepare", result.FailureReason);
        Assert.Contains("[prepare-path-invalid]", result.FailureReason);
        Assert.DoesNotContain("host", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentedMinimalDefinition_IsValid_WhenThePrepareScriptExists()
    {
        Assert.Empty(ProjectDefinitionReader.Parse(Minimal, path => path == ".agent-studio/prepare").Issues);
        Assert.Equal(["commands.prepare [prepare-script-missing]"],
            ProjectDefinitionReader.Parse(Minimal, _ => false).Issues
                .Select(issue => $"{issue.Path} [{issue.Code}]"));
    }

    [Fact]
    public void ReviewCheck_InvalidDelivery_ReissuesWithOneFindingPerIssue()
    {
        var decision = ProjectDefinitionGate.Evaluate(WithoutPrepare, _ => true, priorReissues: 0, maxReissues: 3);

        Assert.Equal(CompletionGate.CompletionGateAction.Reissue, decision.Action);
        Assert.Equal(CompletionGate.CompletionGateSource.ProjectDefinition, decision.Source);
        var finding = Assert.Single(decision.Findings);
        Assert.StartsWith("commands.prepare", finding);
        Assert.Contains("[prepare-path-invalid] Prepare must be a repository-relative path", finding);
        Assert.Contains("[prepare-path-invalid]", decision.Reason);

        var followUp = CompletionGate.BuildFollowUp(decision.Findings, source: decision.Source);
        Assert.Contains("project definition check", followUp);
        Assert.Contains("commands.prepare is required", followUp);
        Assert.Contains("- [ ] " + finding, followUp);
        Assert.DoesNotContain("unfinished work", followUp);
    }

    [Fact]
    public void ReviewCheck_BudgetSpent_Escalates()
    {
        var decision = ProjectDefinitionGate.Evaluate(WithoutPrepare, _ => true, priorReissues: 3, maxReissues: 3);

        Assert.Equal(CompletionGate.CompletionGateAction.Escalate, decision.Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Minimal)]
    public void ReviewCheck_AbsentOrValidDefinition_Passes(string? yaml)
    {
        var decision = ProjectDefinitionGate.Evaluate(yaml, _ => true, priorReissues: 0, maxReissues: 3);

        Assert.False(decision.IsIncomplete);
        Assert.Empty(decision.Findings);
    }
}
