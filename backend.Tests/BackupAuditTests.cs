using System.Text.Json;
using AgentStudio.Management;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3737: backup create, upload, verify, retention, and the offline
/// decrypt and restore write control plane audit records with actor and
/// outcome; the operator backup view keeps host paths out of its error text.
/// </summary>
public sealed class BackupAuditTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private readonly BackupFixture _fixture = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ControlPlaneAuditLog _audit;

    public BackupAuditTests() => _audit = new ControlPlaneAuditLog(Path.Combine(_fixture.Root, "audit"), _clock);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Scheduled_tick_records_create_upload_and_verify_with_the_schedule_actor()
    {
        var target = new FlakyTarget { Failures = 1 };
        var service = Service(target);

        await service.RunDueAsync(CancellationToken.None);

        var record = Assert.Single(service.List());
        var entries = Backups();
        Assert.Equal(
            [(BackupAudit.Create, BackupAudit.Succeeded), (BackupAudit.Upload, BackupAudit.Failed), (BackupAudit.Verify, BackupAudit.Succeeded)],
            entries.Select(entry => (entry.Operation, entry.Outcome)).ToArray());
        Assert.All(entries, entry =>
        {
            Assert.Equal(ControlPlaneAuditStreams.Backup, entry.Stream);
            Assert.Equal(BackupAudit.Schedule, entry.Actor);
            Assert.Equal(record.Id, entry.Target);
            Assert.Equal(BackupAudit.Schedule, entry.Details!.Value.GetProperty("trigger").GetString());
        });
        Assert.Contains("target offline", Summary(entries[1]));

        _clock.Advance(TimeSpan.FromMinutes(16));
        await service.RunDueAsync(CancellationToken.None);
        var retried = Backups()[^1];
        Assert.Equal((BackupAudit.Upload, BackupAudit.Succeeded, BackupAudit.Schedule), (retried.Operation, retried.Outcome, retried.Actor));
        Assert.True(_audit.VerifyChain().Ok);
    }

    [Fact]
    public async Task Manual_runs_record_the_principal_and_retention_names_every_removed_archive()
    {
        var service = Service(null);
        var first = await service.CreateAsync("manual:usr_owner", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await service.CreateAsync("manual:usr_owner", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.CreateAsync("manual:usr_owner", CancellationToken.None);

        Assert.Equal((2, 0), service.ApplyRetention(1, dryRun: true, "manual:usr_owner"));
        Assert.Equal(3, Backups().Count);
        Assert.Equal((2, 2), service.ApplyRetention(1, dryRun: false, "manual:usr_owner"));

        var retention = Backups()[^1];
        Assert.Equal((BackupAudit.Retention, BackupAudit.Succeeded, "usr_owner"), (retention.Operation, retention.Outcome, retention.Actor));
        var facts = retention.Details!.Value.GetProperty("facts");
        Assert.Equal(1, facts.GetProperty("keep").GetInt32());
        Assert.Equal(12, facts.GetProperty("configured").GetInt32());
        Assert.Equal([second.Id, first.Id], facts.GetProperty("removed").EnumerateArray().Select(item => item.GetString()).ToArray());

        var unconfigured = Service(null, withKey: false);
        await Assert.ThrowsAsync<BackupException>(() => unconfigured.CreateAsync("manual:usr_operator", CancellationToken.None));
        var failed = Backups()[^1];
        Assert.Equal((BackupAudit.Create, BackupAudit.Failed, "usr_operator"), (failed.Operation, failed.Outcome, failed.Actor));
        Assert.Equal("backup-not-configured", failed.Details!.Value.GetProperty("facts").GetProperty("code").GetString());

        await Assert.ThrowsAsync<BackupException>(() => service.VerifyAsync("studio-backup-missing", "manual:usr_owner", CancellationToken.None));
        Assert.Equal((BackupAudit.Verify, BackupAudit.Failed, "studio-backup-missing"),
            (Backups()[^1].Operation, Backups()[^1].Outcome, Backups()[^1].Target));
    }

    [Fact]
    public async Task Offline_decrypt_and_restore_append_to_the_workspace_chain_and_a_failed_restore_leaves_targets_empty()
    {
        var record = await Service(null).CreateAsync("test", CancellationToken.None);
        var archive = _fixture.Catalog.ArchivePath(record.Id);
        var operations = Path.Combine(_fixture.Root, "operations-workspace");
        Directory.CreateDirectory(operations);
        var none = new ConfigurationBuilder().Build();
        var error = new StringWriter();

        var plain = Path.Combine(_fixture.Root, "plain.tar.gz");
        Assert.Equal(0, StudioBackupCommand.Run(
            ["decrypt", "--key", _fixture.KeyFile, "--archive", archive, "--output", plain,
             "--actor", "offline:ops@host-a", "--audit-workspace", operations], new StringWriter(), error, none));
        Assert.Equal(1, StudioBackupCommand.Run(
            ["decrypt", "--key", _fixture.KeyFile, "--archive", archive, "--output", plain,
             "--actor", "offline:ops@host-a", "--audit-workspace", operations], new StringWriter(), error, none));
        var decrypts = Chain(operations);
        Assert.Equal([BackupAudit.Succeeded, BackupAudit.Failed], decrypts.Select(entry => entry.Outcome).ToArray());
        Assert.All(decrypts, entry => Assert.Equal(
            (ControlPlaneAuditStreams.Backup, BackupAudit.Decrypt, "offline:ops@host-a", record.FileName),
            (entry.Stream, entry.Operation, entry.Actor, entry.Target)));
        Assert.Equal("decrypt-output-exists", decrypts[1].Details!.Value.GetProperty("facts").GetProperty("code").GetString());

        var host = Path.Combine(_fixture.Root, "new-host");
        string[] args =
        [
            "restore", "--key", _fixture.KeyFile, "--archive", archive, "--no-clone",
            "--target", "workspace=" + Path.Combine(host, "workspace"), "--target", "home=" + Path.Combine(host, "home"),
            "--target", "projects=" + Path.Combine(host, "projects"),
        ];
        error = new StringWriter();
        Assert.Equal(0, StudioBackupCommand.Run(args, new StringWriter(), error, none));
        Assert.Equal("", error.ToString());
        var restored = Assert.Single(Chain(Path.Combine(host, "workspace")));
        Assert.Equal((BackupAudit.Restore, BackupAudit.Succeeded, "offline:" + Environment.UserName),
            (restored.Operation, restored.Outcome, restored.Actor));
        Assert.Contains($"Restored backup {record.Id}", restored.Details!.Value.GetProperty("summary").GetString());

        // A failed restore must not write into a target it has to leave empty, even when told to record there.
        error = new StringWriter();
        Assert.Equal(1, StudioBackupCommand.Run([.. args, "--audit-workspace", Path.Combine(host, "workspace")],
            new StringWriter(), error, none));
        Assert.Contains("was not recorded", error.ToString());
        Assert.Single(Chain(Path.Combine(host, "workspace")));

        Assert.Equal(1, StudioBackupCommand.Run([.. args, "--audit-workspace", operations], new StringWriter(), new StringWriter(), none));
        var failedRestore = Chain(operations)[^1];
        Assert.Equal((BackupAudit.Restore, BackupAudit.Failed), (failedRestore.Operation, failedRestore.Outcome));
        Assert.Equal("restore-target-not-empty", failedRestore.Details!.Value.GetProperty("facts").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("Could not open /var/lib/studio/backups/x.studio-backup: denied", "Could not open <path>: denied")]
    [InlineData(@"Access to the path 'C:\data\backups\x' is denied.", "Access to the path '<path>' is denied.")]
    [InlineData("The off-host copy failed: https://s3.example.test/bucket returned 403", "The off-host copy failed: https://s3.example.test/bucket returned 403")]
    [InlineData("Restored 3/4 files into staging", "Restored 3/4 files into staging")]
    [InlineData("rsync to backup@vault.example.test:/srv/off-host failed", "rsync to backup@vault.example.test:/srv/off-host failed")]
    public void Error_text_loses_absolute_host_paths(string text, string expected)
        => Assert.Equal(expected, BackupAudit.WithoutHostPaths(text));

    [Fact]
    public void Scoped_overview_drops_target_key_fingerprints_and_host_paths()
    {
        const string error = "Could not write /srv/studio/backups/.staging/spool: No space left on device";
        var overview = Service(null).Overview() with
        {
            KeyId = "bk_1234",
            SecretKeyId = "kek_5678",
            LastRun = new BackupRunRecord(Start.ToString("O"), "schedule", "failed", null, error, 12),
            LastVerification = new BackupVerificationOverview(Start.ToString("O"), "studio-backup-a", "failed", error),
            OffHost = new BackupOffHostOverview("directory", "/mnt/off-host", "failed", null, null, error),
            Warnings = [new BackupWarning("backup-failed", "danger", "The last backup run failed: " + error)],
        };

        var scoped = BackupEndpoints.Scoped(overview);

        var json = JsonSerializer.Serialize(scoped);
        Assert.DoesNotContain("/srv/", json);
        Assert.DoesNotContain("/mnt/", json);
        Assert.Null(scoped.KeyId);
        Assert.Null(scoped.SecretKeyId);
        Assert.Null(scoped.OffHost.Target);
        Assert.Equal("Could not write <path>: No space left on device", scoped.LastRun!.Error);
        Assert.Equal("The last backup run failed: Could not write <path>: No space left on device", scoped.Warnings[0].Message);
    }

    private StudioBackupService Service(IBackupTarget? target, bool withKey = true)
        => new(_fixture.Configuration(withKey), new BackupWritePause(), _clock, targets: _ => target, audit: _audit);

    private List<ControlPlaneAuditEntry> Backups()
        => _audit.ReadNewest(entry => entry.Stream == ControlPlaneAuditStreams.Backup, 100).Reverse().ToList();

    private static List<ControlPlaneAuditEntry> Chain(string workspace)
        => new ControlPlaneAuditLog(Path.Combine(workspace, ".security")).ReadNewest(_ => true, 100).Reverse().ToList();

    private static string Summary(ControlPlaneAuditEntry entry) => entry.Details!.Value.GetProperty("summary").GetString()!;

    private sealed class FlakyTarget : IBackupTarget
    {
        public int Failures { get; set; }
        public string Kind => "fake";
        public string Description => "fake://off-host";

        public Task UploadAsync(string localPath, string remoteName, CancellationToken cancellationToken)
        {
            if (Failures-- > 0) throw new BackupException("backup-target-upload-failed", "target offline");
            return Task.CompletedTask;
        }
    }
}
