using AgentStudio.Setup;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Net;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class InstallationJourneyTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Principals = ["task-server-admin", "runner-coding"];

    [Theory]
    [InlineData("one-box", "studio")]
    [InlineData("workstation", "studio")]
    [InlineData("join-host", "agent-host")]
    [InlineData("attach-studio", "connector")]
    [InlineData("relocate-authority", "control-plane")]
    public void Each_journey_runs_one_installer_mode(string journey, string mode)
        => Assert.Equal(mode, JourneyPolicy.ModeFor(JourneyPolicy.Parse(journey)));

    [Fact]
    public void One_box_journey_names_every_role_fact()
        => Assert.Equal(
            ["Prerequisites", "Storage", "Release pin", "Human identity", "Project origin", "First runner", "Budgets"],
            JourneyPolicy.Steps(InstallationJourney.OneBox).Facts.Select(fact => fact.Name));

    [Fact]
    public void One_box_journey_ends_at_canary_and_recovery_checkpoint()
        => Assert.Equal(["authenticated-canary", "recovery-checkpoint"],
            JourneyPolicy.Steps(InstallationJourney.OneBox).Checkpoints.TakeLast(2));

    [Fact]
    public void Journey_flag_selects_the_mode()
        => Assert.Equal("agent-host", ProductCommand.Parse(["--journey", "join-host"]).Values["--mode"]);

    [Fact]
    public void Journey_conflicting_with_mode_is_rejected()
        => Assert.Throws<ArgumentException>(
            () => ProductCommand.Parse(["--journey", "one-box", "--mode", "connector"]));

    [Fact]
    public void Preflight_is_a_verb()
        => Assert.Equal("preflight", ProductCommand.Parse(["preflight", "--journey", "attach-studio"]).Verb);

    [Theory]
    [InlineData(null, false)]
    [InlineData("bkp_1", false)]
    public void Relocation_is_gated_by_recovery_checkpoint_and_freeze(string? checkpoint, bool frozen)
        => Assert.NotNull(JourneyPolicy.RelocationBlocker(checkpoint, frozen));

    [Fact]
    public void Relocation_proceeds_with_verified_checkpoint_and_freeze()
        => Assert.Null(JourneyPolicy.RelocationBlocker("bkp_1", authorityFrozen: true));

    [Fact]
    public void First_install_creates_an_installation_id()
    {
        var decision = Decide(null, Request("1.2.0"));

        Assert.Equal(ManifestAction.Create, decision.Action);
        Assert.Equal("inst_new", decision.Next!.InstallationId);
        Assert.Equal(InstallationManifest.PhaseInstalling, decision.Next.Phase);
    }

    [Fact]
    public void Rerun_preserves_installation_id_and_principals()
    {
        var existing = Manifest("1.2.0", InstallationManifest.PhaseComplete) with { Principals = ["operator-anna"] };

        var decision = Decide(existing, Request("1.2.0"));

        Assert.Equal(ManifestAction.Resume, decision.Action);
        Assert.Equal("inst_original", decision.Next!.InstallationId);
        Assert.Equal(["operator-anna", "task-server-admin", "runner-coding"], decision.Next.Principals);
        Assert.Equal(existing.CreatedUtc, decision.Next.CreatedUtc);
    }

    [Fact]
    public void Interrupted_install_resumes_with_the_same_release()
    {
        var decision = Decide(Manifest("1.2.0", InstallationManifest.PhaseInstalling), Request("1.2.0"));

        Assert.Equal(ManifestAction.Resume, decision.Action);
        Assert.Equal("inst_original", decision.Next!.InstallationId);
    }

    [Fact]
    public void Interrupted_install_rejects_a_different_release()
        => AssertRejected(Decide(Manifest("1.2.0", InstallationManifest.PhaseInstalling), Request("1.3.0")),
            "same release");

    [Fact]
    public void Install_of_a_wrong_version_over_a_complete_installation_is_rejected()
        => AssertRejected(Decide(Manifest("1.2.0", InstallationManifest.PhaseComplete), Request("1.1.0")),
            "update or rollback");

    [Fact]
    public void Update_to_an_older_release_is_rejected()
        => AssertRejected(Decide(Manifest("1.2.0", InstallationManifest.PhaseComplete), Request("1.1.0", "update")),
            "newer release");

    [Fact]
    public void Update_keeps_identity_and_moves_the_pin()
    {
        var decision = Decide(Manifest("1.2.0", InstallationManifest.PhaseComplete), Request("1.3.0", "update"));

        Assert.Equal(ManifestAction.Update, decision.Action);
        Assert.Equal(("inst_original", "1.3.0"), (decision.Next!.InstallationId, decision.Next.ReleaseVersion));
    }

    [Fact]
    public void Reinstall_after_uninstall_keeps_preserved_identity()
    {
        var decision = Decide(Manifest("1.2.0", InstallationManifest.PhaseUninstalled), Request("1.2.0"));

        Assert.Equal(ManifestAction.Reinstall, decision.Action);
        Assert.Equal("inst_original", decision.Next!.InstallationId);
    }

    [Fact]
    public void Reinstall_of_an_older_release_over_preserved_data_is_rejected()
        => AssertRejected(Decide(Manifest("1.2.0", InstallationManifest.PhaseUninstalled), Request("1.1.0")),
            "older");

    [Fact]
    public void Different_role_on_the_same_installation_is_rejected()
        => AssertRejected(Decide(Manifest("1.2.0", InstallationManifest.PhaseComplete),
            Request("1.2.0") with { Mode = "connector" }), "relocate-authority");

    [Fact]
    public void Joining_cannot_rewrite_the_project_origin()
        => AssertRejected(Decide(
            Manifest("1.2.0", InstallationManifest.PhaseComplete) with { ProjectOrigin = "git@example.com:a/b.git" },
            Request("1.2.0") with { ProjectOrigin = "git@example.com:c/d.git" }), "project registry");

    [Fact]
    public void Newer_manifest_schema_is_rejected()
        => AssertRejected(Decide(Manifest("1.2.0", InstallationManifest.PhaseComplete) with { Schema = 99 },
            Request("1.2.0")), "newer");

    [Fact]
    public void Update_without_manifest_is_rejected()
        => AssertRejected(Decide(null, Request("1.2.0", "update")), "install first");

    [Fact]
    public async Task Manifest_round_trips_without_secrets_and_owner_only()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var manifest = Manifest("1.2.0", InstallationManifest.PhaseComplete);
            await ManifestStore.WriteAsync(root, manifest);
            await ManifestStore.CheckpointAsync(root, manifest, "services-healthy", "observed");

            var read = await ManifestStore.ReadAsync(root);
            Assert.Equal(manifest.InstallationId, read!.InstallationId);
            Assert.Equal(manifest.Principals, read.Principals);
            var checkpoint = await File.ReadAllTextAsync(Path.Combine(root, InstallationManifest.CheckpointFileName));
            Assert.Contains("\"installationId\":\"inst_original\"", checkpoint);
            Assert.Contains("\"host\":", checkpoint);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, InstallationManifest.FileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_prerequisites_report_specific_recovery_actions()
    {
        var findings = PreflightPolicy.Evaluate(InstallationJourney.OneBox, "docker", new HostFacts(
            Windows: true, X64: true, Virtualization: false, DockerEngine: false, DockerCompose: false,
            Wsl2: false, FreeDiskBytes: 1L << 30, DnsResolves: false, WireGuardInterface: false,
            TlsTrusted: false, BackupMountWritable: false, ProviderCli: false, ProtectedSecretFile: false));

        var failed = findings.Where(finding => finding.Status == PreflightStatus.Fail).ToDictionary(f => f.Check);
        Assert.Equal(
            ["VM / virtualization", "WSL", "Docker", "Docker Compose", "Storage", "Backup mount"],
            failed.Keys);
        Assert.Contains("wsl --install", failed["WSL"].Recovery);
        Assert.Contains("Docker Desktop", failed["Docker"].Recovery);
        Assert.Equal(PreflightStatus.NotApplicable, findings.Single(f => f.Check == "DNS").Status);
        Assert.Equal(PreflightStatus.NotApplicable, findings.Single(f => f.Check == "TLS").Status);
        Assert.All(failed.Values, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Recovery)));
    }

    [Fact]
    public void Joining_host_reports_network_provider_and_secret_recovery()
    {
        var findings = PreflightPolicy.Evaluate(InstallationJourney.JoinHost, "native", new HostFacts(
            false, true, null, null, null, null, 40L << 30, false, false, false, null, false, false,
            AuthorityReachable: false));
        var failed = findings.Where(f => f.Status == PreflightStatus.Fail).ToDictionary(f => f.Check);
        Assert.Contains("wg-quick", failed["WireGuard"].Recovery);
        Assert.Contains("never disable certificate validation", failed["TLS"].Recovery);
        Assert.Contains("chmod 600", failed["Secret file"].Recovery);
        Assert.Contains("Codex or Claude", failed["Provider"].Recovery);
    }

    [Fact]
    public void Probes_outside_the_journey_are_not_applicable_rather_than_passed()
    {
        var findings = PreflightPolicy.Evaluate(InstallationJourney.AttachStudio, "native", new HostFacts(
            false, true, true, null, null, null, null, null, null, null, null, null, null));

        Assert.DoesNotContain(findings, finding => finding.Check == "Docker");
        Assert.Equal(PreflightStatus.NotApplicable, findings.Single(finding => finding.Check == "WireGuard").Status);
    }

    [Fact]
    public void Native_one_box_does_not_require_docker_or_hypervisor()
    {
        var findings = PreflightPolicy.Evaluate(InstallationJourney.OneBox, "native", new HostFacts(
            false, true, null, false, false, null, 40L << 30, null, null, null, null, null, null));
        Assert.DoesNotContain(findings, finding => finding.Check == "Docker");
        Assert.Equal(PreflightStatus.NotApplicable, findings.Single(f => f.Check == "VM / virtualization").Status);
        Assert.DoesNotContain(findings, finding => finding.Status == PreflightStatus.Fail);
    }

    [Fact]
    public void Windows_connector_does_not_require_hypervisor_or_twenty_gib()
    {
        var findings = PreflightPolicy.Evaluate(InstallationJourney.AttachStudio, "native", new HostFacts(
            true, true, false, false, false, false, 1L << 30, null, null, null, null, null, null));
        Assert.Equal(PreflightStatus.NotApplicable, findings.Single(f => f.Check == "VM / virtualization").Status);
        Assert.Equal(PreflightStatus.NotApplicable, findings.Single(f => f.Check == "Storage").Status);
        Assert.DoesNotContain(findings, finding => finding.Status == PreflightStatus.Fail);
    }

    [Theory]
    [InlineData("1.2.0-rc1", "1.2.0", -1)]
    [InlineData("1.2.0-rc2", "1.2.0-rc1", 1)]
    [InlineData("1.2.0+build2", "1.2.0+build1", 0)]
    public void Semantic_version_comparison_accepts_prerelease_tags(string left, string right, int sign)
        => Assert.Equal(sign, Math.Sign(ManifestPolicy.CompareVersions(left, right)));

    [Fact]
    public async Task Product_setup_reconciles_delegated_install_and_preserves_identity_until_purge()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var plan = new ProductPlan(ProductProfile.Delegated, "agent-host", "native", []);
            var first = await ProductSetup.ReconcileManifestAsync(root, "install", plan,
                InstallationJourney.JoinHost, "1.2.0", null, false);
            Assert.NotNull(first);
            await ProductSetup.FinishManifestAsync(root, first, false, false,
                InstallationManifest.PhaseComplete, "host-enrolled");
            var repeated = await ProductSetup.ReconcileManifestAsync(root, "install", plan,
                InstallationJourney.JoinHost, "1.2.0", null, false);
            Assert.Equal(first.InstallationId, repeated!.InstallationId);
            Assert.Equal(first.Principals, repeated.Principals);
            await ProductSetup.FinishManifestAsync(root, repeated, false, false,
                InstallationManifest.PhaseComplete, "host-enrolled");

            var uninstall = await ProductSetup.ReconcileManifestAsync(root, "uninstall", plan,
                InstallationJourney.JoinHost, "1.2.0", null, false);
            await ProductSetup.FinishManifestAsync(root, uninstall, false, false,
                InstallationManifest.PhaseUninstalled, "uninstalled");
            Assert.Equal(first.InstallationId, (await ManifestStore.ReadAsync(root))!.InstallationId);
            var reinstall = await ProductSetup.ReconcileManifestAsync(root, "install", plan,
                InstallationJourney.JoinHost, "1.2.0", null, false);
            Assert.Equal(first.InstallationId, reinstall!.InstallationId);
            await ProductSetup.FinishManifestAsync(root, reinstall, false, true,
                InstallationManifest.PhaseUninstalled, "uninstalled");
            Assert.Null(await ManifestStore.ReadAsync(root));
            Assert.False(File.Exists(Path.Combine(root, InstallationManifest.CheckpointFileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Delegated_uninstall_records_uninstalled_and_purge_removes_data_and_metadata()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory().FullName;
        var names = new[] { "AGENT_SETUP_HOST_OPT", "AGENT_SETUP_HOST_CONFIG",
            "AGENT_SETUP_HOST_STATE", "AGENT_SETUP_SYSTEMD_ROOT", "AGENT_SETUP_SYSTEMCTL" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            var config = Path.Combine(root, "config");
            var state = Path.Combine(root, "state");
            var units = Path.Combine(root, "units");
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(units);
            await File.WriteAllTextAsync(Path.Combine(state, "task-data"), "preserve");
            await File.WriteAllTextAsync(Path.Combine(units, "agent-host.service"), "unit");
            var fakeSystemctl = Path.Combine(root, "systemctl");
            await File.WriteAllTextAsync(fakeSystemctl,
                "#!/bin/sh\n[ \"$1\" = is-active ] && exit 1\nexit 0\n");
            File.SetUnixFileMode(fakeSystemctl,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable(names[0], Path.Combine(root, "opt"));
            Environment.SetEnvironmentVariable(names[1], config);
            Environment.SetEnvironmentVariable(names[2], state);
            Environment.SetEnvironmentVariable(names[3], units);
            Environment.SetEnvironmentVariable(names[4], fakeSystemctl);
            var manifest = Manifest("1.2.0", InstallationManifest.PhaseComplete) with
            { Mode = "agent-host", Target = "native", Journey = "join-host" };
            await ManifestStore.WriteAsync(config, manifest);
            await ManifestStore.CheckpointAsync(config, manifest, "host-enrolled", "observed");

            await File.WriteAllTextAsync(Path.Combine(units, "agent-host-review.service"), "unit");
            await File.WriteAllTextAsync(fakeSystemctl, "#!/bin/sh\nexit 0\n");
            Assert.Equal(1, await ProductSetup.RunAsync(["uninstall", "--journey", "join-host",
                "--install-dir", config]));
            Assert.Equal(InstallationManifest.PhaseComplete,
                (await ManifestStore.ReadAsync(config))!.Phase);
            Assert.True(File.Exists(Path.Combine(units, "agent-host-review.service")));
            await File.WriteAllTextAsync(fakeSystemctl,
                "#!/bin/sh\n[ \"$1\" = is-active ] && exit 1\nexit 0\n");

            Assert.Equal(0, await ProductSetup.RunAsync(["uninstall", "--journey", "join-host",
                "--install-dir", config]));
            Assert.Equal(InstallationManifest.PhaseUninstalled,
                (await ManifestStore.ReadAsync(config))!.Phase);
            Assert.True(File.Exists(Path.Combine(state, "task-data")));
            Assert.True(File.Exists(Path.Combine(config, InstallationManifest.CheckpointFileName)));

            Assert.Equal(0, await ProductSetup.RunAsync(["uninstall", "--journey", "join-host",
                "--install-dir", config, "--purge"]));
            Assert.False(Directory.Exists(config));
            Assert.False(Directory.Exists(state));
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unreachable_authority_is_a_connectivity_failure_with_tls_not_applicable()
    {
        var findings = PreflightPolicy.Evaluate(InstallationJourney.AttachStudio, "native", new HostFacts(
            true, true, null, null, null, null, null, true, null, null, null, null, null,
            AuthorityReachable: false));
        Assert.Equal(PreflightStatus.Fail, findings.Single(f => f.Check == "Connectivity").Status);
        Assert.Equal(PreflightStatus.NotApplicable, findings.Single(f => f.Check == "TLS").Status);
    }

    [Fact]
    public void Windows_workstation_execution_has_no_native_exemption()
    {
        var note = PreflightPolicy.ExecutionPlatformNote(InstallationJourney.OneBox, windows: true);

        Assert.Contains("linux-x64", note);
        Assert.Contains("not exempted from AGT-W51", note);
        Assert.Null(PreflightPolicy.ExecutionPlatformNote(InstallationJourney.OneBox, windows: false));
        Assert.Null(PreflightPolicy.ExecutionPlatformNote(InstallationJourney.OneBox, windows: true, target: "native"));
    }

    [Fact]
    public void Invalid_credential_files_are_rejected()
    {
        var empty = Path.GetTempFileName();
        var open = Path.GetTempFileName();
        try
        {
            File.WriteAllText(open, "token");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(open, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

            Assert.Throws<InvalidOperationException>(() => SetupSecrets.RequireProtected(empty, "Token file"));
            Assert.Throws<FileNotFoundException>(
                () => SetupSecrets.RequireProtected(empty + ".missing", "Token file"));
            if (!OperatingSystem.IsWindows())
                Assert.Contains("chmod 600", Assert.Throws<InvalidOperationException>(
                    () => SetupSecrets.RequireProtected(open, "Token file")).Message);
        }
        finally
        {
            File.Delete(empty);
            File.Delete(open);
        }
    }

    [Fact]
    public void Windows_token_with_world_read_acl_is_rejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "token");
            var info = new FileInfo(file);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadData, AccessControlType.Allow));
            info.SetAccessControl(acl);
            Assert.False(SetupSecrets.IsProtected(file));
            Assert.Throws<InvalidOperationException>(() => SetupSecrets.RequireProtected(file, "Token file"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Secret_file_symlink_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(root, "token");
            File.WriteAllText(target, "token");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var link = Path.Combine(root, "link");
            File.CreateSymbolicLink(link, target);
            Assert.False(SetupSecrets.IsProtected(link));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Relocation_requires_verified_recovery_and_preserves_source_identity()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var source = Manifest("1.2.0", InstallationManifest.PhaseComplete);
            var sourcePath = Path.Combine(root, "source.json");
            await File.WriteAllTextAsync(sourcePath, System.Text.Json.JsonSerializer.Serialize(source));
            var destination = Path.Combine(root, "destination");
            await ManifestStore.WriteAsync(destination, source with { Mode = "control-plane", Journey = "relocate-authority" });
            var set = Path.Combine(root, "backup-1");
            Directory.CreateDirectory(set);
            await File.WriteAllTextAsync(Path.Combine(set, "snapshot.db"), "snapshot");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("snapshot")));
            var setHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"snapshot.db:8:{hash.ToLowerInvariant()}\n")));
            await File.WriteAllTextAsync(Path.Combine(set, "inventory.json"),
                $$"""{"setSha256":"{{setHash}}","files":[{"relativePath":"snapshot.db","size":8,"sha256":"{{hash}}"}]}""");
            await File.WriteAllTextAsync(Path.Combine(set, "complete.json"),
                $$"""{"setSha256":"{{setHash}}"}""");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RelocationGate.VerifyAsync(sourcePath, set, destination, true));
            await File.WriteAllTextAsync(set + ".rehearsal.json",
                $$"""{"backupId":"backup-1","setSha256":"{{setHash}}","installationId":"inst_original","verified":true,"restoredIntoEmptyTarget":true}""");
            Assert.Equal("inst_original", (await RelocationGate.VerifyAsync(sourcePath, set, destination, true)).InstallationId);
            var relocated = RelocationGate.RelocatedManifest(source, "docker");
            Assert.Equal(("inst_original", "control-plane", "relocate-authority"),
                (relocated.InstallationId, relocated.Mode, relocated.Journey));
            Assert.Equal(source.Principals, relocated.Principals);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RelocationGate.VerifyAsync(sourcePath, set, destination, false));
            await File.WriteAllTextAsync(sourcePath, System.Text.Json.JsonSerializer.Serialize(
                source with { Phase = InstallationManifest.PhaseInstalling }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RelocationGate.VerifyAsync(sourcePath, set, destination, true));
            await File.WriteAllTextAsync(sourcePath, System.Text.Json.JsonSerializer.Serialize(source));
            await ManifestStore.WriteAsync(destination, source with { InstallationId = "different" });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RelocationGate.VerifyAsync(sourcePath, set, destination, true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Relocation_verifies_target_before_restoring_with_management_token()
    {
        var token = Path.GetTempFileName();
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var set = Path.Combine(root, "backup-1");
            Directory.CreateDirectory(set);
            await File.WriteAllTextAsync(Path.Combine(set, "inventory.json"), "{\"setSha256\":\"abc123\"}");
            await File.WriteAllTextAsync(token, "private-token");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(token, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var calls = new List<string>();
            var identity = new string('a', 64);
            using var http = new HttpClient(new RecordingHandler(request =>
            {
                calls.Add(request.RequestUri!.AbsolutePath);
                Assert.Equal("private-token", request.Headers.Authorization!.Parameter);
                var answer = calls.Count == 1
                    ? "{\"backupId\":\"backup-1\",\"verified\":true,\"identitySha256\":\"" + identity + "\",\"summary\":{\"setSha256\":\"abc123\"}}"
                    : "{\"backupId\":\"backup-1\",\"restored\":true,\"identitySha256\":\"" + identity + "\"}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(answer),
                };
            }));
            Assert.Equal(identity, await RelocationGate.RestoreAsync("https://authority.wg.internal", token,
                set, http));
            Assert.Equal([
                "/api/v1/management/backups/full/backup-1/verify",
                "/api/v1/management/backups/full/backup-1/restore",
            ], calls);
            var rejectedCalls = 0;
            using var wrongSet = new HttpClient(new RecordingHandler(_ =>
            {
                rejectedCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"backupId\":\"backup-1\",\"verified\":true,\"summary\":{\"setSha256\":\"different\"}}"),
                };
            }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RelocationGate.RestoreAsync("https://authority.wg.internal", token, set, wrongSet));
            Assert.Equal(1, rejectedCalls);
            var mismatchCalls = 0;
            using var mismatchedRestore = new HttpClient(new RecordingHandler(_ =>
            {
                mismatchCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(mismatchCalls == 1
                        ? "{\"backupId\":\"backup-1\",\"verified\":true,\"identitySha256\":\"" + identity + "\",\"summary\":{\"setSha256\":\"abc123\"}}"
                        : "{\"backupId\":\"backup-1\",\"restored\":true,\"identitySha256\":\"different\"}"),
                };
            }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RelocationGate.RestoreAsync("https://authority.wg.internal", token, set, mismatchedRestore));
            Assert.Equal(2, mismatchCalls);
        }
        finally
        {
            File.Delete(token);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Relocation_checks_destination_manifest_again_after_restore()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var source = Manifest("1.2.0", InstallationManifest.PhaseComplete);
            await ManifestStore.WriteAsync(root, source);
            Assert.Equal(source.InstallationId,
                (await RelocationGate.VerifyManifestAfterRestoreAsync(source, root)).InstallationId);
            await ManifestStore.WriteAsync(root, source with { InstallationId = "new-authority" });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RelocationGate.VerifyManifestAfterRestoreAsync(source, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }

    [Fact]
    public void Unattended_prompts_never_invent_required_values()
        => Assert.Throws<InvalidOperationException>(
            () => new ConsolePrompter(nonInteractive: true).Ask("Remote Task Server URL"));

    [Fact]
    public void Unattended_secret_prompts_are_refused()
        => Assert.Throws<InvalidOperationException>(
            () => new ConsolePrompter(nonInteractive: true).Secret("Studio token"));

    [Fact]
    public void Interactive_prompts_use_the_answer_and_the_default()
    {
        var input = Console.In;
        var output = Console.Out;
        try
        {
            Console.SetIn(new StringReader("\n\nhttps://tasks.wg.internal\ny\n"));
            Console.SetOut(TextWriter.Null);
            var prompter = new ConsolePrompter(nonInteractive: false);

            Assert.Equal("4011", prompter.Ask("Studio browser port", "4011"));
            Assert.Equal("https://tasks.wg.internal", prompter.Ask("Remote Task Server URL"));
            Assert.True(prompter.Confirm("Open Studio"));
        }
        finally
        {
            Console.SetIn(input);
            Console.SetOut(output);
        }
    }

    private static ManifestDecision Decide(InstallationManifest? existing, ManifestRequest request)
        => ManifestPolicy.Decide(existing, request, () => "inst_new", Now);

    private static ManifestRequest Request(string version, string verb = "install")
        => new("one-box", "studio", "docker", version, verb, Principals);

    private static InstallationManifest Manifest(string version, string phase)
        => new(InstallationManifest.CurrentSchema, "inst_original", "one-box", "studio", "docker", version, phase,
            ["task-server-admin"], null, "runner-coding", 2, 2, Now.AddDays(-3), Now.AddDays(-1));

    private static void AssertRejected(ManifestDecision decision, string reason)
    {
        Assert.Equal(ManifestAction.Reject, decision.Action);
        Assert.Contains(reason, decision.Reason);
    }
}
