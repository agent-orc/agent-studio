using AgentStudio.Management;
using Xunit;

namespace AgentStudio.Tests;

public sealed class InstallationConnectivityTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static InstallationConnectivityManifest Transition(string owner = ConnectivityLinkOwners.LinkSupervisor,
        string package = ConnectivityPackages.LegacyBackend, string? knownHosts = "/etc/agent-studio/known_hosts.d/runner-02",
        IReadOnlyList<string>? listeners = null) => new(
        1, "inst-01", 4, ConnectivityModes.ReverseSshTransition, package,
        "http://127.0.0.1:5031", "http://127.0.0.1:4011", null, owner,
        listeners ?? ["legacy-api", "runner-tunnel", "browser"], null,
        [
            new("agent-runner-01", ConnectivityRoutes.Loopback, null, null, null, null, null),
            new("agent-runner-02", ConnectivityRoutes.ReverseSsh, "runner-02", knownHosts, null, null, null),
        ]);

    private static InstallationConnectivityManifest Direct(string origin = "https://task-server.wg.internal",
        string? ca = "/etc/agent-runner/private-ca.pem", IReadOnlyList<string>? listeners = null) => new(
        1, "inst-01", 5, ConnectivityModes.WireGuardDirect, ConnectivityPackages.ControlPlaneCompose,
        origin, "http://127.0.0.1:4011", ca, ConnectivityLinkOwners.None,
        listeners ?? ["standalone-task-server", "private-edge"], null,
        [new("agent-runner-02", ConnectivityRoutes.PrivateHttps, null, null, null, null, null)]);

    private static string[] Codes(InstallationConnectivityManifest manifest)
        => ConnectivityPolicy.Validate(manifest).Select(item => item.Code).ToArray();

    [Fact]
    public void Transition_GeneratesOneLinkWithPinnedKeyAndReverseDirection()
    {
        var resolved = ConnectivityPolicy.Resolve(Transition());
        var link = Assert.Single(resolved.RunnerLinks);
        Assert.Equal(("agent-runner-02", 15031, 5031), (link.RunnerId, link.RemotePort, link.LocalPort));
        Assert.Equal([5, 10, 30, 60, 120], link.BackoffSeconds);
        Assert.Equal("http://127.0.0.1:15031",
            ConnectivityPolicy.RunnerServerUrl(resolved, Transition().Runners[1]));
        Assert.Equal("http://127.0.0.1:5031",
            ConnectivityPolicy.RunnerServerUrl(resolved, Transition().Runners[0]));
        var config = ConnectivityPolicy.RunnerLinksConfiguration(resolved);
        Assert.Equal("/etc/agent-studio/known_hosts.d/runner-02", config["RunnerLinks:0:KnownHostsFile"]);
        Assert.DoesNotContain(config.Values, value => value?.Contains("token", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(config, ConnectivityPolicy.RunnerLinksConfiguration(ConnectivityPolicy.Resolve(Transition())));
    }

    [Fact]
    public void TunnelKeeperOwnerDuringSoak_GeneratesNoSupervisorLink()
    {
        var resolved = ConnectivityPolicy.Resolve(Transition(ConnectivityLinkOwners.TunnelKeeper));
        Assert.Empty(resolved.RunnerLinks);
        Assert.Equal(ConnectivityLinkOwners.TunnelKeeper, resolved.LinkOwner);
    }

    [Theory]
    [InlineData(ConnectivityPackages.StandaloneTaskServer, "link-owner-package")]
    [InlineData(ConnectivityPackages.ControlPlaneCompose, "link-owner-package")]
    public void StandalonePackages_DoNotHostLinkSupervisor(string package, string code)
    {
        Assert.False(ConnectivityPolicy.PackageHostsLinkSupervisor(package));
        Assert.Contains(code, Codes(Transition(package: package)));
    }

    [Fact]
    public void MissingHostKeyPinOwnerOrDuplicatedListener_AreRejected()
    {
        Assert.Contains("host-key-pin", Codes(Transition(knownHosts: null)));
        Assert.Contains("link-owner-missing", Codes(Transition(ConnectivityLinkOwners.None)));
        Assert.Contains("listener-duplicated", Codes(Transition(listeners: ["legacy-api", "legacy-api"])));
        Assert.Contains("listener-not-allowed", Codes(Transition(listeners: ["legacy-api", "standalone-task-server"])));
        var clash = Transition() with { PortOverrides = [new("runner-tunnel", 5031)] };
        Assert.Contains("port-duplicated", Codes(clash));
        Assert.Throws<InvalidOperationException>(() => ConnectivityPolicy.Resolve(Transition(knownHosts: null)));
    }

    [Fact]
    public void DirectWireGuard_RequiresPrivateDnsTlsTrustAndNoPublicOrLegacyListener()
    {
        Assert.Empty(Codes(Direct()));
        Assert.Empty(ConnectivityPolicy.Resolve(Direct()).RunnerLinks);
        Assert.Contains("private-tls", Codes(Direct("http://task-server.wg.internal")));
        Assert.Contains("private-dns", Codes(Direct("https://10.60.0.1")));
        Assert.Contains("private-trust", Codes(Direct(ca: null)));
        Assert.Contains("listener-not-allowed", Codes(Direct(listeners: ["standalone-task-server", "legacy-api"])));
        Assert.Contains("listener-not-allowed", Codes(Direct(listeners: ["private-edge", "runner-tunnel"])));
        Assert.Contains("listener-not-allowed", Codes(Direct(listeners: ["private-edge", "local-updater"])));
        var workstationLink = Direct() with
        {
            Runners = [new("agent-runner-02", ConnectivityRoutes.ReverseSsh, "runner-02", "/k", null, null, null)],
        };
        Assert.Contains("route-mode", Codes(workstationLink));
        Assert.Contains("link-owner-missing", Codes(workstationLink));
    }

    [Fact]
    public void LoopbackModes_RejectNonLoopbackServerOrigin()
        => Assert.Contains("server-origin-scope", Codes(Transition() with { ServerOrigin = "http://0.0.0.0:5031" }));

    [Fact]
    public void StaleOrMismatchedOrExpiredAdvertisement_IsNotFreshReachability()
    {
        var resolved = ConnectivityPolicy.Resolve(Direct());
        var runner = Direct().Runners[0];
        Assert.Null(ConnectivityPolicy.AdvertisementFinding(
            resolved, runner, 5, "https://task-server.wg.internal/", Now.AddSeconds(-30), Now, TimeSpan.FromSeconds(90)));
        Assert.Equal("advertisement-stale-revision", ConnectivityPolicy.AdvertisementFinding(
            resolved, runner, 4, "https://task-server.wg.internal", Now, Now, TimeSpan.FromSeconds(90))!.Code);
        var mismatch = ConnectivityPolicy.AdvertisementFinding(
            resolved, runner, 5, "http://127.0.0.1:15031", Now, Now, TimeSpan.FromSeconds(90))!;
        Assert.Equal("advertisement-route-mismatch", mismatch.Code);
        Assert.Contains("https://task-server.wg.internal", mismatch.Remediation);
        Assert.Equal("advertisement-expired", ConnectivityPolicy.AdvertisementFinding(
            resolved, runner, 5, "https://task-server.wg.internal", Now.AddMinutes(-5), Now, TimeSpan.FromSeconds(90))!.Code);
    }

    [Theory]
    [InlineData(-1, true, true, "tls-expired")]
    [InlineData(3, true, true, "tls-expiring")]
    [InlineData(90, false, true, "tls-untrusted")]
    [InlineData(90, true, false, "tls-name-mismatch")]
    [InlineData(90, true, true, null)]
    public void EdgeCertificate_ExpiryTrustAndNameMatrix(int days, bool trusted, bool named, string? expected)
    {
        var finding = ConnectivityPolicy.CertificateFinding("task-server.wg.internal", Now.AddDays(days),
            named ? ["task-server.wg.internal"] : ["other.internal"], trusted, Now, TimeSpan.FromDays(14));
        Assert.Equal(expected, finding?.Code);
        if (finding is not null) Assert.False(string.IsNullOrWhiteSpace(finding.Remediation));
    }

    [Theory]
    [InlineData("one-box.json")]
    [InlineData("reverse-ssh-transition.json")]
    [InlineData("wireguard-direct.json")]
    public void ShippedExamples_AreValid(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "deploy", "connectivity")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        var manifest = InstallationConnectivityManifest.Parse(
            File.ReadAllText(Path.Combine(directory!, "deploy", "connectivity", name)));
        Assert.Empty(ConnectivityPolicy.Validate(manifest));
    }
}
