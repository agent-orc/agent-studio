using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectDeploymentDefinitionTests
{
    private const string Base = """
        schemaVersion: 2
        stack: [node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build: [npm run build]
          test: [npm test]
          lint: []
        testSuites:
        cachePaths: []
        capabilities: [git]
        environment:
        project:
          id: website
        """;

    private const string Deployment = """

        pipeline:
          build: [npm run build]
          test: [npm test]
        artifacts:
          - name: web
            type: static
            path: dist
          - name: app
            type: container
            dockerfile: Dockerfile
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
            environments: [staging, production]
          - step: start-container
            artifact: app
            target: run-host
          - step: run
            run: scripts/smoke.sh
          - step: health
            health: /healthz
          - step: approval
            approval: [Owner]
            environments: [production]
        """;

    [Fact]
    public void Parses_p1_artifacts_lifecycle_and_each_step_kind()
    {
        var result = ProjectDefinitionReader.Parse(Base + Deployment);
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        var deployment = Assert.IsType<ProjectDeploymentDefinition>(result.Definition!.Deployment);
        Assert.Equal(new[] { "static", "container" }, deployment.Artifacts.Select(artifact => artifact.Type));
        Assert.Equal(new[] { "staging", "production" }, deployment.Environments.Select(environment => environment.Name));
        Assert.Equal(new[] { "publish-static", "start-container", "run", "health", "approval" },
            deployment.Deploy.Select(step => step.Step));
        Assert.Equal("deploy/website", deployment.Deploy[0].Branch);
        Assert.Equal(new[] { "npm test" }, deployment.PipelineTest);
    }

    [Theory]
    [InlineData("artifact: web", "artifact: missing", "deploy-artifact-unknown")]
    [InlineData("branch: deploy/website", "branch: ", "deploy-branch-required")]
    [InlineData("requires: staging", "requires: production", "production-requires-staging")]
    [InlineData("health: /healthz", "health: healthz", "deploy-health-invalid")]
    [InlineData("environments: [staging, production]", "environments: [production, staging]", "deployment-environment-order-invalid")]
    public void Rejects_invalid_p1_definition_with_field_issue(string before, string after, string code)
    {
        var result = ProjectDefinitionReader.Parse(Base + Deployment.Replace(before, after, StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Code == code && !string.IsNullOrWhiteSpace(issue.Path));
    }

    [Fact]
    public void Schema_two_can_add_pipeline_before_deployment_is_configured()
    {
        var result = ProjectDefinitionReader.Parse(Base + "\npipeline:\n  build: [npm run build]\n  test: [npm test]\n");
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(new[] { "npm run build" }, result.Definition!.Deployment!.PipelineBuild);
    }

    [Fact]
    public void Schema_one_keeps_legacy_contract_and_rejects_deployment_fields()
    {
        var legacy = Base.Replace("schemaVersion: 2", "schemaVersion: 1", StringComparison.Ordinal)
            .Replace("project:\n  id: website", "", StringComparison.Ordinal);
        Assert.True(ProjectDefinitionReader.Parse(legacy).IsValid);
        Assert.Contains(ProjectDefinitionReader.Parse(legacy + Deployment).Issues,
            issue => issue.Code == "v1-does-not-support-deployment");
    }



    [Fact]
    public void Import_rejects_unsupported_descriptor_without_mutating_definition()
    {
        var root = Path.Combine(Path.GetTempPath(), "deployment-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var studio = Path.Combine(root, ".agent-studio");
            var descriptor = Path.Combine(root, "docs", "deployments", "unknown");
            Directory.CreateDirectory(studio);
            Directory.CreateDirectory(descriptor);
            File.WriteAllText(Path.Combine(studio, "prepare"), "#!/bin/sh\n");
            File.WriteAllText(Path.Combine(studio, "project.yml"), Base);
            File.WriteAllText(Path.Combine(descriptor, "deployment.json"),
                """{"id":"unknown","kind":"template","template":"unknown"}""");

            var result = ProjectDeploymentImport.ImportWorkspace(root);
            Assert.False(result.Changed);
            Assert.Contains(result.Issues, issue => issue.Code == "deployment-import-incomplete");
            Assert.Equal(Base, File.ReadAllText(Path.Combine(studio, "project.yml")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_refuses_a_command_that_yaml_would_truncate()
    {
        var root = Path.Combine(Path.GetTempPath(), "deployment-comment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var studio = Path.Combine(root, ".agent-studio");
            var descriptor = Path.Combine(root, "docs", "deployments", "website");
            Directory.CreateDirectory(studio);
            Directory.CreateDirectory(descriptor);
            File.WriteAllText(Path.Combine(studio, "prepare"), "#!/bin/sh\n");
            File.WriteAllText(Path.Combine(studio, "project.yml"), Base);
            File.WriteAllText(Path.Combine(descriptor, "deployment.json"),
                """{"id":"website","command":"scripts/deploy.sh # production"}""");

            var result = ProjectDeploymentImport.ImportWorkspace(root);
            Assert.False(result.Changed);
            Assert.Contains(result.Issues, issue => issue.Code == "deployment-import-value-changed");
            Assert.Equal(Base, File.ReadAllText(Path.Combine(studio, "project.yml")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_maps_caddy_template_without_command_to_static_publish()
    {
        var root = Path.Combine(Path.GetTempPath(), "deployment-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            var studio = Path.Combine(root, ".agent-studio");
            var descriptor = Path.Combine(root, "docs", "deployments", "website");
            Directory.CreateDirectory(studio);
            Directory.CreateDirectory(descriptor);
            File.WriteAllText(Path.Combine(studio, "prepare"), "#!/bin/sh\n");
            File.WriteAllText(Path.Combine(studio, "project.yml"), Base);
            File.WriteAllText(Path.Combine(descriptor, "deployment.json"),
                """{"id":"website","kind":"template","template":"caddy-site","targetHostId":"web-vm"}""");

            var result = ProjectDeploymentImport.ImportWorkspace(root);
            Assert.True(result.Changed, string.Join("; ", result.Issues.Select(issue => issue.Message)));
            var step = Assert.Single(ProjectDefinitionReader.ReadWorkspace(root).Definition!.Deployment!.Deploy);
            Assert.Equal("publish-static", step.Step);
            Assert.Equal("web-vm", step.Target);
            Assert.Equal("deploy/website", step.Branch);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_is_one_time_and_preserves_legacy_descriptor_and_line_endings()
    {
        var root = Path.Combine(Path.GetTempPath(), "deployment-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var studio = Path.Combine(root, ".agent-studio");
            var descriptor = Path.Combine(root, "docs", "deployments", "website");
            Directory.CreateDirectory(studio);
            Directory.CreateDirectory(descriptor);
            File.WriteAllText(Path.Combine(studio, "prepare"), "#!/bin/sh\n");
            File.WriteAllText(Path.Combine(studio, "project.yml"),
                Base.Replace("schemaVersion: 2", "schemaVersion: 1", StringComparison.Ordinal)
                    .Replace("project:\n  id: website", "", StringComparison.Ordinal)
                    .Replace("\n", "\r\n", StringComparison.Ordinal));
            var legacyPath = Path.Combine(descriptor, "deployment.json");
            const string legacyJson = """
                {"id":"website","kind":"template","template":"caddy-site","command":"scripts/deploy.sh","targetHostId":"web-vm"}
                """;
            File.WriteAllText(legacyPath, legacyJson);

            var imported = ProjectDeploymentImport.ImportWorkspace(root);
            Assert.True(imported.Changed, string.Join("; ", imported.Issues.Select(issue => issue.Message)));
            Assert.Single(imported.ImportedFiles);
            var yaml = File.ReadAllText(Path.Combine(studio, "project.yml"));
            Assert.DoesNotContain("\n", yaml.Replace("\r\n", "", StringComparison.Ordinal));
            Assert.Contains("schemaVersion: 2", yaml);
            Assert.Equal("scripts/deploy.sh", Assert.Single(ProjectDefinitionReader.ReadWorkspace(root).Definition!.Deployment!.Deploy).Run);
            Assert.Equal(legacyJson, File.ReadAllText(legacyPath));
            Assert.False(ProjectDeploymentImport.ImportWorkspace(root).Changed);
            Assert.Equal(yaml, File.ReadAllText(Path.Combine(studio, "project.yml")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
