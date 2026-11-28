using AgentStudio.Deploy;
using AgentStudio.Management;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ObservedInstallationTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 17, 47, 0, DateTimeKind.Utc);

    [Fact]
    public void Machine_inventory_keeps_reported_commits_and_unknowns_per_component()
    {
        var source = new StudioSelfObservation("studio", "Studio", null, null, null, "1.0", Now);
        var host = new RunnerCapabilitySnapshotDto("worker", "Worker", "studio", "instance", "v1", 1,
            "active", Now.AddHours(-2), Now.AddHours(-1), new("studio", "open", null, null, null, null), [], null,
            Release: new("runner", "1.0", "bbbbbbb"), Containers: [new("studio-web", null, "running")]);
        var inputs = new StructureObservedInputs([host], [], null, "not-required",
            [new("vm", "Gate", 2, 4, Now, null)], new(source));

        var components = ObservedInstallation.Components(inputs, "1.0", "aaaaaaa", Now);
        Assert.Equal(4, components.Count);
        Assert.Equal(2, components.Select(row => row.Machine).Distinct().Count());
        Assert.Equal("aaaaaaa", components[0].Commit);
        Assert.Contains(components, row => row.Commit == "bbbbbbb" && row.Health == "Stale heartbeat");
        Assert.Contains(components, row => row.Name == "Web frontend" && row.Commit is null);
        Assert.Contains(components, row => row.Machine == "vm" && row.Health == "Reporting" && row.Version is null);
    }

    [Fact]
    public void History_retains_a_failed_attempt_and_does_not_time_an_observation_as_a_deploy()
    {
        var live = new string('a', 40);
        var target = new string('b', 40);
        var revisions = new[] { new StudioReleaseEntry(live, Now.AddDays(-1), true, true, [], "observed:1") };
        var view = new StudioReleasesView("studio", live, Now, [], revisions, new Dictionary<string, StudioCardLiveState>());
        var run = new InstanceDeployRun { Id = "attempt", TargetCommit = target, Version = "1.1",
            StartedAtUtc = Now.AddMinutes(-3), LastReportAtUtc = Now, FinishedAtUtc = Now, Outcome = "failed",
            Steps = [new() { Name = "build", Kind = "build", State = "failed", ReportedAtUtc = Now }] };
        var rows = ObservedInstallation.History(view, new() { Runs = [run] }, Now);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("failed", live, target), (rows[0].Outcome, rows[0].FromCommit, rows[0].ToCommit));
        Assert.Equal("build", Assert.Single(rows[0].Steps).Name);
        Assert.Equal("observed running", rows[1].Outcome);
        Assert.Null(rows[1].StartedAtUtc);
        Assert.Null(rows[1].CardCount);
    }

    [Fact]
    public void Impact_flags_describe_the_attributed_paths()
    {
        Assert.Equal(["Changes start-up", "Changes stored data", "Changes the runner contract"],
            WaitingDelivery.PathFlags(["backend/Host/Program.cs", "backend/store/Migrations/Init.cs",
                "contracts/TaskServer.Contracts/ReleaseIdentityContracts.cs", "frontend/src/view.html"]));
        Assert.Equal(["Interface only"], WaitingDelivery.PathFlags(["frontend/src/view.html"]));
        Assert.Empty(WaitingDelivery.PathFlags(["docs/README.md"]));
    }
}
