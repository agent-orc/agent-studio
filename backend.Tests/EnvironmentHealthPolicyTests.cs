using System.Text.Json;
using AgentStudio.Projects;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure policy inputs; every "now" is an explicit argument.
public sealed class EnvironmentHealthPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    // failures, successes, open, success, failureThreshold, recoveryThreshold -> action, failures, successes, open
    [InlineData(0, 0, false, true, 3, 2, HealthIncidentAction.None, 0, 1, false)]
    [InlineData(1, 0, false, false, 3, 2, HealthIncidentAction.None, 2, 0, false)]
    [InlineData(2, 0, false, false, 3, 2, HealthIncidentAction.Open, 3, 0, true)]
    [InlineData(0, 0, false, false, 1, 2, HealthIncidentAction.Open, 1, 0, true)]
    [InlineData(3, 0, true, false, 3, 2, HealthIncidentAction.Continue, 4, 0, true)]
    [InlineData(5, 0, true, true, 3, 2, HealthIncidentAction.None, 0, 1, true)]
    [InlineData(0, 1, true, false, 3, 2, HealthIncidentAction.Continue, 1, 0, true)]
    [InlineData(0, 1, true, true, 3, 2, HealthIncidentAction.Close, 0, 2, false)]
    [InlineData(4, 0, true, true, 3, 1, HealthIncidentAction.Close, 0, 1, false)]
    [InlineData(2, 0, false, true, 3, 2, HealthIncidentAction.None, 0, 1, false)]
    public void IncidentPolicy_Matrix(int failures, int successes, bool open, bool success, int failureThreshold,
        int recoveryThreshold, HealthIncidentAction action, int nextFailures, int nextSuccesses, bool nextOpen)
    {
        var step = HealthIncidentPolicy.Next(new(failures, successes, open), success, failureThreshold, recoveryThreshold);

        Assert.Equal(action, step.Action);
        Assert.Equal(new HealthCheckStreak(nextFailures, nextSuccesses, nextOpen), step.Streak);
    }

    [Fact]
    public void IncidentPolicy_FlappingBelowThreshold_NeverOpens_AndAnOpenIncidentNeverOpensTwice()
    {
        var streak = HealthCheckStreak.Initial;
        var actions = new List<HealthIncidentAction>();
        foreach (var success in new[] { false, false, true, false, false, true, false, false, false, false, true, false, true, true })
        {
            var step = HealthIncidentPolicy.Next(streak, success, 3, 2);
            actions.Add(step.Action);
            streak = step.Streak;
        }

        Assert.Equal(1, actions.Count(action => action == HealthIncidentAction.Open));
        Assert.Equal(1, actions.Count(action => action == HealthIncidentAction.Close));
        Assert.Equal(HealthIncidentAction.Open, actions[8]);
        Assert.Equal(HealthIncidentAction.Close, actions[^1]);
    }

    [Theory]
    [InlineData(20.0, new int[0], null)]
    [InlineData(14.0, new int[0], 14)]
    [InlineData(10.0, new int[0], 14)]
    [InlineData(10.0, new[] { 14 }, null)]
    [InlineData(3.0, new[] { 14 }, 3)]
    [InlineData(2.5, new int[0], 3)]
    [InlineData(2.0, new[] { 3 }, null)]
    [InlineData(1.0, new[] { 14, 3 }, null)]
    [InlineData(-1.0, new int[0], null)]
    public void CertificateExpiryPolicy_WarnsOnceAt14AndOnceAt3Days(double daysLeft, int[] warned, int? expected)
        => Assert.Equal(expected, CertificateExpiryPolicy.Next(Now.AddDays(daysLeft), Now, warned));

    [Fact]
    public void Series_UptimeAndPercentiles_UseOnlyTheWindow()
    {
        var samples = new List<HealthSample>
        {
            new(Now.AddDays(-8), false, null, null, "timeout"),
            new(Now.AddDays(-2), false, null, 503, "unexpected-status"),
        };
        samples.AddRange(Enumerable.Range(1, 20).Select(index =>
            new HealthSample(Now.AddMinutes(-index), true, index * 10, 200, null)));
        samples.Add(new(Now.AddMinutes(-30), false, 900, 500, "unexpected-status"));

        Assert.Equal(20d / 21, HealthSeries.Uptime(samples, Now, TimeSpan.FromHours(24))!.Value, 6);
        Assert.Equal(20d / 22, HealthSeries.Uptime(samples, Now, TimeSpan.FromDays(7))!.Value, 6);
        Assert.Equal(20d / 23, HealthSeries.Uptime(samples, Now, TimeSpan.FromDays(30))!.Value, 6);
        Assert.Null(HealthSeries.Uptime([], Now, TimeSpan.FromDays(1)));
        var latency = HealthSeries.Latency(samples, Now, TimeSpan.FromHours(24));
        Assert.Equal(new HealthLatency(100, 190, 200), latency);
    }

    [Fact]
    public void Series_HourlyAggregation_AndDaysLeavingTheRawWindow()
    {
        var hour = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var aggregates = HealthSeries.Hourly([
            new(hour.AddMinutes(1), true, 100, 200, null),
            new(hour.AddMinutes(2), true, 300, 200, null),
            new(hour.AddMinutes(3), false, null, null, "timeout"),
            new(hour.AddHours(1).AddMinutes(5), true, 50, 200, null),
        ]);

        Assert.Equal(new HealthHourlyAggregate[] { new(hour, 3, 1, 100, 300, 300), new(hour.AddHours(1), 1, 0, 50, 50, 50) },
            aggregates);
        Assert.Equal([new DateOnly(2026, 9, 18)], HealthSeries.DaysToAggregate(
            [new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 19), new DateOnly(2026, 10, 19)], Now));
    }

    [Fact]
    public void Defaults_DeriveFromHealthUrlAndPublicAddress()
    {
        var https = Environment("https://shop.example", "https://shop.example/healthz");
        var checks = HealthCheckDeclarations.Effective(https);
        Assert.Equal(["http", "tls"], checks.Select(check => check.Id));
        Assert.Equal("https://shop.example/healthz", checks[0].Url);
        Assert.Equal(("shop.example", 443), (checks[1].Host, checks[1].Port!.Value));
        Assert.All(checks, check => Assert.Equal((60, 10, 3, true), (check.IntervalSeconds, check.TimeoutSeconds,
            check.FailureThreshold, check.Derived)));

        Assert.Equal(["http://shop.example:8080/"], HealthCheckDeclarations.Effective(Environment("http://shop.example:8080", null))
            .Select(check => check.Target));
        Assert.Equal(["shop.example/"], HealthCheckDeclarations.Effective(Environment("http://shop.example", null))
            .Select(check => check.Url!.Replace("http://", "")));
        Assert.Equal(["db.internal:5432"], HealthCheckDeclarations.Effective(Environment("db.internal:5432", null))
            .Select(check => check.Target));
        Assert.Empty(HealthCheckDeclarations.Effective(Environment("not an address", null)));
        Assert.Empty(HealthCheckDeclarations.Effective(Environment("https://shop.example", null) with { HealthChecks = [] }));
    }

    [Fact]
    public void Declarations_ParseExplicitChecks_AndRejectInvalidOnes()
    {
        using var document = JsonDocument.Parse("""
            { "healthChecks": [
              { "id": "api", "kind": "http", "url": "https://shop.example/api/health", "expectedStatus": 204,
                "bodyContains": "ok", "intervalSeconds": 120, "timeoutSeconds": 5, "failureThreshold": 5 },
              { "id": "db", "kind": "tcp", "host": "db.internal", "port": 5432 },
              { "id": "cert", "kind": "tls", "url": "https://shop.example" } ] }
            """);
        var checks = HealthCheckDeclarations.Parse(document.RootElement, "stage.json")!;

        Assert.Equal(new HealthCheckDefinition("api", "http", "https://shop.example/api/health", null, null, 204, "ok",
            120, 5, 5, 2), checks[0]);
        Assert.Equal("db.internal:5432", checks[1].Target);
        Assert.Equal("shop.example:443", checks[2].Target);
        using var absent = JsonDocument.Parse("{}");
        Assert.Null(HealthCheckDeclarations.Parse(absent.RootElement, "stage.json"));

        foreach (var invalid in new[]
        {
            """{ "id": "API", "kind": "http", "url": "https://x.example" }""",
            """{ "id": "a", "kind": "udp", "host": "x.example", "port": 1 }""",
            """{ "id": "a", "kind": "http", "url": "ftp://x.example" }""",
            """{ "id": "a", "kind": "tcp", "host": "x.example" }""",
            """{ "id": "a", "kind": "http", "url": "https://x.example", "intervalSeconds": 30 }""",
            """{ "id": "a", "kind": "http", "url": "https://x.example", "timeoutSeconds": 60, "intervalSeconds": 60 }""",
            """{ "id": "a", "kind": "http", "url": "https://x.example", "failureThreshold": 0 }""",
            """{ "id": "a", "kind": "http", "url": "https://x.example", "expectedStatus": 99 }""",
        })
        {
            using var bad = JsonDocument.Parse($$"""{ "healthChecks": [ {{invalid}} ] }""");
            Assert.Throws<JsonException>(() => HealthCheckDeclarations.Parse(bad.RootElement, "stage.json"));
        }
        using var duplicate = JsonDocument.Parse("""
            { "healthChecks": [ { "id": "a", "kind": "tcp", "host": "x.example", "port": 1 },
                                { "id": "a", "kind": "tcp", "host": "y.example", "port": 2 } ] }
            """);
        Assert.Throws<JsonException>(() => HealthCheckDeclarations.Parse(duplicate.RootElement, "stage.json"));
    }

    private static EnvironmentDeclaration Environment(string address, string? healthUrl)
        => new("stage", "Stage", "stage", "stage-host", address, "container", "8081", healthUrl, "person", [], 8080);
}
