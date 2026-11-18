using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F16: an environment on a LAN or VPN opts into private-network health checks
/// from project.yml schema 2, and a refused target names its fix.
/// </summary>
public sealed class PrivateNetworkHealthCheckTests
{
    private static string Yaml(string stagingExtra = "", string productionExtra = "") => $$"""
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
        artifacts:
          - name: site
            type: static
            path: dist
        environments:
          - name: staging
            protected: false
        {{stagingExtra}}
          - name: production
            protected: true
        {{productionExtra}}
        deploy:
          - step: static-site
            artifact: site
            target: lan-host
            directory: /srv/www/staging
            healthUrl: http://172.17.0.1:8081/
            healthTimeoutSeconds: 60
            environments: [staging]
          - step: static-site
            artifact: site
            target: lan-host
            directory: /srv/www/production
            healthUrl: http://172.17.0.1:8080/
            healthTimeoutSeconds: 60
            environments: [production]
        """;

    private const string OptIn = "    healthChecksAllowPrivateNetwork: true";

    [Fact]
    public void Schema_two_environments_parse_the_private_network_opt_in()
    {
        var result = ProjectDefinitionReader.Parse(Yaml(stagingExtra: OptIn));

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(item => $"{item.Path}: {item.Code}")));
        Assert.Equal([("staging", true), ("production", false)], result.Definition!.Deployment!.Environments
            .Select(item => (item.Name, item.HealthChecksAllowPrivateNetwork)));
    }

    [Fact]
    public void A_non_boolean_opt_in_is_a_field_issue()
    {
        var result = ProjectDefinitionReader.Parse(Yaml(stagingExtra: "    healthChecksAllowPrivateNetwork: yes"));

        Assert.Contains(result.Issues, issue => issue is
        {
            Path: "environments[0].healthChecksAllowPrivateNetwork", Code: "environment-health-private-network-invalid",
        });
    }

    [Fact]
    public void Json_schema_declares_every_environment_key()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "docs", "app", "schemas", "project-execution.schema.json")));
        var items = schema.RootElement.GetProperty("properties").GetProperty("environments").GetProperty("oneOf")[1]
            .GetProperty("prefixItems").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(ProjectDeploymentVocabulary.EnvironmentKeys,
            item.GetProperty("properties").EnumerateObject().Select(property => property.Name)));
    }

    [Fact]
    public void Schema_two_declarations_carry_the_opt_in_into_the_effective_checks()
    {
        var effective = ProjectEnvironmentService.ApplyDefinition([], Yaml(productionExtra: OptIn));

        Assert.All(effective, file => Assert.Null(file.Error));
        var staging = effective.Single(item => item.EnvironmentId == "stage").Declaration!;
        var production = effective.Single(item => item.EnvironmentId == "production").Declaration!;
        Assert.False(staging.HealthChecksAllowPrivateNetwork);
        Assert.True(production.HealthChecksAllowPrivateNetwork);
        Assert.All(HealthCheckDeclarations.Effective(production), check => Assert.True(check.AllowPrivateNetwork));
        Assert.All(HealthCheckDeclarations.Effective(staging), check => Assert.False(check.AllowPrivateNetwork));
    }

    [Fact]
    public void A_migrated_json_declaration_keeps_its_opt_in_and_accepts_the_project_yml_one()
    {
        EnvironmentDeclaration Legacy(string id, string kind, string directory, string url, bool allow) => new(
            id, id, kind, "lan-host", url, "static", directory, url, "person", [], 8080,
            HealthChecksAllowPrivateNetwork: allow);
        var files = new[]
        {
            new EnvironmentDeclarationFile("stage", Legacy("stage", "stage", "/srv/www/staging", "http://172.17.0.1:8081/", true), null),
            new EnvironmentDeclarationFile("production",
                Legacy("production", "production", "/srv/www/production", "http://172.17.0.1:8080/", false), null),
        };

        var effective = ProjectEnvironmentService.ApplyDefinition(files, Yaml(productionExtra: OptIn));

        Assert.All(effective, file => Assert.Null(file.Error));
        Assert.All(effective, file => Assert.True(file.Declaration!.HealthChecksAllowPrivateNetwork));
    }

    [Fact]
    public void A_refused_private_target_names_the_project_yml_opt_in()
    {
        var production = ProjectEnvironmentService.ApplyDefinition([], Yaml())
            .Single(item => item.EnvironmentId == "production").Declaration!;
        var check = Assert.Single(HealthCheckDeclarations.Effective(production));

        var problem = HealthAddressProblems.Describe(production, check);

        Assert.Contains("http://172.17.0.1:8080/", problem);
        Assert.Contains("private network address", problem);
        Assert.Contains("healthChecksAllowPrivateNetwork: true on production under environments in .agent-studio/project.yml",
            problem);
    }

    [Fact]
    public void A_refused_target_the_opt_in_cannot_admit_names_no_opt_in()
    {
        var declaration = new EnvironmentDeclaration("stage", "Staging", "stage", "host", "http://127.0.0.1:8080/",
            "static", "/srv/www", "http://127.0.0.1:8080/health", "person", [], 8080);
        var loopback = Assert.Single(HealthCheckDeclarations.Effective(declaration));
        var optedIn = Assert.Single(HealthCheckDeclarations.Effective(declaration with
        {
            HealthUrl = "http://internal.example/health", HealthChecksAllowPrivateNetwork = true,
        }));
        var hostname = Assert.Single(HealthCheckDeclarations.Effective(declaration with { HealthUrl = "http://internal.example/health" }));

        Assert.DoesNotContain("healthChecksAllowPrivateNetwork", HealthAddressProblems.Describe(declaration, loopback));
        Assert.Contains("never probes", HealthAddressProblems.Describe(declaration, loopback));
        Assert.DoesNotContain("healthChecksAllowPrivateNetwork", HealthAddressProblems.Describe(declaration, optedIn));
        // A host name resolves at probe time, so both causes are named; a JSON declaration points at its file.
        var both = HealthAddressProblems.Describe(declaration, hostname);
        Assert.Contains("\"healthChecksAllowPrivateNetwork\": true in docs/deployments/environments/stage.json", both);
        Assert.Contains("never probed", both);
    }
}
