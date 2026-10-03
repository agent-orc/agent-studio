extern alias UpdSvc;

using System.Diagnostics;
using AgentStudio.TestSupport;
using UpdSvc::AgentTaskboard.UpdateService;
using UpdSvc::AgentTaskboard.UpdateService.Installation;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2947 (Dossier AGT-W63 D6 option A, I06). Direct matrix for the
/// installation upgrade contract: one updater per placement, bounded drain,
/// verified backup, retained prior release, resume after interruption,
/// success only from observed digests plus runtime mode plus canary, no
/// downgrade after a schema change, and offline hosts held pending.
/// The script-level rehearsals run the real Docker lifecycle scripts.
/// </summary>
public class InstallationUpgradePolicyTests
{
    private static readonly InstallationProtocolRange V1To2 = new(2, 1, 2);

    private static InstallationRelease Release(string version, int schema, string? digest = null) => new(
        version,
        [new("authority", $"task-server:{version}", digest ?? $"sha256:{version}-a"),
         new("engine", $"engine:{version}", digest ?? $"sha256:{version}-e"),
         new("edge", "caddy:2-alpine", "sha256:edge")],
        schema,
        V1To2);

    private static InstallationBackup Backup(int schema, bool verified = true)
        => new("bk-1", "sha", verified, schema, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(InstallationPlacement.DevStableCheckout, InstallationUpdater.CheckoutUpdateService)]
    [InlineData(InstallationPlacement.InstalledCompose, InstallationUpdater.DockerLifecycle)]
    [InlineData(InstallationPlacement.InstalledSystemd, InstallationUpdater.SystemdLifecycle)]
    [InlineData(InstallationPlacement.RunnerHost, InstallationUpdater.RunnerHostTooling)]
    public void Each_placement_has_exactly_one_updater(InstallationPlacement placement, InstallationUpdater owner)
    {
        Assert.Equal(owner, InstallationUpgradePolicy.SelectUpdater(placement));
        foreach (var updater in Enum.GetValues<InstallationUpdater>())
            Assert.Equal(updater == owner, InstallationUpgradePolicy.MayApply(updater, placement));
    }

    [Fact]
    public void Checkout_update_service_defaults_to_the_checkout_placement()
        => Assert.Equal(InstallationPlacement.DevStableCheckout, new UpdateServiceOptions().Placement);

    [Fact]
    public void Preflight_allows_a_bounded_drain_with_verified_backup_and_retained_prior()
    {
        var current = Release("1.0.0", 24);
        var result = InstallationUpgradePolicy.EvaluatePreflight(current, Release("1.1.0", 24), current, Backup(24), 900);

        Assert.True(result.Allowed, string.Join("; ", result.Refusals));
    }

    [Theory]
    [InlineData(0, true, 24, true, "Drain must be bounded")]
    [InlineData(7200, true, 24, true, "Drain must be bounded")]
    [InlineData(900, false, 24, true, "verified backup")]
    [InlineData(900, true, 23, true, "store schema 23")]
    [InlineData(900, true, 24, false, "retained as the prior release")]
    public void Preflight_refuses_each_missing_precondition(
        int drainSeconds, bool backupVerified, int backupSchema, bool priorRetained, string refusal)
    {
        var current = Release("1.0.0", 24);
        var result = InstallationUpgradePolicy.EvaluatePreflight(
            current,
            Release("1.1.0", 24),
            priorRetained ? current : null,
            Backup(backupSchema, backupVerified),
            drainSeconds);

        Assert.False(result.Allowed);
        Assert.Contains(result.Refusals, r => r.Contains(refusal, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UpgradePhase.Started, UpgradePhase.Drained)]
    [InlineData(UpgradePhase.Drained, UpgradePhase.Drained)]
    [InlineData(UpgradePhase.BackedUp, UpgradePhase.Switched)]
    [InlineData(UpgradePhase.Switched, UpgradePhase.Healthy)]
    [InlineData(UpgradePhase.Healthy, UpgradePhase.Healthy)]
    [InlineData(UpgradePhase.Compatible, UpgradePhase.Resumed)]
    [InlineData(UpgradePhase.Resumed, UpgradePhase.CanaryPassed)]
    public void Interrupted_upgrade_resumes_without_switching_twice(UpgradePhase last, UpgradePhase next)
        => Assert.Equal(next, InstallationUpgradePolicy.ResumeFrom(last));

    [Fact]
    public void Success_requires_observed_digests_normal_mode_and_canary()
    {
        var desired = Release("1.1.0", 24);
        var running = desired.Components;

        Assert.Equal(UpgradeVerdict.Succeeded, InstallationUpgradePolicy.EvaluateCompletion(
            desired, new("1.1.0", running, "Normal", true, true)));
        Assert.Equal(UpgradeVerdict.AwaitingCanary, InstallationUpgradePolicy.EvaluateCompletion(
            desired, new("1.1.0", running, "Normal", true, null)));
        Assert.Equal(UpgradeVerdict.Failed, InstallationUpgradePolicy.EvaluateCompletion(
            desired, new("1.1.0", running, "Normal", true, false)));
    }

    [Theory]
    [InlineData("1.0.0", "Normal", true, "sha256:1.1.0-a")]
    [InlineData("1.1.0", "Draining", true, "sha256:1.1.0-a")]
    [InlineData("1.1.0", "Normal", false, "sha256:1.1.0-a")]
    [InlineData("1.1.0", "Normal", true, "sha256:pulled-but-not-running")]
    [InlineData("1.1.0", "Normal", true, null)]
    public void Health_or_a_pulled_image_alone_is_not_success(
        string version, string mode, bool health, string? authorityDigest)
    {
        var desired = Release("1.1.0", 24);
        var running = desired.Components
            .Select(c => c.Role == "authority" ? c with { ImageDigest = authorityDigest } : c)
            .ToList();

        Assert.Equal(UpgradeVerdict.Failed, InstallationUpgradePolicy.EvaluateCompletion(
            desired, new(version, running, mode, health, true)));
    }

    [Theory]
    [InlineData(24, 24, true, RollbackDecision.SwitchToPrior)]
    [InlineData(24, 25, true, RollbackDecision.SwitchToPrior)]
    [InlineData(25, 24, true, RollbackDecision.RestoreFromBackupRequired)]
    [InlineData(25, 24, false, RollbackDecision.Refused)]
    public void Rollback_never_downgrades_across_a_schema_change(
        int activeSchema, int targetSchema, bool backupAtTargetSchema, RollbackDecision expected)
    {
        var backup = backupAtTargetSchema ? Backup(targetSchema) : Backup(activeSchema);

        Assert.Equal(expected, InstallationUpgradePolicy.EvaluateRollback(
            Release("1.1.0", activeSchema), Release("1.0.0", targetSchema), backup));
    }

    [Fact]
    public void Rollback_without_a_recorded_target_schema_is_refused()
        => Assert.Equal(RollbackDecision.Refused, InstallationUpgradePolicy.EvaluateRollback(
            Release("1.1.0", 24), Release("1.0.0", 24) with { StoreSchemaVersion = null }, Backup(24)));

    [Theory]
    [InlineData(false, 2, "pending", HostReturnDecision.RemainPending)]
    [InlineData(false, 0, "pending", HostReturnDecision.RemainPending)]
    [InlineData(true, 2, "current", HostReturnDecision.Admit)]
    [InlineData(true, 3, "incompatible", HostReturnDecision.RemainPending)]
    public void Offline_hosts_stay_pending_until_their_protocol_is_admissible(
        bool online, int protocol, string state, HostReturnDecision decision)
    {
        var host = new InstallationHost("h1", "r", protocol, online, state, null);

        Assert.Equal(state, InstallationUpgradePolicy.ClassifyHost(online, protocol, V1To2));
        Assert.Equal(decision, InstallationUpgradePolicy.EvaluateHostReturn(host, V1To2));
    }

    [Fact]
    public void Only_online_hosts_block_a_candidate_protocol()
    {
        var hosts = new[]
        {
            new InstallationHost("online-old", "r", 1, true, "current", null),
            new InstallationHost("offline-old", "r", 1, false, "pending", null),
        };

        Assert.Equal(["online-old"], InstallationUpgradePolicy.IncompatibleOnlineHosts(hosts, new(3, 2, 3)));
    }

    [Fact]
    public void Manifest_with_another_schema_version_is_rejected()
        => Assert.Throws<InvalidDataException>(() => InstallationManifest.Parse(
            """{"schemaVersion":2,"installationId":"x","placement":"installed-compose","updater":"docker-lifecycle","desired":{"version":"1","components":[]},"progress":{"operation":"update","targetVersion":"1","phase":"started","outcome":"in-progress","updatedAt":"2026-10-01T00:00:00Z"},"hosts":[]}"""));

    /// <summary>
    /// Runs every acceptance rehearsal (N-1 to N, active-run drain, bounded
    /// drain, candidate boot failure, schema-changing failure, interrupted
    /// upgrade, offline host, incompatible protocol, rollback and refused
    /// rollback) against the real update-docker.sh and rollback-docker.sh,
    /// then parses the resulting manifest shape with the C# contract.
    /// </summary>
    [SkippableFact]
    public async Task Docker_lifecycle_rehearsals_pass()
    {
        Skip.If(OperatingSystem.IsWindows(), "The rehearsal fakes rely on POSIX symlinks and kill.");
        Skip.IfNot(PosixShell.IsAvailable, "No POSIX shell.");
        Skip.If(FindOnPath("jq") is null, "jq is not installed.");
        var script = Path.Combine(FindRepositoryRoot(), "deploy", "release", "agent-orchestrator", "tests", "upgrade-rehearsal.sh");

        var manifestOut = Path.Combine(Path.GetTempPath(), $"installation-manifest-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo(PosixShell.RequirePath(), script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["REHEARSAL_MANIFEST_OUT"] = manifestOut;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, output + error);
        Assert.Contains("all rehearsals passed", output);

        var manifest = InstallationManifest.Parse(await File.ReadAllTextAsync(manifestOut));
        File.Delete(manifestOut);
        Assert.Equal("docker-lifecycle", manifest.Updater);
        Assert.Equal("1.1.0", manifest.Observed!.Version);
        Assert.Equal("Normal", manifest.Observed.RuntimeMode);
        Assert.All(manifest.Observed.Components, c => Assert.False(string.IsNullOrEmpty(c.ImageDigest)));
        Assert.Equal("pending", Assert.Single(manifest.Hosts).State);
    }

    private static string? FindOnPath(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agent-taskboard.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Agent Studio repository root was not found.");
    }
}
