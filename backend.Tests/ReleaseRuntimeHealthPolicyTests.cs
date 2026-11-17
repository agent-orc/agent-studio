using AgentStudio.Deploy;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure policy; every time is an input.
public sealed class ReleaseRuntimeHealthPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Release R1 = Release("r1", 1);
    private static readonly Release R2 = Release("r2", 2);
    private static readonly Release R3 = Release("r3", 3);
    private static readonly IReadOnlyDictionary<string, Release> Releases =
        new[] { R1, R2, R3 }.ToDictionary(item => item.Id, StringComparer.Ordinal);

    // R1 runs from hour 1, R2 from hour 2, R3 from hour 3 (each run finishes five minutes after its hour).
    private static readonly Deployment[] Runs =
    [
        Run("d1", R1, 1), Run("d2", R2, 2), Run("d3", R3, 3),
    ];

    [Fact]
    public void HealthCheckIncident_MarksTheReleaseThatRanWhenItStarted_AndStaysAfterTheNextRelease()
    {
        var incident = new RuntimeIncidentObservation(ReleaseRuntimeHealthPolicy.HealthCheckSource,
            T0.AddHours(2).AddMinutes(30), "health check http: HTTP 502");
        var (records, changed) = ReleaseRuntimeHealthPolicy.Observe([], Runs, Releases,
            [new("production", [incident], null)]);

        Assert.True(changed);
        var record = Assert.Single(records);
        Assert.Equal((R2.Id, "production", ReleaseRuntimeHealthPolicy.Incident), (record.ReleaseId, record.EnvironmentId, record.Health));
        Assert.Equal("health check http: HTTP 502", record.Incident?.Detail);

        // The same observation again changes nothing; a passing probe never clears an incident.
        var (again, changedAgain) = ReleaseRuntimeHealthPolicy.Observe(records, Runs, Releases,
            [new("production", [incident], T0.AddHours(3).AddMinutes(30))]);
        Assert.Equal(ReleaseRuntimeHealthPolicy.Incident, again.Single(item => item.ReleaseId == R2.Id).Health);
        Assert.True(changedAgain);
        Assert.Equal(ReleaseRuntimeHealthPolicy.Healthy, again.Single(item => item.ReleaseId == R3.Id).Health);
    }

    [Fact]
    public void CrashLoop_MarksTheReleaseOfTheNamedArtifact_NotTheOneRunningNow()
    {
        var crash = new RuntimeIncidentObservation(ReleaseRuntimeHealthPolicy.CrashLoopSource,
            T0.AddHours(4), "the host reported crash-loop", R2.ArtifactDigests[0]);
        var (records, _) = ReleaseRuntimeHealthPolicy.Observe([], Runs, Releases, [new("production", [crash], null)]);

        Assert.Equal(R2.Id, Assert.Single(records).ReleaseId);
    }

    [Fact]
    public void HealthyProbe_MarksOnlyTheCurrentRelease_AndAnIncidentBeforeAnyReleaseIsIgnored()
    {
        var early = new RuntimeIncidentObservation(ReleaseRuntimeHealthPolicy.HealthCheckSource, T0, "before releases");
        var (records, _) = ReleaseRuntimeHealthPolicy.Observe([], Runs, Releases,
            [new("production", [early], T0.AddHours(3).AddMinutes(10))]);

        var record = Assert.Single(records);
        Assert.Equal((R3.Id, ReleaseRuntimeHealthPolicy.Healthy), (record.ReleaseId, record.Health));
    }

    [Fact]
    public void Target_SkipsAnOlderReleaseWithAnIncident_AndNamesIt()
    {
        var health = new[]
        {
            Incident(R2),
            new ReleaseRuntimeHealthRecord(R1.Id, "production", ReleaseRuntimeHealthPolicy.Healthy, T0.AddHours(1)),
        };
        var target = ReleaseRuntimeHealthPolicy.Target(Runs, Releases, health)!;

        Assert.Equal(R1.Id, target.Release.Id);
        Assert.Equal(ReleaseRuntimeHealthPolicy.Healthy, target.RuntimeHealth);
        Assert.Equal([R2.Version], target.SkippedVersions);
        Assert.Equal(T0.AddHours(1).AddMinutes(5), target.DeployedAtUtc);
    }

    [Fact]
    public void Target_WithoutAHealthyOlderRelease_IsThePreviousReleaseMarkedAsIncident()
    {
        var target = ReleaseRuntimeHealthPolicy.Target(Runs, Releases, [Incident(R1), Incident(R2)])!;

        Assert.Equal(R2.Id, target.Release.Id);
        Assert.Equal(ReleaseRuntimeHealthPolicy.Incident, target.RuntimeHealth);
        Assert.Equal("crash", target.Incident?.Detail);
        Assert.Empty(target.SkippedVersions);
    }

    [Fact]
    public void Target_WithoutRecords_IsThePreviousReleaseWithUnknownHealth_AndNullWithoutOne()
    {
        var target = ReleaseRuntimeHealthPolicy.Target(Runs, Releases, [])!;
        Assert.Equal((R2.Id, ReleaseRuntimeHealthPolicy.Unknown), (target.Release.Id, target.RuntimeHealth));
        Assert.Equal(EnvironmentStatusProjection.PreviousRelease(Runs, Releases)?.Id, target.Release.Id);
        Assert.Null(ReleaseRuntimeHealthPolicy.Target([Run("d1", R1, 1)], Releases, []));
    }

    private static ReleaseRuntimeHealthRecord Incident(Release release)
        => new(release.Id, "production", ReleaseRuntimeHealthPolicy.Incident, T0,
            new(ReleaseRuntimeHealthPolicy.CrashLoopSource, T0, "crash"));

    private static Release Release(string id, int number)
        => new(id, "demo", $"2026.11.16.{number}", new string((char)('0' + number), 40),
            ["registry.example/app@sha256:" + new string((char)('a' + number), 64)], "process", "variables",
            [], T0.AddHours(number), ReleaseSnapshot.DefaultChannel);

    private static Deployment Run(string id, Release release, int hour)
        => new(id, release.Id, "production", DeploymentStates.Succeeded, T0.AddHours(hour),
            T0.AddHours(hour).AddMinutes(5), null, [], DeploymentTriggers.Deploy, "release-gate", T0.AddHours(hour));
}
