using AgentStudio.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2865. The ADR-0031 phase-6 db-touch sentinel used to hang off
/// <c>DevTools:UpdateStableEnabled</c>, whose other consumer is the DevTools
/// SSE stream that runs <c>update-stable.sh</c>. A default Stable sets
/// neither that flag nor <c>Environment:IsDev</c>, so phase 6 could never pass
/// there and the run found out only after the restart (run 0649a4e0,
/// 17.09.2026).
///
/// The gate is now its own decision. These tests are the matrix: they pin the
/// precedence, the contract-installed default, and - the constraint that must
/// not regress - that nothing here opens the DevTools SSE stream.
/// </summary>
public class InternalProbeGateTests
{
    [Fact]
    public void PlainProduction_IsClosed()
    {
        var decision = InternalProbeGate.Decide(
            probeEnabled: null, isDev: false, devToolsUpdateStableEnabled: false, updateContractInstalled: false);

        Assert.False(decision.Enabled);
        Assert.Equal(InternalProbeGateReason.NoGate, decision.Reason);
    }

    [Fact]
    public void UpdateContractInstalled_OpensTheSentinelWithoutAnyDevToolsFlag()
    {
        // The incident's default Stable: no dev branding, no DevTools flag,
        // but the Update Service owns this instance, so the matrix it is
        // about to run has to be able to answer.
        var decision = InternalProbeGate.Decide(
            probeEnabled: null, isDev: false, devToolsUpdateStableEnabled: false, updateContractInstalled: true);

        Assert.True(decision.Enabled);
        Assert.Equal(InternalProbeGateReason.UpdateContractInstalled, decision.Reason);
    }

    [Fact]
    public void DevEnvironment_OpensTheSentinel()
    {
        var decision = InternalProbeGate.Decide(
            probeEnabled: null, isDev: true, devToolsUpdateStableEnabled: false, updateContractInstalled: false);

        Assert.True(decision.Enabled);
        Assert.Equal(InternalProbeGateReason.DevEnvironment, decision.Reason);
    }

    [Fact]
    public void LegacyDevToolsFlag_StillOpensTheSentinel()
    {
        // Backwards compatibility: an operator who fixed the incident by hand
        // by setting DevTools:UpdateStableEnabled=true keeps a working probe
        // after this change.
        var decision = InternalProbeGate.Decide(
            probeEnabled: null, isDev: false, devToolsUpdateStableEnabled: true, updateContractInstalled: false);

        Assert.True(decision.Enabled);
        Assert.Equal(InternalProbeGateReason.DevToolsUpdateStable, decision.Reason);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ExplicitFlag_WinsOverEveryOtherInput(bool probeEnabled, bool isDev, bool contractInstalled)
    {
        var decision = InternalProbeGate.Decide(probeEnabled, isDev, devToolsUpdateStableEnabled: isDev, contractInstalled);

        Assert.Equal(probeEnabled, decision.Enabled);
        Assert.Equal(
            probeEnabled ? InternalProbeGateReason.ExplicitlyEnabled : InternalProbeGateReason.ExplicitlyDisabled,
            decision.Reason);
    }

    [Fact]
    public void ExplicitlyDisabled_ClosesTheSentinelEvenOnAContractInstallation()
    {
        var decision = InternalProbeGate.Decide(
            probeEnabled: false, isDev: true, devToolsUpdateStableEnabled: true, updateContractInstalled: true);

        Assert.False(decision.Enabled);
        Assert.Equal(InternalProbeGateReason.ExplicitlyDisabled, decision.Reason);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void ConfiguredFlag_IsReadFromConfiguration(string raw, bool expected)
    {
        var config = Configuration(new Dictionary<string, string?>
        {
            [InternalProbeGate.ProbeEnabledKey] = raw,
        });

        Assert.Equal(expected, InternalProbeGate.Decide(config).Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("yes-please")]
    public void UnsetOrUnparsableFlag_FallsBackToTheContractDefault(string raw)
    {
        // "Unset" must not silently mean "closed": that is exactly the state
        // the incident ran into. With no contract marker on disk it stays
        // closed; the marker case is covered below.
        var config = Configuration(new Dictionary<string, string?>
        {
            [InternalProbeGate.ProbeEnabledKey] = raw,
            ["Environment:IsDev"] = "true",
        });

        var decision = InternalProbeGate.Decide(config);
        Assert.True(decision.Enabled);
        Assert.Equal(InternalProbeGateReason.DevEnvironment, decision.Reason);
    }

    [Fact]
    public void ApprovedTagMarkerUnderTheWorkspace_CountsAsAnInstalledContract()
    {
        using var workspace = new TempWorkspace();
        var config = Configuration(new Dictionary<string, string?> { ["TaskRepository"] = workspace.Root });

        Assert.False(InternalProbeGate.UpdateContractInstalled(config));
        Assert.False(InternalProbeGate.Decide(config).Enabled);

        workspace.WriteApprovedTag("v0.7.0");

        Assert.True(InternalProbeGate.UpdateContractInstalled(config));
        var decision = InternalProbeGate.Decide(config);
        Assert.True(decision.Enabled);
        Assert.Equal(InternalProbeGateReason.UpdateContractInstalled, decision.Reason);
    }

    [Fact]
    public void ExplicitApprovedTagPath_OverridesTheWorkspaceLocation()
    {
        using var workspace = new TempWorkspace();
        var elsewhere = Path.Combine(workspace.Root, "elsewhere-approved-tag");
        var config = Configuration(new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace.Root,
            [InternalProbeGate.ApprovedTagFileKey] = elsewhere,
        });

        workspace.WriteApprovedTag("v0.7.0");
        Assert.False(InternalProbeGate.UpdateContractInstalled(config));

        File.WriteAllText(elsewhere, "v0.7.0");
        Assert.True(InternalProbeGate.UpdateContractInstalled(config));
    }

    [Fact]
    public void NoWorkspaceConfigured_IsNotAnInstalledContract()
    {
        var config = Configuration(new Dictionary<string, string?>());

        Assert.Null(InternalProbeGate.ApprovedTagFile(config));
        Assert.False(InternalProbeGate.UpdateContractInstalled(config));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "atp-probe-gate-" + Guid.NewGuid().ToString("N")[..8]);

        public TempWorkspace() => Directory.CreateDirectory(Root);

        public void WriteApprovedTag(string tag)
        {
            var path = Path.Combine(Root, InternalProbeGate.ApprovedTagRelativePath
                .Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, tag);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a temp dir */ }
        }
    }
}
