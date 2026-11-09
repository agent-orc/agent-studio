using AgentStudio.Management;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ObservedStructureProjectionTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly StructureResponse Declared = new(
        [new("machine:runner", "physical", "Runner host", "installation", null, null, null, "declared", "role fixture"),
         new("component:runner:gate", "container", "Gate", "machine:runner", "gate", null, null, "declared", "role fixture"),
         new("external:registry", "external", "Registry", null, null, null, null, "declared", "role fixture")],
        [new("machine:runner", "external:registry", "tunnel", "Route", "role fixture")]);

    private static RunnerCapabilitySnapshotDto Host(DateTime seen, string status = "active",
        IReadOnlyList<HostContainerSummaryDto>? containers = null) => new(
        "runner", "Runner", "runner", "instance", "1", 1, status, Now.AddHours(-1), seen,
        new("runner", "open", null, null, null, null), [], null,
        Release: new("release", "1.0"), Containers: containers);

    [Fact]
    public void EveryDifferenceHasAnExplicitStateAndMissingMetricsStayUnobserved()
    {
        var empty = ObservedStructureProjection.Apply(Declared, [], [], [], [], null, "not-required", Now);
        Assert.Equal("declared-only", empty.Nodes[0].Observation!.Difference);
        Assert.Equal("not observed", empty.Nodes[0].Observation!.Health);
        Assert.Equal("unreachable-by-design", empty.Nodes[2].Observation!.Difference);

        var matched = ObservedStructureProjection.Apply(Declared, [Host(Now)], [], [], [], null, "not-required", Now);
        Assert.Equal("matched", matched.Nodes[0].Observation!.Difference);
        Assert.Null(matched.Nodes[0].Observation!.CpuPercent);
        Assert.Null(matched.Nodes[0].Observation!.MemoryUsedBytes);

        var stale = ObservedStructureProjection.Apply(Declared, [Host(Now.AddSeconds(-91))], [], [], [], null, "not-required", Now);
        Assert.Equal("stale", stale.Nodes[0].Observation!.Difference);
        Assert.Equal("stale", stale.Nodes[0].Observation!.Health);

        var down = ObservedStructureProjection.Apply(Declared, [Host(Now, "offline")], [], [], [], null, "not-required", Now);
        Assert.Equal("down", down.Nodes[0].Observation!.Health);

        var undeclared = ObservedStructureProjection.Apply(Declared,
            [Host(Now, containers: [new("Extra", "sha256:abc", "running")])], [], [], [], null, "not-required", Now);
        var extra = undeclared.Nodes.Single(node => node.Id == "observed-container:runner:Extra");
        Assert.Equal("undeclared", extra.Observation!.Difference);
        Assert.Equal("sha256:abc", extra.Observation.Version);

        var drift = new HostReleaseDriftSnapshot(Now, new("2.0", null, Now), 1,
            [new("runner", "Runner", "runner", "coding", null, HostReleaseDriftStates.Behind,
                24, 24, true, "Older release", Now, false)]);
        var differs = ObservedStructureProjection.Apply(Declared, [Host(Now)], [], [], [], drift, "not-required", Now);
        Assert.Equal("differs", differs.Nodes[0].Observation!.Difference);
        Assert.True(differs.Nodes[0].Observation!.Behind);
        var roleNode = Declared with { Nodes = [Declared.Nodes[0], Declared.Nodes[1] with { UnitId = "runner" }] };
        var role = ObservedStructureProjection.Apply(roleNode, [Host(Now)], [], [], [], drift, "not-required", Now);
        Assert.Equal("differs", role.Nodes[1].Observation!.Difference);
        Assert.Equal("1.0", role.Nodes[1].Observation.Version);
    }

    [Fact]
    public void LinkHeartbeatChangesToStaleAndThenDown()
    {
        RunnerLinkResource Link(string state, DateTime heartbeat) => new("runner", "reverse-ssh", state,
            Now.AddHours(-1), heartbeat, null, null, 0, null, null, null, null, null, null, null, null);
        var up = ObservedStructureProjection.Apply(Declared, [Host(Now)], [Link("up", Now)], [], [], null, "not-required", Now);
        Assert.Equal("up", up.Links[0].Observation!.Health);
        var stale = ObservedStructureProjection.Apply(Declared, [Host(Now)], [Link("up", Now.AddSeconds(-91))], [], [], null, "not-required", Now);
        Assert.Equal("stale", stale.Links[0].Observation!.Health);
        var down = ObservedStructureProjection.Apply(Declared, [Host(Now)], [Link("down", Now)], [], [], null, "not-required", Now);
        Assert.Equal("down", down.Links[0].Observation!.Health);
    }

    [Fact]
    public void RunTargetHealthEngineAndGateFactsJoinTheirStableKeys()
    {
        var declaration = new EnvironmentDeclaration("production", "Production", "production", "runner",
            "role address", "container", "role destination", null, "none", [], 8080)
        { Artifacts = [new ProjectArtifactDefinition("api", "container", null, "Dockerfile")] };
        var observed = new RunTargetObservation("Project", "production", "running", "release-1", null,
            0, Now, []);
        var deployment = new StructureDeploymentFact("project-id",
            new(declaration, "release-2", "healthy", null, [], observed));
        var check = new EnvironmentHealthCheckView("http", "http", "role target", false,
            200, false, 60, 10, 3, 2, "healthy",
            new(new DateTimeOffset(Now), true, 20, 200, null), 0,
            null, null, null, new(null, null, null), null);
        var health = new StructureHealthFact("project-id",
            new("Project", "production", "Production", "production", "role address", "healthy",
                null, null, null, new(null, null, null), null, null, [check]));
        var graph = new StructureResponse([
            new("machine:gate", "physical", "Gate host", null, null, null, null, "declared", "role fixture"),
            new("environment:project-id:production", "environment", "Production", null, null,
                "production", "project-id", "declared", "role fixture"),
            new("instance:project-id:production:api", "container", "API", "machine:gate", "api",
                "production", "project-id", "declared", "role fixture"),
            new("component:gate:engine", "container", "Engine", "machine:gate", "engine", null,
                null, "declared", "role fixture", "engine")], []);

        var result = ObservedStructureProjection.Apply(graph, [], [], [deployment], [health], null,
            OrchestrationEngineReadiness.Ready, Now,
            [new PullGateHost("gate", "Gate host", 2, 4, new DateTimeOffset(Now), null) { Busy = 1 }]);
        Assert.Equal("matched", result.Nodes[0].Observation!.Difference);
        Assert.Equal(1, result.Nodes[0].Observation!.ActiveGates);
        Assert.Equal("matched", result.Nodes[1].Observation!.Difference);
        Assert.Equal("differs", result.Nodes[2].Observation!.Difference);
        Assert.Equal("release-1", result.Nodes[2].Observation!.Version);
        Assert.Equal("up", result.Nodes[3].Observation!.Health);

        var missingCheck = health with { View = health.View with
            { Checks = [check, check with { Id = "missing", LastSample = null }] } };
        var missing = ObservedStructureProjection.Apply(graph, [], [], [deployment], [missingCheck], null,
            OrchestrationEngineReadiness.Ready, Now);
        Assert.Equal("declared-only", missing.Nodes[1].Observation!.Difference);
        Assert.Equal("not observed", missing.Nodes[1].Observation!.Health);

        var oldSample = check.LastSample! with { At = new DateTimeOffset(Now.AddSeconds(-61)) };
        var expiredCheck = health with { View = health.View with
            { Checks = [check, check with { Id = "expired", LastSample = oldSample }] } };
        var expired = ObservedStructureProjection.Apply(graph, [], [], [deployment], [expiredCheck], null,
            OrchestrationEngineReadiness.Ready, Now);
        Assert.Equal("stale", expired.Nodes[1].Observation!.Difference);
    }
}
