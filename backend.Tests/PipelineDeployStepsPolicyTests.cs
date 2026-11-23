using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F5 of the playthrough: a schema 2 project.yml written by the migration has
/// Staging and Production but no deploy steps, and the Settings > Pipeline form
/// must turn the operator's values into valid steps without a shell.
/// </summary>
public sealed class PipelineDeployStepsPolicyTests
{
    private const string SchemaOne = """
        schemaVersion: 1
        stack: [node]
        toolVersions: {}
        commands:
          prepare: .agent-studio/prepare
          build:
            - npm run build
          test:
            - npm test
          lint: []
        testSuites:
          - id: unit
            category: routine
            expectedDurationSeconds: 30
            command: npm test
        cachePaths: [node_modules, dist]
        capabilities: [git, child-process]
        environment: {}
        """;

    /// <summary>The schema 2 file the migration writes when no JSON declarations exist (the playthrough's 2859917).</summary>
    private static string Migrated()
    {
        var proposal = PipelineMigrationPolicy.Propose(SchemaOne, ProjectDefinitionReader.Parse(SchemaOne),
            new PipelineMigrationFacts("shop", [], false, null));
        Assert.True(proposal.Available);
        return proposal.ProposedYaml!;
    }

    private static PipelineDeployStepsRequest StaticForm(string stagingTarget = "web-vm") => new("static", "dist",
    [
        new("staging", stagingTarget, "/srv/www/_staging/shop", null, null, "https://shop.example.test/_staging/", 60),
        new("production", "web-vm", "/srv/www/shop", null, null, "https://shop.example.test/", 120),
    ]);

    [Fact]
    public void The_migrated_file_without_deploy_steps_yields_no_deployable_environment()
    {
        var error = Assert.Throws<JsonException>(() => ProjectEnvironmentService.ApplyDefinition([], Migrated()));

        Assert.Contains("deploy steps", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Static_form_appends_valid_steps_that_deploy_both_environments()
    {
        var current = Migrated();

        var proposal = PipelineDeployStepsPolicy.Propose(current, ProjectDefinitionReader.Parse(current), StaticForm());

        Assert.True(proposal.Available, string.Join("; ", proposal.Notes));
        Assert.StartsWith(current.TrimEnd('\n'), proposal.ProposedYaml!, StringComparison.Ordinal);
        var parsed = ProjectDefinitionReader.Parse(proposal.ProposedYaml!);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        var deployment = parsed.Definition!.Deployment!;
        var artifact = Assert.Single(deployment.Artifacts);
        Assert.Equal(("site", "static", "dist"), (artifact.Name, artifact.Type, artifact.Path));
        Assert.Equal(["/srv/www/_staging/shop", "/srv/www/shop"], deployment.Deploy.Select(step => step.Directory));
        Assert.Equal([60, 120], deployment.Deploy.Select(step => step.HealthTimeoutSeconds ?? 0));

        var environments = ProjectEnvironmentService.ApplyDefinition([], proposal.ProposedYaml!);
        Assert.Equal(["stage", "production"], environments.Select(file => file.Declaration!.Id));
        Assert.All(environments, file => Assert.Equal("web-vm", file.Declaration!.TargetHostId));
    }

    [Fact]
    public void Container_form_writes_compose_file_and_service()
    {
        var current = Migrated();
        var form = new PipelineDeployStepsRequest("container", "Dockerfile",
        [
            new("production", "app-vm", null, "/srv/apps/shop/production/compose.yml", "shop", "https://shop.example.test/healthz", 90),
            new("staging", "app-vm", null, "/srv/apps/shop/staging/compose.yml", "shop", "https://staging.shop.example.test/healthz", 90),
        ]);

        var proposal = PipelineDeployStepsPolicy.Propose(current, ProjectDefinitionReader.Parse(current), form);

        var parsed = ProjectDefinitionReader.Parse(proposal.ProposedYaml!);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        var deployment = parsed.Definition!.Deployment!;
        Assert.Equal(("app", "container", "Dockerfile"),
            (deployment.Artifacts[0].Name, deployment.Artifacts[0].Type, deployment.Artifacts[0].Dockerfile));
        Assert.Equal(["staging", "production"], deployment.Deploy.Select(step => Assert.Single(step.Environments)));
        Assert.All(deployment.Deploy, step => Assert.Equal(("container", "shop"), (step.Step, step.Service)));
    }

    [Fact]
    public void A_value_that_could_inject_yaml_is_refused()
    {
        var current = Migrated();

        var proposal = PipelineDeployStepsPolicy.Propose(current, ProjectDefinitionReader.Parse(current),
            StaticForm("web-vm\n  - step: run\n    run: rm -rf /"));

        Assert.False(proposal.Available);
        Assert.Equal("validation", proposal.Reason);
        Assert.Null(proposal.ProposedYaml);
    }

    [Fact]
    public void Every_declared_environment_needs_one_step()
    {
        var current = Migrated();
        var form = StaticForm() with { Environments = [StaticForm().Environments![0]] };

        var proposal = PipelineDeployStepsPolicy.Propose(current, ProjectDefinitionReader.Parse(current), form);

        Assert.False(proposal.Available);
        Assert.Contains("production", proposal.Notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_one_and_files_with_deploy_steps_are_not_offered_the_form()
    {
        Assert.Equal("schema-2-required",
            PipelineDeployStepsPolicy.Propose(SchemaOne, ProjectDefinitionReader.Parse(SchemaOne), StaticForm()).Reason);

        var current = Migrated();
        var withSteps = PipelineDeployStepsPolicy.Propose(current, ProjectDefinitionReader.Parse(current), StaticForm()).ProposedYaml!;
        Assert.Equal("deploy-steps-present",
            PipelineDeployStepsPolicy.Propose(withSteps, ProjectDefinitionReader.Parse(withSteps), StaticForm()).Reason);
    }
}
