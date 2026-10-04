using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectModelMigrationSelectionTests
{
    [Fact]
    public void ProjectAcceptanceSelectsOnlyMatchingExplicitIdleCards()
    {
        var jobs = new[]
        {
            Job("matching", "/workspace/alpha", "2-ready", true, "gpt-5.6-sol"),
            Job("policy", "/workspace/alpha", "2-ready", false, "gpt-5.6-sol"),
            Job("running", "/workspace/alpha", "3-progress", true, "gpt-5.6-sol"),
            Job("review", "/workspace/alpha", "4-auto-review", true, "gpt-5.6-sol"),
            Job("other-project", "/workspace/beta", "2-ready", true, "gpt-5.6-sol"),
            Job("other-model", "/workspace/alpha", "2-ready", true, "gpt-5.6-luna"),
        };

        var selected = TaskCrudEndpoints.SelectProjectMigrationCandidates(
            jobs, "/workspace/alpha", "gpt-5.6-sol");

        Assert.Equal("matching", Assert.Single(selected).Id);
    }

    private static TaskInfo Job(string id, string watchPath, string state, bool explicitPin, string model)
        => new()
        {
            Id = id,
            WatchPath = watchPath,
            State = state,
            ModelExplicit = explicitPin,
            Model = model,
        };
}
