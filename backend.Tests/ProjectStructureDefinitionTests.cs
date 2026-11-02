using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The project.yml schema 2 <c>structure</c> section (AGT-3349 S5): regions,
/// placements with runner deploy target kinds, managed services, validation
/// paths, and the JSON schema staying in step with the vocabulary.
/// </summary>
public sealed class ProjectStructureDefinitionTests
{
    internal const string Shop = """
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
          id: shop
        artifacts:
          - name: web
            type: container
            dockerfile: Dockerfile
          - name: cart
            type: container
            dockerfile: Dockerfile
        deploy:
          - step: container
            artifact: web
            target: run-host
            composeFile: /srv/apps/web/compose.yml
            service: web
            healthUrl: https://web.example/healthz
            healthTimeoutSeconds: 60
          - step: container
            artifact: cart
            target: run-host
            composeFile: /srv/apps/cart/compose.yml
            service: cart
            healthUrl: https://cart.example/healthz
            healthTimeoutSeconds: 60
        structure:
          regions:
            - name: eu
              label: Region EU
            - name: us
              label: Region US
          placements:
            - name: prod-eu
              kind: kubernetes
              region: eu
              environments: [production]
            - name: storefront-eu
              kind: namespace
              parent: prod-eu
              artifacts: [web, cart]
              replicas: 4
            - name: prod-us
              kind: kubernetes
              region: us
              environments: [production]
            - name: storefront-us
              kind: namespace
              parent: prod-us
              artifacts: [web]
            - name: stage-host
              kind: ssh-docker
              environments: [staging]
              artifacts: [web, cart]
            - name: preview
              kind: cloud-run
              region: eu
              environments: [staging]
          managedServices:
            - name: postgresql
              kind: database
              region: eu
              environments: [production]
              healthCheck: postgres-tls
            - name: message-queue
              kind: queue
              region: eu
              environments: [production]
        """;

    [Fact]
    public void Parses_regions_placements_and_managed_services()
    {
        var result = ProjectDefinitionReader.Parse(Shop);

        Assert.True(result.IsValid, Describe(result));
        var structure = result.Definition!.Deployment!.Structure;
        Assert.Equal(["eu", "us"], structure.Regions.Select(region => region.Name));
        Assert.Equal("Region EU", structure.Regions[0].Label);
        var namespaceEu = structure.Placements.Single(placement => placement.Name == "storefront-eu");
        Assert.Equal(("namespace", "prod-eu", 4), (namespaceEu.Kind, namespaceEu.Parent, namespaceEu.Replicas));
        Assert.Equal(["web", "cart"], namespaceEu.Artifacts);
        Assert.Equal(["production"], structure.Placements[0].Environments);
        Assert.Equal("postgres-tls", structure.ManagedServices[0].HealthCheck);
        Assert.Null(structure.ManagedServices[1].HealthCheck);
    }

    [Fact]
    public void Structure_alone_is_a_schema_two_deployment_and_refused_by_schema_one()
    {
        var head = Shop[..Shop.IndexOf("artifacts:", StringComparison.Ordinal)];
        var only = head + "structure:\n  regions:\n    - name: eu\n";
        var schemaOne = only.Replace("schemaVersion: 2", "schemaVersion: 1", StringComparison.Ordinal)
            .Replace("project:\n  id: shop\n", string.Empty, StringComparison.Ordinal);

        Assert.True(ProjectDefinitionReader.Parse(only).IsValid);
        Assert.Contains(ProjectDefinitionReader.Parse(schemaOne).Issues,
            issue => issue.Code == "v1-does-not-support-deployment");
    }

    [Theory]
    [InlineData("      kind: kubernetes\n      region: eu", "      kind: nomad\n      region: eu", "structure.placements[0].kind", "structure-placement-kind-invalid")]
    [InlineData("      parent: prod-eu\n      artifacts: [web, cart]", "      parent: missing\n      artifacts: [web, cart]", "structure.placements[1].parent", "structure-parent-unknown")]
    [InlineData("      parent: prod-eu\n      artifacts: [web, cart]", "      parent: stage-host\n      artifacts: [web, cart]", "structure.placements[1].parent", "structure-namespace-parent-invalid")]
    [InlineData("      region: us\n      environments: [production]", "      region: ap\n      environments: [production]", "structure.placements[2].region", "structure-region-unknown")]
    [InlineData("      environments: [staging]\n      artifacts: [web, cart]", "      environments: [development]\n      artifacts: [web, cart]", "structure.placements[4].environments", "structure-environment-unknown")]
    [InlineData("      artifacts: [web]", "      artifacts: [search]", "structure.placements[3].artifacts", "structure-artifact-unknown")]
    [InlineData("      replicas: 4", "      replicas: 0", "structure.placements[1].replicas", "structure-replicas-invalid")]
    [InlineData("      replicas: 4", "      replicas: four", "structure.placements[1].replicas", "structure-replicas-invalid")]
    [InlineData("      parent: prod-eu\n      artifacts: [web, cart]", "      parent: prod-eu\n      region: us\n      artifacts: [web, cart]", "structure.placements[1].region", "structure-region-conflict")]
    [InlineData("    - name: prod-us\n", "    - name: prod-eu\n", "structure.placements[2].name", "structure-name-invalid")]
    [InlineData("      kind: queue", "      kind: mainframe", "structure.managedServices[1].kind", "structure-managed-kind-invalid")]
    [InlineData("      healthCheck: postgres-tls", "      healthCheck: Postgres TLS", "structure.managedServices[0].healthCheck", "structure-health-check-invalid")]
    [InlineData("      label: Region US", "      label: Region US\n      zone: a", "structure.regions[1].zone", "unknown-field")]
    [InlineData("  managedServices:", "  clusters:\n    - name: x\n  managedServices:", "structure.clusters", "unknown-field")]
    public void Refuses_inconsistent_structure_with_a_located_path(string find, string replace, string path, string code)
    {
        Assert.Contains(find, Shop, StringComparison.Ordinal);
        var result = ProjectDefinitionReader.Parse(ReplaceFirst(Shop, find, replace));

        var issue = Assert.Single(result.Issues, issue => issue.Code == code);
        Assert.Equal(path, issue.Path);
        Assert.NotNull(issue.Line);
    }

    [Fact]
    public void Refuses_a_placement_parent_cycle()
    {
        var yaml = Shop.Replace("    - name: prod-eu\n      kind: kubernetes\n",
            "    - name: prod-eu\n      kind: kubernetes\n      parent: storefront-eu\n", StringComparison.Ordinal);

        Assert.Contains(ProjectDefinitionReader.Parse(yaml).Issues, issue => issue.Code == "structure-parent-cycle");
    }

    [Fact]
    public void Json_schema_declares_the_structure_vocabulary()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "docs", "app", "schemas", "project-execution.schema.json")));
        var structure = schema.RootElement.GetProperty("properties").GetProperty("structure").GetProperty("properties");
        static IEnumerable<string?> Names(JsonElement element) => element.EnumerateObject().Select(item => (string?)item.Name);
        static IEnumerable<string?> Enum(JsonElement element) => element.GetProperty("enum").EnumerateArray().Select(item => item.GetString());
        JsonElement Item(string section) => structure.GetProperty(section).GetProperty("items").GetProperty("properties");

        Assert.Equal(ProjectStructureVocabulary.Sections, Names(structure));
        Assert.Equal(ProjectStructureVocabulary.RegionKeys, Names(Item("regions")));
        Assert.Equal(ProjectStructureVocabulary.PlacementKeys, Names(Item("placements")));
        Assert.Equal(ProjectStructureVocabulary.ManagedServiceKeys, Names(Item("managedServices")));
        Assert.Equal(ProjectStructureVocabulary.PlacementKinds, Enum(Item("placements").GetProperty("kind")));
        Assert.Equal(ProjectStructureVocabulary.ManagedServiceKinds, Enum(Item("managedServices").GetProperty("kind")));
        Assert.Equal(ProjectStructureVocabulary.MaxReplicas,
            Item("placements").GetProperty("replicas").GetProperty("maximum").GetInt32());
    }

    [Theory]
    [InlineData("ssh-docker", "server", "SSH Docker host")]
    [InlineData("kubernetes", "cluster", "Kubernetes")]
    [InlineData("cloud-run", "managed-runtime", "Cloud Run")]
    [InlineData("namespace", "namespace", null)]
    [InlineData("vm", "vm", null)]
    public void Deploy_target_kinds_map_to_placement_kinds(string kind, string placement, string? detail)
    {
        Assert.Equal(placement, ProjectStructureVocabulary.PlacementKindFor(kind));
        Assert.Equal(detail, ProjectStructureVocabulary.PlacementDetail(kind));
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        var index = text.IndexOf(find, StringComparison.Ordinal);
        return text[..index] + replace + text[(index + find.Length)..];
    }

    private static string Describe(ProjectDefinitionReadResult result)
        => string.Join("; ", result.Issues.Select(issue => $"{issue.Path}: {issue.Code}"));
}
