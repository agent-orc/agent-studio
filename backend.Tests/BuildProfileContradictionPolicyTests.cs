using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2827: <c>VerifyCommandPlanner.Plan</c> already gives a valid repository
/// <c>.agent-studio/project.yml</c> outright precedence over a central
/// <see cref="BuildProfile"/> - correct, but silent. These tests pin the pure
/// policy that flags when a declared profile command disagrees with the
/// repository definition that actually wins, so project settings can warn
/// instead of leaving the mismatch to surface as a confusing review failure.
/// </summary>
public sealed class BuildProfileContradictionPolicyTests
{
    [Fact]
    public void Evaluate_flags_a_test_command_that_disagrees_with_the_repository_definition()
    {
        var repository = Definition(test: ["dotnet test QualityStudio.slnx --filter \"Category!=MachineBound&Category!=ExternalLive\""]);
        var profile = new BuildProfile
        {
            TestCmds = ["dotnet test QualityStudio.slnx --filter \"Category!=MachineBound\""],
        };

        var contradictions = BuildProfileContradictionPolicy.Evaluate(repository, profile);

        var contradiction = Assert.Single(contradictions);
        Assert.Equal("test", contradiction.Field);
        Assert.Equal(profile.TestCmds, contradiction.ProfileCommands);
        Assert.Equal(repository.Commands.Test, contradiction.RepositoryCommands);
    }

    [Fact]
    public void Evaluate_flags_a_build_command_that_disagrees_with_the_repository_definition()
    {
        var repository = Definition(test: [], build: ["dotnet build"]);
        var profile = new BuildProfile { BuildCmds = ["npm run build"] };

        var contradictions = BuildProfileContradictionPolicy.Evaluate(repository, profile);

        var contradiction = Assert.Single(contradictions);
        Assert.Equal("build", contradiction.Field);
    }

    [Fact]
    public void Evaluate_is_silent_when_the_profile_command_matches_the_repository_definition()
    {
        var repository = Definition(test: ["npm test"]);
        var profile = new BuildProfile { TestCmds = ["npm test"] };

        Assert.Empty(BuildProfileContradictionPolicy.Evaluate(repository, profile));
    }

    [Fact]
    public void Evaluate_is_silent_when_the_profile_declares_no_command_for_the_field()
    {
        // A profile with no test commands falls through to the repository
        // definition, which is the documented behaviour, not a contradiction.
        var repository = Definition(test: ["npm test"]);
        var profile = new BuildProfile { InstallCmd = "npm ci" };

        Assert.Empty(BuildProfileContradictionPolicy.Evaluate(repository, profile));
    }

    [Fact]
    public void Evaluate_is_silent_when_there_is_no_repository_definition()
    {
        var profile = new BuildProfile { TestCmds = ["npm test"] };

        Assert.Empty(BuildProfileContradictionPolicy.Evaluate(null, profile));
    }

    [Fact]
    public void Evaluate_is_silent_when_there_is_no_declared_profile()
    {
        var repository = Definition(test: ["npm test"]);

        Assert.Empty(BuildProfileContradictionPolicy.Evaluate(repository, null));
    }

    private static AgentStudio.TaskServer.Contracts.ProjectExecutionDefinition Definition(
        IReadOnlyList<string> test,
        IReadOnlyList<string>? build = null)
        => new(
            SchemaVersion: 1,
            Stack: [],
            ToolVersions: new Dictionary<string, string>(),
            Commands: new AgentStudio.TaskServer.Contracts.ProjectCommandSet(
                Prepare: ".agent-studio/prepare",
                Build: build ?? [],
                Test: test,
                Lint: []),
            TestSuites: [],
            CachePaths: [],
            Capabilities: [],
            Environment: new Dictionary<string, string>(),
            DevServer: null,
            Image: null,
            Release: null,
            Project: null,
            Quality: null);
}
