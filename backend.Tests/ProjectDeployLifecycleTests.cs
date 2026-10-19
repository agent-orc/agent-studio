using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectDeployLifecycleTests
{
    private const string Base = """
        schemaVersion: 2
        stack: [node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build: [npm run build]
          test: [npm test]
          lint: [npm run lint]
        testSuites:
        cachePaths: []
        capabilities: [git]
        environment:
        project:
          id: website
        """;

    private const string Lifecycle = """

        artifacts:
          - name: web
            type: static
            path: dist
        environments: [staging, production]
        lifecycles:
          standard:
            - phase: staging
              automatic: true
            - phase: production
              approval: [Owner, Maintainer]
              requires: staging
        deploy:
          - step: publish-static
            artifact: web
            target: web-vm
            branch: deploy/website
          - step: health
            health: /healthz
            environments: [staging]
          - step: approval
            approval: [Owner]
            environments: [production]
        """;

    private static ProjectExecutionDefinition Parse(string yaml)
    {
        var read = ProjectDefinitionReader.Parse(yaml);
        Assert.True(read.IsValid, string.Join("; ", read.Issues.Select(issue => issue.Message)));
        return read.Definition!;
    }

    [Fact]
    public void NoDefinition_HasNoLifecycle()
    {
        Assert.Null(ProjectDeployLifecycleView.From(null));
    }

    [Fact]
    public void SchemaWithoutDeploymentSection_IsNotDeclared()
    {
        var view = ProjectDeployLifecycleView.From(Parse(Base));

        Assert.NotNull(view);
        Assert.False(view!.Declared);
        Assert.Empty(view.Nodes);
    }

    [Fact]
    public void DeclaredLifecycle_OrdersGateArtifactThenPhases()
    {
        var view = ProjectDeployLifecycleView.From(Parse(Base + Lifecycle))!;

        Assert.True(view.Declared);
        Assert.Equal(["gate", "artifact", "staging", "production"], view.Nodes.Select(node => node.Id));
        Assert.Equal(["gate", "artifact", "environment", "environment"], view.Nodes.Select(node => node.Kind));
        Assert.Equal(["web (static, dist)"], view.Nodes[1].Items);
        Assert.Equal("artifact", view.Nodes[2].Requires);
        Assert.Equal("staging", view.Nodes[3].Requires);
    }

    [Fact]
    public void Gate_FallsBackToCommandsWhenPipelineIsAbsent()
    {
        var view = ProjectDeployLifecycleView.From(Parse(Base + Lifecycle))!;

        Assert.Equal(["npm run build", "npm test", "npm run lint"], view.Nodes[0].Items);
    }

    [Fact]
    public void Gate_PrefersDeclaredPipelineCommands()
    {
        var yaml = Base + """

            pipeline:
              build: [npm ci, npm run build]
              test: [npm run test:ci]
            """ + Lifecycle;

        var view = ProjectDeployLifecycleView.From(Parse(yaml))!;

        Assert.Equal(["npm ci", "npm run build", "npm run test:ci"], view.Nodes[0].Items);
    }

    [Fact]
    public void Steps_AreScopedToTheirEnvironments_AndUnscopedStepsRunEverywhere()
    {
        var view = ProjectDeployLifecycleView.From(Parse(Base + Lifecycle))!;
        var staging = view.Nodes.Single(node => node.Id == "staging");
        var production = view.Nodes.Single(node => node.Id == "production");

        Assert.True(staging.Automatic);
        Assert.Empty(staging.Approval);
        Assert.Empty(staging.Items);
        Assert.Equal(
        [
            new DeployLifecycleStep("publish-static", "web", "web-vm", "deploy/website", null),
            new DeployLifecycleStep("health", null, null, null, "/healthz"),
        ], staging.Steps);
        Assert.False(production.Automatic);
        Assert.Equal(["Owner", "Maintainer"], production.Approval);
        Assert.Equal(
        [
            new DeployLifecycleStep("publish-static", "web", "web-vm", "deploy/website", null),
            new DeployLifecycleStep("approval", null, null, "Owner", null),
        ], production.Steps);
    }

    [Fact]
    public void RunStep_ShowsItsCommand()
    {
        var yaml = Base + Lifecycle + """

              - step: run
                run: ./smoke.sh
                environments: [staging]
            """;

        var staging = ProjectDeployLifecycleView.From(Parse(yaml))!.Nodes.Single(node => node.Id == "staging");

        Assert.Equal(new DeployLifecycleStep("run", null, null, "./smoke.sh", null), staging.Steps[^1]);
    }

    [Fact]
    public void InvalidDefinition_StillProjectsTheParsedLifecycle_WithStaticSiteAndContainerSteps()
    {
        // static-site and container are AGT-3221 step kinds; this server's
        // validator rejects them, yet the diagram still shows what they deploy where.
        var yaml = Base + """

            artifacts:
              - name: web
                type: static
                path: dist
              - name: api
                type: container
                dockerfile: Dockerfile
            environments: [staging, production]
            lifecycles:
              standard:
                - phase: staging
                  automatic: true
                - phase: production
                  approval: [Owner]
                  requires: staging
            deploy:
              - step: static-site
                artifact: web
                target: web-vm
              - step: container
                artifact: api
                target: app-vm
            """;
        var read = ProjectDefinitionReader.Parse(yaml);

        Assert.False(read.IsValid);
        Assert.Contains(read.Issues, issue => issue.Code == "deploy-step-invalid");
        var view = ProjectDeployLifecycleView.From(read.Definition ?? read.Parsed);

        Assert.NotNull(view);
        Assert.Equal(["gate", "artifact", "staging", "production"], view!.Nodes.Select(node => node.Id));
        Assert.Equal(
        [
            new DeployLifecycleStep("static-site", "web", "web-vm", null, null),
            new DeployLifecycleStep("container", "api", "app-vm", null, null),
        ], view.Nodes[2].Steps);
    }
}
