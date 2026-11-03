using AgentStudio.Management;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StructureProjectionTests
{
    [Fact]
    public void WorkspaceSystemsAndProjectPartialPictureKeepSharedPlacementStable()
    {
        var projects = new[] {
            new ProjectRecord { Id = "PROJ-001", DisplayName = "AGT", WorkspaceId = "ws-default" },
            new ProjectRecord { Id = "PROJ-002", DisplayName = "Website", WorkspaceId = "ws-default" },
            new ProjectRecord { Id = "PROJ-003", DisplayName = "Library", WorkspaceId = "ws-default" },
        };
        var manifest = new InstallationConnectivityManifest(1, "installation", 1, "one-box",
            "standalone-task-server", "server origin", "browser origin", null, "none", [], [], [],
            [new("web", "Web placement", [
                new("website", "Website process", "web", "process", "PROJ-002", "production"),
                new("agent", "Run-target agent", "agent", "process", "PROJ-001", "production")], Kind: "vm"),
             new("runner", "Runner placement", [
                new("engine", "Engine", "engine", "container", "PROJ-001", "production"),
                new("channel", "npm channel", "package", "registry", "PROJ-003", "production")])],
            [new("shared", "Shared external")],
            [new("component:web:website", "component:runner:channel", "artifact", "Package"),
             new("component:web:website", "external:shared", "call", "Service"),
             new("component:runner:engine", "external:shared", "call", "Service")]);
        var systems = new[] { new WorkspaceSystem { Id = "product", Name = "Product", Units = [
            new("PROJ-001", "agent"), new("PROJ-002", "website") ] } };
        var workspace = StructureProjection.Build(manifest, projects,
            _ => Array.Empty<EnvironmentDeclarationFile>(), null, null, systems: systems);
        Assert.Contains(workspace.Nodes, node => node.Id == "system:product:production");
        Assert.Equal("system:product:production", workspace.Nodes.Single(node =>
            node.Id == "component:web:website").SystemBoundaryId);
        Assert.Contains(workspace.Nodes, node => node.Id == "component:runner:channel");
        var partial = StructureProjection.Build(manifest, projects,
            _ => Array.Empty<EnvironmentDeclarationFile>(), "PROJ-002", null, systems: systems);
        Assert.Equal(["component:runner:channel", "component:web:agent"],
            partial.Nodes.Where(node => node.OtherProject).Select(node => node.Id).Order().ToArray());
        Assert.Contains(partial.Nodes, node => node.Id == "external:shared");
        Assert.DoesNotContain(partial.Nodes, node => node.Id == "component:runner:engine");
        Assert.Equal(workspace.Nodes.Single(node => node.Id == "component:web:agent").ParentId,
            partial.Nodes.Single(node => node.Id == "component:web:agent").ParentId);
        var moved = manifest with { Machines = manifest.Machines!.Select(machine => machine.Id == "web"
            ? machine with { Components = machine.Components.Select(component => component.Id == "agent"
                ? component with { ProjectId = "PROJ-003" } : component).ToArray() } : machine).ToArray() };
        var afterMove = StructureProjection.Build(moved, projects,
            _ => Array.Empty<EnvironmentDeclarationFile>(), null, null, systems: systems);
        Assert.Equal(workspace.Nodes.Single(node => node.Id == "component:web:agent").ParentId,
            afterMove.Nodes.Single(node => node.Id == "component:web:agent").ParentId);
        Assert.Equal(workspace.Links, afterMove.Links);
        Assert.Equal("PROJ-003", afterMove.Nodes.Single(node => node.Id == "component:web:agent").Project);
    }
    [Fact]
    public void NestedManifestProjectsAsDeclaredPlacementAndTypedLinks()
    {
        var manifest = new InstallationConnectivityManifest(1, "installation", 1, "one-box",
            "standalone-task-server", "server origin", "browser origin",
            null, "none", [], [], [],
            [new("studio", "Studio machine", [new("backend", "Studio backend", "control", "process")],
                Kind: "physical", Location: "Operator site"),
             new("lab", "Home-lab physical machine", [], Kind: "physical", Location: "Home lab"),
             new("vm", "Home-lab VM", [new("gate", "Build and test gate", "verification", "container")],
                 ParentId: "lab", Kind: "vm", Location: "Home lab")],
            [new("github", "GitHub")],
            [new("component:studio:backend", "external:github", "call", "Git")]);

        var result = StructureProjection.Build(manifest, [],
            _ => Array.Empty<EnvironmentDeclarationFile>(), null, null);

        Assert.Equal("machine:lab", result.Nodes.Single(n => n.Id == "machine:vm").ParentId);
        Assert.Equal("machine:vm", result.Nodes.Single(n => n.Id == "component:vm:gate").ParentId);
        Assert.All(result.Nodes, n => Assert.Equal("declared", n.Provenance));
        Assert.Equal("call", Assert.Single(result.Links).Kind);
    }

    [Fact]
    public void OldManifestWithoutPlacementFieldsStillParses()
    {
        const string json = """
            {"schemaVersion":1,"installationId":"installation","revision":1,"mode":"one-box",
             "package":"standalone-task-server","serverOrigin":"server origin",
             "browserOrigin":"browser origin","enabledListeners":[],"runners":[],
             "machines":[{"id":"studio","name":"Studio machine","components":[]}]}
            """;
        var manifest = InstallationConnectivityManifest.Parse(json);
        Assert.Null(Assert.Single(manifest.Machines!).ParentId);
    }

    [Fact]
    public void JoinsSchemaTwoArtifactsAndEnvironmentDeclarationsWithoutObservedState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"structure-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".agent-studio"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".agent-studio", "project.yml"), """
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
                  id: studio
                artifacts:
                  - name: api
                    type: container
                    dockerfile: Dockerfile
                """);
            var project = new ProjectRecord
            {
                Id = "PROJ-001", DisplayName = "Studio project", RepositoryPath = root
            };
            var declaration = new EnvironmentDeclaration("stage", "Stage", "stage", "studio",
                "role address", "container", "role destination", null, "none", [], 8080)
            {
                Artifacts = [new ProjectArtifactDefinition("api", "container", null, "Dockerfile")]
            };
            var manifest = new InstallationConnectivityManifest(1, "installation", 1, "one-box",
                "standalone-task-server", "server origin", "browser origin", null, "none", [], [], [],
                [new("studio", "Studio machine", [], Kind: "physical")]);

            var result = StructureProjection.Build(manifest, [project],
                _ => [new EnvironmentDeclarationFile("stage", declaration, null)],
                "PROJ-001", "stage");

            Assert.Contains(result.Nodes, n => n.Id == "unit:PROJ-001:api" && n.UnitId == "api");
            Assert.Contains(result.Nodes, n => n.Id == "environment:PROJ-001:stage");
            Assert.Contains(result.Nodes, n => n.Id == "instance:PROJ-001:stage:api"
                && n.ParentId == "machine:studio" && n.EnvironmentId == "stage");
            Assert.All(result.Nodes, n => Assert.Equal("declared", n.Provenance));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StructureSectionProjectsRegionsTargetKindsAndManagedServiceObservation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"structure-section-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".agent-studio"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".agent-studio", "project.yml"), ProjectStructureDefinitionTests.Shop);
            var project = new ProjectRecord { Id = "PROJ-004", DisplayName = "Shop", RepositoryPath = root };
            var probed = new List<(string Environment, string Check)>();
            StructureProbe? Probe(ProjectRecord owner, string environment, string check)
            {
                probed.Add((environment, check));
                return owner.Id == "PROJ-004" && environment == "production" && check == "postgres-tls"
                    ? new StructureProbe("tls", "up") : null;
            }

            var result = StructureProjection.Build(null, [project],
                _ => Array.Empty<EnvironmentDeclarationFile>(), null, null, probes: Probe);
            StructureNode Node(string id) => Assert.Single(result.Nodes, node => node.Id == id);

            Assert.Equal(("environment", "installation"), (Node("environment:PROJ-004:production").Kind,
                Node("environment:PROJ-004:production").ParentId));
            Assert.Equal(("region", "Region EU", "environment:PROJ-004:production"),
                (Node("region:PROJ-004:production:eu").Kind, Node("region:PROJ-004:production:eu").Label,
                 Node("region:PROJ-004:production:eu").ParentId));
            var cluster = Node("placement:PROJ-004:production:prod-eu");
            Assert.Equal(("cluster", "Kubernetes", "region:PROJ-004:production:eu"), (cluster.Kind, cluster.Detail, cluster.ParentId));
            Assert.Equal(("namespace", cluster.Id), (Node("placement:PROJ-004:production:storefront-eu").Kind,
                Node("placement:PROJ-004:production:storefront-eu").ParentId));
            var web = Node("instance:PROJ-004:production:storefront-eu:web");
            Assert.Equal(("container", 4, "storefront-eu", "web", "placement:PROJ-004:production:storefront-eu"),
                (web.Kind, web.Replicas, web.Role, web.UnitId, web.ParentId));
            Assert.Equal("web", Node("instance:PROJ-004:production:storefront-us:web").UnitId);
            Assert.Equal(("server", "SSH Docker host", "environment:PROJ-004:staging"),
                (Node("placement:PROJ-004:staging:stage-host").Kind, Node("placement:PROJ-004:staging:stage-host").Detail,
                 Node("placement:PROJ-004:staging:stage-host").ParentId));
            Assert.Equal(("managed-runtime", "Cloud Run"), (Node("placement:PROJ-004:staging:preview").Kind,
                Node("placement:PROJ-004:staging:preview").Detail));
            var database = Node("managed:PROJ-004:production:postgresql");
            Assert.Equal(("managed", "database", "probed", "tls", "up", "region:PROJ-004:production:eu"),
                (database.Kind, database.Detail, database.ProbeObservation, database.Probe, database.Status, database.ParentId));
            var queue = Node("managed:PROJ-004:production:message-queue");
            Assert.Equal(("not-observed", null, null), (queue.ProbeObservation, queue.Probe, queue.Status));
            Assert.Equal([("production", "postgres-tls")], probed);
            Assert.DoesNotContain(result.Nodes, node => node.Id.StartsWith("placement:PROJ-004:staging:prod-", StringComparison.Ordinal));
            Assert.All(result.Nodes, node => Assert.Equal("declared", node.Provenance));

            var staging = StructureProjection.Build(null, [project],
                _ => Array.Empty<EnvironmentDeclarationFile>(), null, "staging", probes: Probe);
            Assert.DoesNotContain(staging.Nodes, node => node.EnvironmentId == "production");
            Assert.Contains(staging.Nodes, node => node.Id == "instance:PROJ-004:staging:stage-host:cart");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PlacedArtifactReplacesTheEnvironmentDeclarationInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"structure-placed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".agent-studio"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".agent-studio", "project.yml"), ProjectStructureDefinitionTests.Shop);
            var project = new ProjectRecord { Id = "PROJ-004", DisplayName = "Shop", RepositoryPath = root };
            var declaration = new EnvironmentDeclaration("staging", "Staging", "staging", "run-host",
                "role address", "container", "role destination", null, "none", [], 8080)
            {
                Artifacts = [new ProjectArtifactDefinition("web", "container", null, "Dockerfile")]
            };

            var result = StructureProjection.Build(null, [project],
                _ => [new EnvironmentDeclarationFile("staging", declaration, null)], null, null);

            Assert.DoesNotContain(result.Nodes, node => node.Id == "instance:PROJ-004:staging:web");
            Assert.Single(result.Nodes, node => node.Id == "environment:PROJ-004:staging");
            Assert.Equal("Staging", result.Nodes.Single(node => node.Id == "environment:PROJ-004:staging").Label);
            Assert.Contains(result.Nodes, node => node.Id == "instance:PROJ-004:staging:stage-host:web");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
