using System.Net;
using System.Net.Sockets;
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
        DateOnly[] days = [new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 19), new DateOnly(2026, 10, 19)];
        Assert.Equal([new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 19)], HealthSeries.DaysToAggregate(days, Now));
        Assert.Equal([new DateOnly(2026, 9, 18)], HealthSeries.DaysPastRawRetention(days, Now));
    }

    [Fact]
    public void Series_LongWindowUptime_UsesAggregatesAndOnlyTheRawTailAfterThem()
    {
        var hour = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        HealthHourlyAggregate[] hourly = [new(hour.AddDays(-40), 60, 60, null, null, null), new(hour, 60, 6, 100, 200, 300)];
        HealthSample[] raw =
        [
            new(hour.AddMinutes(30), false, null, null, "timeout"), // already in the aggregate of that hour
            new(Now.AddMinutes(-2), true, 90, 200, null),
            new(Now.AddMinutes(-1), false, null, 503, "unexpected-status"),
        ];

        Assert.Equal(hour.AddHours(1), HealthSeries.AggregatedUntil(hourly));
        Assert.Null(HealthSeries.AggregatedUntil([]));
        Assert.Equal(55d / 62, HealthSeries.Uptime(hourly, raw, Now, TimeSpan.FromDays(30))!.Value, 6);
        Assert.Equal(0.5, HealthSeries.Uptime(hourly, raw, Now, TimeSpan.FromDays(7))!.Value, 6);
        Assert.Null(HealthSeries.Uptime([], [], Now, TimeSpan.FromDays(30)));
    }

    [Theory]
    [InlineData(false, 0, EnvironmentIncidentAction.None)]
    [InlineData(false, 1, EnvironmentIncidentAction.Open)]
    [InlineData(false, 2, EnvironmentIncidentAction.Open)]
    [InlineData(true, 2, EnvironmentIncidentAction.None)]
    [InlineData(true, 1, EnvironmentIncidentAction.None)]
    [InlineData(true, 0, EnvironmentIncidentAction.Close)]
    public void EnvironmentIncidentPolicy_Matrix(bool open, int openChecks, EnvironmentIncidentAction expected)
        => Assert.Equal(expected, EnvironmentIncidentPolicy.Next(open, openChecks));

    [Theory]
    [InlineData("127.0.0.1", HealthAddressClass.Blocked)]
    [InlineData("127.10.0.5", HealthAddressClass.Blocked)]
    [InlineData("0.0.0.0", HealthAddressClass.Blocked)]
    [InlineData("169.254.169.254", HealthAddressClass.Blocked)]
    [InlineData("169.254.10.1", HealthAddressClass.Blocked)]
    [InlineData("100.100.100.200", HealthAddressClass.Blocked)]
    [InlineData("168.63.129.16", HealthAddressClass.Blocked)]
    [InlineData("224.0.0.1", HealthAddressClass.Blocked)]
    [InlineData("255.255.255.255", HealthAddressClass.Blocked)]
    [InlineData("::1", HealthAddressClass.Blocked)]
    [InlineData("::", HealthAddressClass.Blocked)]
    [InlineData("fe80::1", HealthAddressClass.Blocked)]
    [InlineData("ff02::1", HealthAddressClass.Blocked)]
    [InlineData("fd00:ec2::254", HealthAddressClass.Blocked)]
    [InlineData("::ffff:127.0.0.1", HealthAddressClass.Blocked)]
    [InlineData("::ffff:169.254.169.254", HealthAddressClass.Blocked)]
    [InlineData("64:ff9b::7f00:1", HealthAddressClass.Blocked)]
    [InlineData("64:ff9b::a00:1", HealthAddressClass.Private)]
    [InlineData("64:ff9b::808:808", HealthAddressClass.Public)]
    [InlineData("2002:7f00:1::", HealthAddressClass.Blocked)]
    [InlineData("2002:c0a8:101::", HealthAddressClass.Private)]
    [InlineData("2002:808:808::", HealthAddressClass.Public)]
    [InlineData("10.1.2.3", HealthAddressClass.Private)]
    [InlineData("172.16.0.1", HealthAddressClass.Private)]
    [InlineData("172.31.255.255", HealthAddressClass.Private)]
    [InlineData("192.168.1.10", HealthAddressClass.Private)]
    [InlineData("100.64.0.1", HealthAddressClass.Private)]
    [InlineData("fd12:3456::1", HealthAddressClass.Private)]
    [InlineData("::ffff:10.0.0.1", HealthAddressClass.Private)]
    [InlineData("172.32.0.1", HealthAddressClass.Public)]
    [InlineData("8.8.8.8", HealthAddressClass.Public)]
    [InlineData("203.0.113.7", HealthAddressClass.Public)]
    [InlineData("2001:4860:4860::8888", HealthAddressClass.Public)]
    public void AddressPolicy_ClassifiesTargets(string address, HealthAddressClass expected)
    {
        var parsed = IPAddress.Parse(address);
        Assert.Equal(expected, HealthCheckAddressPolicy.Classify(parsed));
        Assert.Equal(expected == HealthAddressClass.Public, HealthCheckAddressPolicy.Allows(parsed, false));
        Assert.Equal(expected != HealthAddressClass.Blocked, HealthCheckAddressPolicy.Allows(parsed, true));
    }

    [Fact]
    public async Task Connector_ChecksEveryResolution_SoARebindingAnswerIsRefused()
    {
        var answers = new Queue<IPAddress[]>([
            [IPAddress.Parse("203.0.113.7")],
            [IPAddress.Parse("169.254.169.254")],
            [IPAddress.Parse("169.254.169.254"), IPAddress.Parse("203.0.113.8")],
        ]);
        var connects = new List<IPEndPoint>();
        var connector = new HealthCheckConnector((_, _) => Task.FromResult(answers.Dequeue()), (endpoint, _) =>
        {
            connects.Add(endpoint);
            return Task.FromResult(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
        });

        (await connector.ConnectAsync("rebind.example", 80, false, CancellationToken.None)).Dispose();
        await Assert.ThrowsAsync<HealthCheckAddressBlockedException>(
            () => connector.ConnectAsync("rebind.example", 80, false, CancellationToken.None));
        (await connector.ConnectAsync("rebind.example", 80, false, CancellationToken.None)).Dispose();

        Assert.Equal([new IPEndPoint(IPAddress.Parse("203.0.113.7"), 80), new IPEndPoint(IPAddress.Parse("203.0.113.8"), 80)],
            connects);
    }

    [Theory]
    [InlineData("https://probe:pw@shop.example/health?token=abc#top", "https://shop.example/health")]
    [InlineData("http://shop.example:8080/api/v1/health?x=1", "http://shop.example:8080/api/v1/health")]
    [InlineData("https://shop.example", "https://shop.example/")]
    [InlineData("db.internal:5432", "db.internal:5432")]
    public void Targets_DropUserInfoQueryAndFragment(string target, string expected)
        => Assert.Equal(expected, HealthTargets.Display(target));

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
            """{ "id": "a", "kind": "http", "url": "https://x.example", "intervalSeconds": 90.5 }""",
            """{ "id": "a", "kind": "http", "url": "https://x.example", "timeoutSeconds": 1e12 }""",
            """{ "id": "a", "kind": "tcp", "host": "x.example", "port": "22" }""",
            "1",
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

    [Fact]
    public void PrivateNetworkOptIn_IsExplicit_AndReachesEveryEffectiveCheck()
    {
        using var on = JsonDocument.Parse("""{ "healthChecksAllowPrivateNetwork": true }""");
        using var off = JsonDocument.Parse("{}");
        using var wrong = JsonDocument.Parse("""{ "healthChecksAllowPrivateNetwork": "yes" }""");
        Assert.True(HealthCheckDeclarations.AllowsPrivateNetwork(on.RootElement, "stage.json"));
        Assert.False(HealthCheckDeclarations.AllowsPrivateNetwork(off.RootElement, "stage.json"));
        Assert.Throws<JsonException>(() => HealthCheckDeclarations.AllowsPrivateNetwork(wrong.RootElement, "stage.json"));

        var derived = Environment("https://10.0.0.5", null);
        Assert.All(HealthCheckDeclarations.Effective(derived), check => Assert.False(check.AllowPrivateNetwork));
        Assert.All(HealthCheckDeclarations.Effective(derived with { HealthChecksAllowPrivateNetwork = true }),
            check => Assert.True(check.AllowPrivateNetwork));
        var declared = derived with
        {
            HealthChecks = [new("db", "tcp", null, "10.0.0.6", 5432, 200, null, 60, 5, 3, 2)],
            HealthChecksAllowPrivateNetwork = true,
        };
        Assert.True(Assert.Single(HealthCheckDeclarations.Effective(declared)).AllowPrivateNetwork);
    }

    private static EnvironmentDeclaration Environment(string address, string? healthUrl)
        => new("stage", "Stage", "stage", "stage-host", address, "container", "8081", healthUrl, "person", [], 8080);
}
