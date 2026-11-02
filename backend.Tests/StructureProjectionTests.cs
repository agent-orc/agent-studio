using AgentStudio.Management;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StructureProjectionTests
{
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
}
