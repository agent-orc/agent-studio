using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// project.yml schema 2 for stage P1 (AGT-3221): protected environments,
/// static-site and container steps over SSH with health checks, scoped
/// variables that name secrets, issue lines, schema 1 compatibility, and the
/// JSON schema staying in step with the validator.
/// </summary>
public sealed class ProjectDefinitionSchemaTwoTests
{
    private const string Head = """
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

    private const string Pipeline = """

        artifacts:
          - name: site
            type: static
            path: dist
          - name: api
            type: container
            dockerfile: Dockerfile
            image: ghcr.io/agent-orc/api
        environments:
          - name: staging
            protected: false
          - name: production
            protected: true
        lifecycles:
          standard:
            - phase: staging
              automatic: true
            - phase: production
              approval: [Owner, Maintainer]
              requires: staging
        deploy:
          - step: static-site
            artifact: site
            target: web-vm
            directory: /srv/www/_staging/studio
            healthUrl: https://agent-orchestrator.dev/_staging/studio/
            healthTimeoutSeconds: 60
            environments: [staging]
          - step: container
            artifact: api
            target: run-host
            composeFile: /srv/apps/api/compose.yml
            service: api
            healthUrl: https://api.agent-orchestrator.dev/healthz
            healthTimeoutSeconds: 120
            environments: [production]
        variables:
          - name: LOG_LEVEL
            value: info
          - name: DATABASE_URL
            secret: DATABASE_URL
            environments: [production]
        """;

    private static string Valid => Head + Pipeline;

    [Fact]
    public void Parses_artifacts_protected_environments_ssh_steps_and_scoped_variables()
    {
        var result = ProjectDefinitionReader.Parse(Valid);

        Assert.True(result.IsValid, Describe(result));
        var deployment = result.Definition!.Deployment!;
        Assert.Equal(["staging", "production"], deployment.Environments.Select(environment => environment.Name));
        Assert.Equal([false, true], deployment.Environments.Select(environment => environment.Protected));
        Assert.Equal("ghcr.io/agent-orc/api", deployment.Artifacts[1].Image);
        var site = deployment.Deploy[0];
        Assert.Equal(("static-site", "web-vm", "/srv/www/_staging/studio", 60),
            (site.Step, site.Target, site.Directory, site.HealthTimeoutSeconds));
        var api = deployment.Deploy[1];
        Assert.Equal(("container", "/srv/apps/api/compose.yml", "api", "https://api.agent-orchestrator.dev/healthz"),
            (api.Step, api.ComposeFile, api.Service, api.HealthUrl));
        Assert.Equal(["LOG_LEVEL", "DATABASE_URL"], deployment.Variables.Select(variable => variable.Name));
        Assert.Equal("DATABASE_URL", deployment.Variables[1].Secret);
        Assert.Null(deployment.Variables[1].Value);
        Assert.False(deployment.EnvironmentsDefaulted);
        Assert.False(deployment.LifecycleDefaulted);
    }

    [Fact]
    public void Staging_and_protected_production_with_the_standard_lifecycle_are_the_default()
    {
        var withoutEnvironments = Cut(Cut(Valid, "environments:", "lifecycles:"), "lifecycles:", "deploy:");

        var result = ProjectDefinitionReader.Parse(withoutEnvironments);

        Assert.True(result.IsValid, Describe(result));
        var deployment = result.Definition!.Deployment!;
        Assert.True(deployment.EnvironmentsDefaulted);
        Assert.True(deployment.LifecycleDefaulted);
        Assert.Equal([("staging", false), ("production", true)],
            deployment.Environments.Select(environment => (environment.Name, environment.Protected)));
        Assert.Equal(["Owner", "Maintainer"], deployment.StandardLifecycle[1].Approval);
    }

    [Fact]
    public void Shorthand_environment_list_protects_production()
    {
        var shorthand = Cut(Valid, "environments:", "lifecycles:")
            .Replace("lifecycles:", "environments: [staging, production]\nlifecycles:", StringComparison.Ordinal);

        var result = ProjectDefinitionReader.Parse(shorthand);

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal([false, true], result.Definition!.Deployment!.Environments.Select(environment => environment.Protected));
    }

    [Theory]
    [InlineData("    service: api\n", "    service: api\n    branch: deploy/api\n", "deploy[1].branch")]
    [InlineData("    directory: /srv/www/_staging/studio\n", "    directory: /srv/www/_staging/studio\n    composeFile: /srv/x.yml\n", "deploy[0].composeFile")]
    [InlineData("    protected: true\n", "    protected: true\n    region: eu\n", "environments[1].region")]
    [InlineData("    value: info\n", "    value: info\n    scope: global\n", "variables[0].scope")]
    [InlineData("    image: ghcr.io/agent-orc/api\n", "    image: ghcr.io/agent-orc/api\n    tag: latest\n", "artifacts[1].tag")]
    [InlineData("variables:\n", "releases:\n  - name: one\nvariables:\n", "releases")]
    public void Unknown_keys_are_refused_with_their_path(string before, string after, string path)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, before, after));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "unknown-field" && issue.Path == path);
    }

    [Fact]
    public void Production_before_staging_is_refused_in_environments_and_in_the_lifecycle()
    {
        var environments = Replace(Valid,
            "  - name: staging\n    protected: false\n  - name: production\n    protected: true\n",
            "  - name: production\n    protected: true\n  - name: staging\n    protected: false\n");
        var lifecycle = Replace(Valid,
            "    - phase: staging\n      automatic: true\n    - phase: production\n      approval: [Owner, Maintainer]\n      requires: staging\n",
            "    - phase: production\n      approval: [Owner, Maintainer]\n      requires: staging\n    - phase: staging\n      automatic: true\n");

        Assert.Contains(ProjectDefinitionReader.Parse(environments).Issues,
            issue => issue.Code == "deployment-environment-order-invalid" && issue.Path == "environments");
        Assert.Contains(ProjectDefinitionReader.Parse(lifecycle).Issues,
            issue => issue.Code == "standard-lifecycle-invalid" && issue.Path == "lifecycles.standard");
    }

    [Fact]
    public void An_explicitly_empty_environment_list_is_refused()
    {
        var empty = Cut(Valid, "environments:", "lifecycles:")
            .Replace("lifecycles:", "environments:\nlifecycles:", StringComparison.Ordinal);

        Assert.Contains(ProjectDefinitionReader.Parse(empty).Issues, issue => issue.Code == "deployment-environments-invalid");
    }

    [Fact]
    public void Unprotected_production_is_refused()
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, "    protected: true\n", "    protected: false\n"));

        Assert.Contains(result.Issues, issue =>
            issue.Code == "production-protection-required" && issue.Path == "environments[1].protected");
    }

    public static TheoryData<string, string> InlineSecrets() => new()
    {
        // Credential-shaped values are assembled at runtime (AGENTS.md fixture rule).
        { "API_TOKEN", "abc" + "123" },
        { "SMTP_PASSWORD", "hunter" + "2" },
        { "DATABASE_URL", "postgres://app:" + "pw" + "@db.internal/app" },
        { "SIGNING_MATERIAL", "-----BEGIN " + "PRIVATE KEY-----" },
        { "UPSTREAM_AUTH", "Bearer " + "abc.def" },
    };

    [Theory]
    [MemberData(nameof(InlineSecrets))]
    public void Inline_secret_values_are_refused(string name, string value)
    {
        var yaml = Replace(Valid, "  - name: LOG_LEVEL\n    value: info\n", $"  - name: {name}\n    value: \"{value}\"\n");

        var result = ProjectDefinitionReader.Parse(yaml);

        Assert.Contains(result.Issues, issue => issue.Code == "secret-value-inline" && issue.Path == "variables[0].value");
        Assert.DoesNotContain(result.Issues, issue => issue.Message.Contains(value, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("    secret: DATABASE_URL\n", "    secret: \"postgres://db/app\"\n", "variable-secret-name-invalid")]
    [InlineData("    secret: DATABASE_URL\n", "    secret: DATABASE_URL\n    value: x\n", "variable-source-invalid")]
    [InlineData("    secret: DATABASE_URL\n    environments: [production]", "    secret: DATABASE_URL\n    environments: [preview]", "variable-environment-unknown")]
    [InlineData("  - name: DATABASE_URL\n", "  - name: LOG_LEVEL\n", "variable-duplicate")]
    public void Variable_rules_name_the_offending_entry(string before, string after, string code)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, before, after));

        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path.StartsWith("variables[1]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("    healthUrl: https://api.agent-orchestrator.dev/healthz\n", "", "deploy[1].healthUrl", "deploy-health-url-invalid")]
    [InlineData("    healthTimeoutSeconds: 120\n", "    healthTimeoutSeconds: 0\n", "deploy[1].healthTimeoutSeconds", "deploy-health-timeout-invalid")]
    [InlineData("    healthTimeoutSeconds: 120\n", "    healthTimeoutSeconds: 901\n", "deploy[1].healthTimeoutSeconds", "deploy-health-timeout-invalid")]
    [InlineData("    healthTimeoutSeconds: 60\n", "    healthTimeoutSeconds: soon\n", "deploy[0].healthTimeoutSeconds", "deploy-health-timeout-invalid")]
    [InlineData("    directory: /srv/www/_staging/studio\n", "    directory: srv/www\n", "deploy[0].directory", "deploy-directory-invalid")]
    [InlineData("    directory: /srv/www/_staging/studio\n", "    directory: /srv/../etc\n", "deploy[0].directory", "deploy-directory-invalid")]
    [InlineData("    composeFile: /srv/apps/api/compose.yml\n", "    composeFile: /srv/apps/api/compose.json\n", "deploy[1].composeFile", "deploy-compose-file-invalid")]
    [InlineData("    target: run-host\n", "    target: deploy@run-host\n", "deploy[1].target", "deploy-target-invalid")]
    [InlineData("    artifact: api\n", "    artifact: site\n", "deploy[1].artifact", "deploy-container-artifact-required")]
    public void Ssh_steps_need_target_destination_and_health_check(string before, string after, string path, string code)
    {
        var result = ProjectDefinitionReader.Parse(Replace(Valid, before, after));

        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path == path);
    }

    [Fact]
    public void Issues_carry_the_line_of_their_path()
    {
        var yaml = Replace(Valid, "    healthTimeoutSeconds: 120\n", "    healthTimeoutSeconds: 0\n")
            .Replace("    protected: true\n", "    protected: true\n    region: eu\n", StringComparison.Ordinal);

        var result = ProjectDefinitionReader.Parse(yaml);

        var timeout = Assert.Single(result.Issues, issue => issue.Path == "deploy[1].healthTimeoutSeconds");
        Assert.Equal(LineOf(yaml, "    healthTimeoutSeconds: 0"), timeout.Line);
        var unknown = Assert.Single(result.Issues, issue => issue.Path == "environments[1].region");
        Assert.Equal(LineOf(yaml, "    region: eu"), unknown.Line);
    }

    [Fact]
    public void Issue_for_a_missing_field_points_at_its_entry_and_a_tab_at_its_own_line()
    {
        var yaml = Replace(Valid, "    service: api\n", string.Empty) + "\tbroken: true\n";

        var result = ProjectDefinitionReader.Parse(yaml);

        var service = Assert.Single(result.Issues, issue => issue.Path == "deploy[1].service");
        Assert.Equal(LineOf(yaml, "  - step: container"), service.Line);
        var tab = Assert.Single(result.Issues, issue => issue.Code == "yaml-tab");
        Assert.Equal(yaml.Split('\n').Length - 1, tab.Line);
    }

    [Fact]
    public void Schema_one_keeps_working_and_refuses_the_schema_two_sections()
    {
        var schemaOne = Head.Replace("schemaVersion: 2", "schemaVersion: 1", StringComparison.Ordinal)
            .Replace("project:\n  id: website", string.Empty, StringComparison.Ordinal);

        var legacy = ProjectDefinitionReader.Parse(schemaOne);
        var mixed = ProjectDefinitionReader.Parse(schemaOne + "variables:\n  - name: LOG_LEVEL\n    value: info\n");

        Assert.True(legacy.IsValid, Describe(legacy));
        Assert.Null(legacy.Definition!.Deployment);
        Assert.Contains(mixed.Issues, issue => issue.Code == "v1-does-not-support-deployment");
    }

    [Fact]
    public void The_repository_schema_one_definition_stays_valid()
    {
        var root = RepositoryRoot.Find();

        var result = ProjectDefinitionReader.ReadWorkspace(root);

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal(1, result.Definition!.SchemaVersion);
    }

    [Fact]
    public void Json_schema_declares_the_validator_vocabulary()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "docs", "app", "schemas", "project-execution.schema.json")));
        var properties = schema.RootElement.GetProperty("properties");

        foreach (var section in ProjectDeploymentVocabulary.Sections)
            Assert.True(properties.TryGetProperty(section, out _), $"schema lacks '{section}'");
        var deployItem = properties.GetProperty("deploy").GetProperty("items");
        Assert.Equal(ProjectDeploymentVocabulary.StepKinds,
            deployItem.GetProperty("properties").GetProperty("step").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        foreach (var kind in new[] { "static-site", "container" })
        {
            var rule = deployItem.GetProperty("allOf").EnumerateArray().Single(entry =>
                entry.GetProperty("if").GetProperty("properties").GetProperty("step").TryGetProperty("const", out var value)
                && value.GetString() == kind);
            var names = rule.GetProperty("then").GetProperty("allOf")[1].GetProperty("propertyNames").GetProperty("enum");
            Assert.Equal(ProjectDeploymentVocabulary.StepKeys(kind), names.EnumerateArray().Select(item => item.GetString()));
        }
        Assert.Equal(ProjectDeploymentVocabulary.ArtifactTypes,
            properties.GetProperty("artifacts").GetProperty("items").GetProperty("oneOf").EnumerateArray()
                .Select(item => item.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()));
        Assert.Equal(ProjectDeploymentVocabulary.VariableKeys,
            properties.GetProperty("variables").GetProperty("items").GetProperty("properties").EnumerateObject().Select(item => item.Name));
    }

    private static string Replace(string text, string before, string after)
    {
        Assert.Contains(before, text, StringComparison.Ordinal);
        return text.Replace(before, after, StringComparison.Ordinal);
    }

    /// <summary>Removes the section that starts at <paramref name="from"/> up to <paramref name="to"/>.</summary>
    private static string Cut(string text, string from, string to)
    {
        var start = text.IndexOf("\n" + from, StringComparison.Ordinal) + 1;
        var end = text.IndexOf("\n" + to, start, StringComparison.Ordinal) + 1;
        Assert.True(start > 0 && end > start);
        return text[..start] + text[end..];
    }

    private static int LineOf(string yaml, string line)
        => Array.IndexOf(yaml.Split('\n'), line) + 1;

    private static string Describe(ProjectDefinitionReadResult result)
        => string.Join("; ", result.Issues.Select(issue => $"{issue.Path} {issue.Code} {issue.Message}"));
}
