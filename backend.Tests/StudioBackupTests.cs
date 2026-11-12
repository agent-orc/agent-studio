using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Management;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3574: scheduled installation backups. Covers the encryption envelope,
/// the schedule, retention, consistency while stores are written, the
/// off-host copy through a fake target, the stale warning, and a restore onto
/// an empty fake host.
/// </summary>
public sealed class StudioBackupTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private readonly BackupFixture _fixture = new();
    private readonly FakeTimeProvider _clock = new(Start);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Cipher_round_trips_many_chunks_and_rejects_tampering_truncation_and_the_wrong_key()
    {
        var key = new BackupKey(RandomNumberGenerator.GetBytes(32));
        var plain = RandomNumberGenerator.GetBytes(BackupCipher.ChunkSize * 2 + 77);
        var sealedBytes = Encrypt(key, plain);

        Assert.Equal(plain, Decrypt(key, sealedBytes));
        Assert.DoesNotContain(Convert.ToHexString(plain[..32]), Convert.ToHexString(sealedBytes));

        var tampered = sealedBytes.ToArray();
        tampered[BackupCipher.HeaderSize + 100] ^= 0x01;
        Assert.Equal("backup-corrupt", Assert.Throws<BackupException>(() => Decrypt(key, tampered)).Code);

        // Cut after the second full chunk: everything left authenticates, but the final chunk is missing.
        var chunk = 4 + BackupCipher.ChunkSize + 16;
        var truncated = sealedBytes[..(BackupCipher.HeaderSize + 2 * chunk)];
        Assert.Equal("backup-truncated", Assert.Throws<BackupException>(() => Decrypt(key, truncated)).Code);

        var appended = sealedBytes.Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.Equal("backup-corrupt", Assert.Throws<BackupException>(() => Decrypt(key, appended)).Code);

        var other = new BackupKey(RandomNumberGenerator.GetBytes(32));
        Assert.Equal("backup-key-mismatch", Assert.Throws<BackupException>(() => Decrypt(other, sealedBytes)).Code);
        Assert.Equal(key.Id, BackupCipher.ReadKeyId(new MemoryStream(sealedBytes)));
    }

    [Fact]
    public void Schedule_runs_every_interval_retries_failures_and_verifies_daily()
    {
        var options = _fixture.Options();
        Assert.True(BackupSchedulePolicy.BackupDue(Start, options, BackupState.Empty, null));

        var newest = Record("studio-backup-a", Start);
        var succeeded = BackupState.Empty with { LastRun = Run(Start, "succeeded") };
        Assert.False(BackupSchedulePolicy.BackupDue(Start.AddHours(5.9), options, succeeded, newest));
        Assert.True(BackupSchedulePolicy.BackupDue(Start.AddHours(6), options, succeeded, newest));

        var failed = BackupState.Empty with { LastRun = Run(Start.AddHours(6), "failed") };
        Assert.False(BackupSchedulePolicy.BackupDue(Start.AddHours(6.4), options, failed, newest));
        Assert.True(BackupSchedulePolicy.BackupDue(Start.AddHours(6.5), options, failed, newest));

        Assert.True(BackupSchedulePolicy.VerifyDue(Start, options, BackupState.Empty, newest));
        var verified = BackupState.Empty with { LastVerification = Run(Start, "verified", "studio-backup-a") };
        Assert.False(BackupSchedulePolicy.VerifyDue(Start.AddHours(23), options, verified, newest));
        Assert.True(BackupSchedulePolicy.VerifyDue(Start.AddHours(24), options, verified, newest));
        var verificationFailed = BackupState.Empty with { LastVerification = Run(Start, "failed", "studio-backup-a") };
        Assert.True(BackupSchedulePolicy.VerifyDue(Start.AddHours(1), options, verificationFailed, Record("studio-backup-b", Start.AddHours(1))));

        Assert.True(BackupSchedulePolicy.UploadDue(Start, null));
        var failedOnce = new BackupUploadRecord("t", "failed", null, Start.ToString("O"), "down", 1);
        Assert.False(BackupSchedulePolicy.UploadDue(Start.AddMinutes(14), failedOnce));
        Assert.True(BackupSchedulePolicy.UploadDue(Start.AddMinutes(15), failedOnce));
        Assert.False(BackupSchedulePolicy.UploadDue(Start.AddMinutes(29), failedOnce with { Attempts = 2 }));
        Assert.False(BackupSchedulePolicy.UploadDue(Start.AddDays(1), failedOnce with { State = "uploaded" }));

        var unconfigured = _fixture.Options(withKey: false);
        Assert.False(BackupSchedulePolicy.BackupDue(Start, unconfigured, BackupState.Empty, null));
    }

    [Fact]
    public void Default_local_state_sources_have_unique_names_and_configured_duplicates_are_rejected()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _fixture.Workspace,
            ["Backup:KeyFile"] = _fixture.KeyFile,
        }).Build();
        var sources = BackupOptions.From(configuration).Sources;
        Assert.Equal(sources.Count, sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (sources.Any(source => source.Name == "local-state-agentstudio") && OperatingSystem.IsLinux())
            Assert.Contains(sources, source => source.Name == "local-state-agentstudio-capitalized");

        var local = Path.Combine(_fixture.Root, "local-state");
        Directory.CreateDirectory(Path.Combine(local, "agentstudio"));
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateSymbolicLink(Path.Combine(local, "AgentStudio"), Path.Combine(local, "agentstudio"));
            var aliased = BackupOptions.DefaultSources(_fixture.Workspace, local);
            Assert.DoesNotContain(aliased, source => source.Name == "local-state-agentstudio-capitalized");
        }

        var duplicate = new[]
        {
            new BackupSource("same", _fixture.Home, false, false, []),
            new BackupSource("SAME", _fixture.Projects, false, false, []),
        };
        Assert.Equal("backup-source-duplicate",
            Assert.Throws<BackupException>(() => BackupOptions.ValidateNames(duplicate)).Code);

        var colliding = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _fixture.Workspace,
            ["Backup:KeyFile"] = _fixture.KeyFile,
            ["Backup:Sources:Workspace:Path"] = _fixture.Home,
        }).Build();
        Assert.Equal("backup-source-duplicate",
            Assert.Throws<BackupException>(() => BackupOptions.From(colliding)).Code);
    }

    [Fact]
    public async Task Scheduled_tick_creates_uploads_and_verifies_then_waits_for_the_interval()
    {
        var target = new FakeTarget();
        var service = _fixture.Service(_clock, target);

        await service.RunDueAsync(CancellationToken.None);

        var record = Assert.Single(service.List());
        Assert.Equal("verified", record.Verification?.Outcome);
        Assert.Equal("uploaded", record.Upload?.State);
        Assert.Equal([record.FileName, record.FileName + ".sha256"], target.Uploads.Select(upload => upload.Name).ToArray());
        Assert.Equal(File.ReadAllBytes(_fixture.Catalog.ArchivePath(record.Id)), target.Uploads[0].Bytes);

        _clock.Advance(TimeSpan.FromHours(1));
        await service.RunDueAsync(CancellationToken.None);
        Assert.Single(service.List());

        _clock.Advance(TimeSpan.FromHours(5));
        await service.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, service.List().Count);
        Assert.Equal("ok", service.Overview().State);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_archives()
    {
        var service = _fixture.Service(_clock, null, retention: 2);
        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            created.Add((await service.CreateAsync("test", CancellationToken.None)).Id);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(created.AsEnumerable().Reverse().Take(2), service.List().Select(record => record.Id));
        Assert.False(File.Exists(_fixture.Catalog.ArchivePath(created[0])));
        Assert.False(File.Exists(_fixture.Catalog.ChecksumPath(created[0])));

        var (matched, removed) = service.ApplyRetention(1, dryRun: true, "test");
        Assert.Equal((1, 0), (matched, removed));
        Assert.Equal(2, service.List().Count);
        Assert.Equal((1, 1), service.ApplyRetention(1, dryRun: false, "test"));
        Assert.Equal(created[2], Assert.Single(service.List()).Id);
    }

    [Fact]
    public async Task Archive_stays_consistent_while_json_journal_and_sqlite_stores_are_written()
    {
        var json = Path.Combine(_fixture.Workspace, ".metadata", "store.json");
        var journal = Path.Combine(_fixture.Workspace, ".metadata", "events.jsonl");
        var database = Path.Combine(_fixture.Workspace, ".metadata", "index.db");
        WriteAtomically(json, Version(0));
        File.WriteAllText(journal, "{\"n\":0}\n");
        using (var connection = OpenDatabase(database))
        {
            Execute(connection, "PRAGMA journal_mode=WAL; CREATE TABLE rows (n INTEGER NOT NULL);");
        }

        var service = _fixture.Service(_clock, null);
        var writes = 0;
        // Each capture is followed by a burst of writes from "another writer":
        // a replaced JSON store, a half-written journal line, and a SQLite insert.
        service.AfterCapture = _ =>
        {
            writes++;
            WriteAtomically(json, Version(writes));
            using (var append = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                append.Write(Encoding.UTF8.GetBytes($"{{\"n\":{writes}}}\n{{\"n\":"));
            using (var fix = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                fix.Write(Encoding.UTF8.GetBytes($"{writes}}}\n"));
            using var connection = OpenDatabase(database);
            Execute(connection, $"INSERT INTO rows (n) VALUES ({writes});");
        };
        // A long-running writer that keeps appending partial lines while the backup reads.
        using var stop = new CancellationTokenSource();
        var background = Task.Run(async () =>
        {
            var n = 1000;
            while (!stop.IsCancellationRequested)
            {
                using (var append = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    append.Write(Encoding.UTF8.GetBytes("{\"n\":"));
                    append.Flush();
                    await Task.Delay(1);
                    append.Write(Encoding.UTF8.GetBytes($"{n++}}}\n"));
                }
            }
        });

        BackupRecord record;
        try { record = await service.CreateAsync("test", CancellationToken.None); }
        finally
        {
            await stop.CancelAsync();
            await background;
        }

        Assert.True(writes > 0);
        var files = _fixture.ReadArchive(record.Id);
        var storedJson = JsonDocument.Parse(files["workspace/.metadata/store.json"]);
        Assert.True(storedJson.RootElement.GetProperty("version").GetInt32() >= 0);
        var lines = Encoding.UTF8.GetString(files["workspace/.metadata/events.jsonl"]).Split('\n');
        Assert.Equal("", lines[^1]);
        Assert.All(lines[..^1], line => JsonDocument.Parse(line));
        Assert.DoesNotContain(files.Keys, name => name.EndsWith("-wal", StringComparison.Ordinal) || name.EndsWith("-shm", StringComparison.Ordinal));

        var restoredDatabase = Path.Combine(_fixture.Root, "restored.db");
        File.WriteAllBytes(restoredDatabase, files["workspace/.metadata/index.db"]);
        using (var connection = OpenDatabase(restoredDatabase))
        {
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", check.ExecuteScalar());
        }
        Assert.True(record.Consistency.WritesHeld);
        Assert.Equal(1, record.Consistency.SqliteSnapshots);
        Assert.Equal(1, record.Consistency.JournalPrefixes);
    }

    [Fact]
    public async Task Write_pause_holds_mutations_during_the_read_and_waits_for_those_in_flight()
    {
        var pause = new BackupWritePause();
        var inFlight = await pause.EnterAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(inFlight);

        var holding = pause.HoldAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(holding.IsCompleted);
        Assert.True(pause.IsHeld);
        inFlight!.Dispose();
        var hold = await holding;
        Assert.True(hold.Drained);

        Assert.Null(await pause.EnterAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
        var waiting = pause.EnterAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        hold.Dispose();
        using var admitted = await waiting;
        Assert.NotNull(admitted);
        Assert.False(pause.IsHeld);
    }

    [Fact]
    public async Task Archive_is_encrypted_and_never_holds_key_material()
    {
        var kek = _fixture.CreateSecretKey();
        File.WriteAllText(Path.Combine(_fixture.Home, "copied-key.txt"), File.ReadAllText(_fixture.SecretKeyFile));
        File.WriteAllText(Path.Combine(_fixture.Home, "spare.kek"), "not even a key");
        File.WriteAllText(Path.Combine(_fixture.Home, "backup-key-copy"), File.ReadAllText(_fixture.KeyFile));
        var marker = "plaintext-marker-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(_fixture.Workspace, "card.md"), marker);

        var service = _fixture.Service(_clock, null, secretKey: kek);
        var record = await service.CreateAsync("test", CancellationToken.None);

        var bytes = File.ReadAllBytes(_fixture.Catalog.ArchivePath(record.Id));
        Assert.Equal(BackupCipher.Magic, bytes[..8]);
        Assert.DoesNotContain(marker, Encoding.Latin1.GetString(bytes));
        Assert.Equal(kek.Load(false).Id, record.SecretKeyId);
        var files = _fixture.ReadArchive(record.Id);
        Assert.Contains("workspace/card.md", files.Keys);
        Assert.DoesNotContain("home/copied-key.txt", files.Keys);
        Assert.DoesNotContain("home/spare.kek", files.Keys);
        Assert.DoesNotContain("home/backup-key-copy", files.Keys);
        var secretText = File.ReadAllText(_fixture.SecretKeyFile).Trim();
        Assert.All(files.Values, content => Assert.DoesNotContain(secretText, Encoding.UTF8.GetString(content)));
        Assert.Equal(3, record.Consistency.KeyFilesExcluded);
        Assert.Equal($"{record.Sha256}  {record.FileName}\n", File.ReadAllText(_fixture.Catalog.ChecksumPath(record.Id)));
    }

    [Fact]
    public async Task Backup_key_alone_opens_neither_authenticator_seed_nor_sealed_secret_value()
    {
        var configuration = _fixture.Configuration();
        var access = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock);
        var password = "backup test phrase " + Guid.NewGuid().ToString("N");
        var owner = access.Bootstrap(new BootstrapRequest("owner", password, "Owner"));
        var principal = access.AuthenticateSession(owner.SessionToken)!;
        var seed = access.BeginTotpEnrollment(principal);
        access.ConfirmTotpEnrollment(principal, SecondFactorCodes.CurrentTotp(seed, _clock.GetUtcNow().UtcDateTime));
        var secretValue = "sealed-value-" + Guid.NewGuid().ToString("N");
        var kek = _fixture.CreateSecretKey().Load(false);
        var sealedPath = Path.Combine(_fixture.Workspace, ".security", "secrets", "items", "test", "v000001.sealed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sealedPath)!);
        File.WriteAllText(sealedPath, JsonSerializer.Serialize(SecretEnvelope.Seal(kek, "test", 1, Encoding.UTF8.GetBytes(secretValue))));

        var record = await _fixture.Service(_clock, null, secretKey: _fixture.CreateSecretKey()).CreateAsync("test", CancellationToken.None);
        using (var encryptedArchive = File.OpenRead(_fixture.Catalog.ArchivePath(record.Id)))
        using (var decrypted = BackupCipher.Decrypt(encryptedArchive, BackupKey.Load(_fixture.KeyFile)))
        using (var gzip = new GZipStream(decrypted, CompressionMode.Decompress))
        using (var plainTar = new MemoryStream())
        {
            gzip.CopyTo(plainTar);
            var archiveText = Encoding.UTF8.GetString(plainTar.ToArray());
            Assert.DoesNotContain(seed, archiveText, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, archiveText, StringComparison.Ordinal);
        }
        var files = _fixture.ReadArchive(record.Id);
        Assert.Contains("workspace/.security/access-state.json", files.Keys);
        Assert.All(files.Values, bytes =>
        {
            var content = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(seed, content, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, content, StringComparison.Ordinal);
        });
        using var archive = File.OpenRead(_fixture.Catalog.ArchivePath(record.Id));
        var (_, contents) = BackupArchive.Read(archive, BackupKey.Load(_fixture.KeyFile), null, null, null);
        var servedContents = _fixture.Service(_clock, null).ReadContents(record.Id)!;
        Assert.Equal(contents.Files.Count, servedContents.Files.Count);
        Assert.Equal(contents.CredentialFiles!["totp-seeds-sealed"], servedContents.CredentialFiles!["totp-seeds-sealed"]);
        Assert.Equal(1, contents.CredentialFiles!["totp-seeds-sealed"]);
        Assert.Equal(1, contents.CredentialFiles["secret-store-values-sealed"]);
        Assert.Contains("sealed:v1:", Encoding.UTF8.GetString(files["workspace/.security/access-state.json"]));

        var restoredRoot = Path.Combine(_fixture.Root, "restored-auth");
        var restoredWorkspace = Path.Combine(restoredRoot, "workspace");
        var restoredState = Path.Combine(restoredWorkspace, ".security", "access-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(restoredState)!);
        File.WriteAllBytes(restoredState, files["workspace/.security/access-state.json"]);
        var restoredKey = Path.Combine(restoredRoot, "secrets.kek");
        File.Copy(_fixture.SecretKeyFile, restoredKey);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(restoredKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var restoredConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = restoredWorkspace, ["Secrets:KeyFile"] = restoredKey
        }).Build();
        _clock.Advance(TimeSpan.FromSeconds(30));
        var restoredAccess = new AccessSecurityStore(restoredConfig, NullLogger<AccessSecurityStore>.Instance, _clock);
        var restoredLogin = restoredAccess.Login("owner", password, "restored", SecondFactorCodes.CurrentTotp(seed, _clock.GetUtcNow().UtcDateTime),
            requireSecondFactor: true);
        Assert.False(restoredAccess.AuthenticateSession(restoredLogin.SessionToken)!.Session.SecondFactorEnrollmentOnly);
    }

    [Fact]
    public void Credential_inventory_counts_files_per_class_including_shared_access_state()
    {
        var names = new[]
        {
            "workspace/.security/access-state.json", "workspace/.security/secrets/items/a/v000001.sealed.json",
            "stack/.env", "stack/backup-target/ssh.key", "home/.ssh/id_ed25519",
            "home/.claude/credentials.json", "home/.local/share/AgentStudio/github-app/git-credentials",
            "caddy-data/caddy/acme/acme.json", "host-certificates/host.key", "origin-pull/ca.key"
        };
        var files = names.Select(name => new BackupContentEntry(name, 1, "hash", "Stable"));
        var counts = BackupCredentialInventory.Count(files);
        Assert.Equal(11, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task Backup_refuses_a_legacy_access_state_snapshot_with_a_plaintext_seed()
    {
        var security = Path.Combine(_fixture.Workspace, ".security");
        Directory.CreateDirectory(security);
        var seed = SecondFactorCodes.NewTotpSecret();
        File.WriteAllText(Path.Combine(security, "access-state.v0.old.json"),
            JsonSerializer.Serialize(new { Users = new[] { new { TotpSecret = seed } } }));
        var error = await Assert.ThrowsAsync<BackupException>(() =>
            _fixture.Service(_clock, null).CreateAsync("test", CancellationToken.None));
        Assert.Equal("backup-plaintext-factor", error.Code);
        Assert.Empty(_fixture.Catalog.List());
    }

    [Fact]
    public async Task Create_waits_for_archive_and_checksum_to_reach_configured_target()
    {
        var target = new FakeTarget();
        var service = _fixture.Service(_clock, target);

        var record = await service.CreateAsync("manual:operator", CancellationToken.None);

        Assert.Equal("uploaded", record.Upload?.State);
        Assert.Equal("uploaded", service.List().Single().Upload?.State);
        Assert.Equal([record.FileName, record.FileName + ".sha256"], target.Uploads.Select(upload => upload.Name).ToArray());
        Assert.Equal(File.ReadAllBytes(_fixture.Catalog.ArchivePath(record.Id)), target.Uploads[0].Bytes);
        Assert.Equal(File.ReadAllBytes(_fixture.Catalog.ChecksumPath(record.Id)), target.Uploads[1].Bytes);
    }

    [Fact]
    public async Task Upload_failure_is_recorded_retried_with_backoff_and_then_succeeds()
    {
        var target = new FakeTarget { Failures = 1 };
        var service = _fixture.Service(_clock, target);
        var record = await service.CreateAsync("test", CancellationToken.None);
        Assert.Equal("failed", record.Upload?.State);
        var failed = Assert.Single(service.List()).Upload!;
        Assert.Equal(("failed", 1), (failed.State, failed.Attempts));
        Assert.Contains("target offline", failed.Error);
        var overview = service.Overview();
        Assert.Equal("failed", overview.OffHost.State);
        Assert.Contains(overview.Warnings, warning => warning.Code == "offhost-failed");
        Assert.Equal("failing", overview.State);

        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(0, await service.UploadPendingAsync(force: false, "test", CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await service.UploadPendingAsync(force: false, "test", CancellationToken.None));
        Assert.Equal("uploaded", Assert.Single(service.List()).Upload?.State);
        Assert.Equal("current", service.Overview().OffHost.State);
        Assert.Equal(record.FileName, target.Uploads[0].Name);
    }

    [Fact]
    public async Task Pending_upload_does_not_hold_the_local_backup_gate()
    {
        var target = new BlockingTarget();
        var service = _fixture.Service(_clock, target);
        var upload = service.CreateAsync("test", CancellationToken.None);
        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(service.IsRunning);
        try
        {
            var id = Assert.Single(service.List()).Id;
            var verification = await service.VerifyAsync(id, "test", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("verified", verification.Outcome);
        }
        finally { target.Release.TrySetResult(); }
        await upload;
    }

    [Fact]
    public async Task Directory_target_refuses_a_missing_mount_and_renames_complete_copies()
    {
        var source = Path.Combine(_fixture.Root, "archive.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var missing = new DirectoryBackupTarget(Path.Combine(_fixture.Root, "not-mounted"));
        Assert.Equal("backup-target-unreachable",
            (await Assert.ThrowsAsync<BackupException>(() => missing.UploadAsync(source, "a.sbk", CancellationToken.None))).Code);

        Directory.CreateDirectory(_fixture.OffHost);
        var target = new DirectoryBackupTarget(_fixture.OffHost);
        await target.UploadAsync(source, "a.sbk", CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_fixture.OffHost, "a.sbk")));
        Assert.False(File.Exists(Path.Combine(_fixture.OffHost, "a.sbk.partial")));
        await Assert.ThrowsAsync<BackupException>(() => target.UploadAsync(source, "../escape", CancellationToken.None));
    }

    [Fact]
    public async Task Sftp_target_pins_the_host_key_and_uploads_under_a_temporary_name()
    {
        var identity = Path.Combine(_fixture.Root, "backup-ssh.key");
        var knownHosts = Path.Combine(_fixture.Root, "backup-known-hosts");
        File.WriteAllText(identity, "fixture");
        File.WriteAllText(knownHosts, "fixture");
        var runner = new FakeProcessRunner();
        var target = new SftpBackupTarget(new BackupTargetOptions("sftp", Host: "backup.example", Port: 2222, User: "studio",
            RemotePath: "/srv/backups", IdentityFile: identity, KnownHostsFile: knownHosts), runner);

        await target.UploadAsync("/data/backups/x.sbk", "x.sbk", CancellationToken.None);

        var start = Assert.Single(runner.Starts);
        Assert.Equal("sftp", start.FileName);
        Assert.True(start.CreateNoWindow);
        Assert.Contains("StrictHostKeyChecking=yes", start.ArgumentList);
        Assert.Contains("UserKnownHostsFile=" + knownHosts, start.ArgumentList);
        Assert.Contains("BatchMode=yes", start.ArgumentList);
        Assert.Equal("studio@backup.example", start.ArgumentList[^1]);
        Assert.Equal("-rm \"/srv/backups/x.sbk.partial\"\nput \"/data/backups/x.sbk\" \"/srv/backups/x.sbk.partial\"\n"
                     + "rename \"/srv/backups/x.sbk.partial\" \"/srv/backups/x.sbk\"\n", runner.Inputs[0]);

        runner.ExitCode = 1;
        Assert.Equal("backup-target-upload-failed",
            (await Assert.ThrowsAsync<BackupException>(() => target.UploadAsync("/data/backups/x.sbk", "x.sbk", CancellationToken.None))).Code);

        var unpinned = new SftpBackupTarget(new BackupTargetOptions("sftp", Host: "h", User: "u", IdentityFile: identity), runner);
        Assert.Equal("backup-target-credentials-missing",
            (await Assert.ThrowsAsync<BackupException>(() => unpinned.UploadAsync("/a", "x.sbk", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task S3_target_signs_a_single_put_with_the_payload_hash()
    {
        var accessFile = Path.Combine(_fixture.Root, "s3-access");
        var secretFile = Path.Combine(_fixture.Root, "s3-secret");
        var access = "fixture-access-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(accessFile, access);
        File.WriteAllText(secretFile, "fixture-secret-" + Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(_fixture.Root, "archive.sbk");
        File.WriteAllBytes(archive, Encoding.UTF8.GetBytes("ciphertext"));
        var handler = new CapturingHandler();
        var target = new S3BackupTarget(new BackupTargetOptions("s3", Endpoint: "https://s3.example:9000", Region: "eu-central-1",
            Bucket: "studio", Prefix: "/live/", AccessKeyIdFile: accessFile, SecretAccessKeyFile: secretFile),
            () => new HttpClient(handler), _clock);

        await target.UploadAsync(archive, "studio-backup-x.tar.gz.sbk", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://s3.example:9000/studio/live/studio-backup-x.tar.gz.sbk", request.Uri);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ciphertext"))), request.Headers["x-amz-content-sha256"]);
        Assert.Equal("20261109T060000Z", request.Headers["x-amz-date"]);
        Assert.Matches($"^AWS4-HMAC-SHA256 Credential={access}/20261109/eu-central-1/s3/aws4_request, "
                       + "SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=[0-9a-f]{64}$", request.Headers["Authorization"]);
        Assert.Equal("ciphertext", request.Body);

        handler.Status = System.Net.HttpStatusCode.Forbidden;
        Assert.Equal("backup-target-upload-failed",
            (await Assert.ThrowsAsync<BackupException>(() => target.UploadAsync(archive, "y.sbk", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task S3_target_times_out_after_upload_when_response_headers_stall()
    {
        var accessFile = Path.Combine(_fixture.Root, "s3-access-timeout");
        var secretFile = Path.Combine(_fixture.Root, "s3-secret-timeout");
        File.WriteAllText(accessFile, "fixture-access-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(secretFile, "fixture-secret-" + Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(_fixture.Root, "timeout.sbk");
        File.WriteAllBytes(archive, [1, 2, 3]);
        var target = new S3BackupTarget(new BackupTargetOptions("s3", Endpoint: "https://s3.example.test",
            Bucket: "generated-fixture", AccessKeyIdFile: accessFile, SecretAccessKeyFile: secretFile),
            () => new HttpClient(new StalledHeadersHandler()) { Timeout = Timeout.InfiniteTimeSpan }, _clock)
        { HeadersTimeoutOverride = TimeSpan.FromMilliseconds(50) };

        var failure = await Assert.ThrowsAsync<BackupException>(() => target.UploadAsync(archive, "x.sbk", CancellationToken.None));
        Assert.Equal("backup-target-upload-failed", failure.Code);
        Assert.Contains("timed out", failure.Message);
        Assert.True(S3BackupTarget.UploadTimeout(64L * 1024 * 1024) > S3BackupTarget.UploadTimeout(1));

        var service = _fixture.Service(_clock, target);
        await service.CreateAsync("test", CancellationToken.None);
        Assert.Equal(0, await service.UploadPendingAsync(true, "test", CancellationToken.None));
        var upload = Assert.Single(service.List()).Upload;
        Assert.Equal("failed", upload?.State);
        Assert.Contains("timed out", upload?.Error);
    }

    [Fact]
    public async Task Commit_during_git_snapshot_walk_restores_a_connected_repository()
    {
        CheckoutFileFixture.Git(_fixture.Workspace, "init", "-q", "-b", "main");
        Commit(_fixture.Workspace, "before backup");
        var original = CheckoutFileFixture.Git(_fixture.Workspace, "rev-parse", "HEAD").Trim();
        var service = _fixture.Service(_clock, null);
        var committed = false;
        service.AfterCapture = path =>
        {
            if (committed || !path.Contains("/git-", StringComparison.Ordinal)
                || !path.Contains("/objects/", StringComparison.Ordinal)) return;
            committed = true;
            File.WriteAllText(Path.Combine(_fixture.Workspace, "later.txt"), "later");
            Commit(_fixture.Workspace, "during walk");
        };

        var record = await service.CreateAsync("test", CancellationToken.None);
        Assert.True(committed);
        Assert.Equal("verified", (await service.VerifyAsync(record.Id, "test", CancellationToken.None)).Outcome);
        var restored = Path.Combine(_fixture.Root, "git-restore");
        BackupRestore.Restore(_fixture.Catalog.ArchivePath(record.Id), BackupKey.Load(_fixture.KeyFile),
            new BackupRestorePlan(new Dictionary<string, string>(), false, restored));
        Assert.Equal(original, CheckoutFileFixture.Git(Path.Combine(restored, "workspace"), "rev-parse", "HEAD").Trim());
        _ = CheckoutFileFixture.Git(Path.Combine(restored, "workspace"), "fsck", "--connectivity-only");
    }

    [Fact]
    public async Task Restore_verification_rejects_a_repository_with_a_missing_git_object()
    {
        CheckoutFileFixture.Git(_fixture.Workspace, "init", "-q", "-b", "main");
        Commit(_fixture.Workspace, "before backup");
        var service = _fixture.Service(_clock, null);
        var changed = false;
        service.AfterCapture = path =>
        {
            var objects = path.IndexOf("/objects/", StringComparison.Ordinal);
            if (changed || objects < 0 || !path.Contains("/git-", StringComparison.Ordinal)) return;
            changed = true;
            var badRef = Path.Combine(path[..objects], "refs", "heads", "broken");
            Directory.CreateDirectory(Path.GetDirectoryName(badRef)!);
            File.WriteAllText(badRef, new string('0', 40) + "\n");
        };

        var record = await service.CreateAsync("test", CancellationToken.None);
        Assert.True(changed);
        var verification = await service.VerifyAsync(record.Id, "test", CancellationToken.None);
        Assert.Equal("failed", verification.Outcome);
        Assert.Contains("git fsck", verification.Detail);
    }

    [Fact]
    public async Task Overview_warns_when_the_newest_good_backup_is_older_than_the_threshold()
    {
        var service = _fixture.Service(_clock, new FakeTarget());
        await service.RunDueAsync(CancellationToken.None);
        Assert.Equal("ok", service.Overview().State);

        _clock.Advance(TimeSpan.FromHours(11));
        var fresh = service.Overview();
        Assert.False(fresh.Stale);
        Assert.Equal(11, fresh.LastGoodAgeHours);

        _clock.Advance(TimeSpan.FromHours(2));
        var stale = service.Overview();
        Assert.True(stale.Stale);
        Assert.Equal("failing", stale.State);
        var warning = Assert.Single(stale.Warnings, item => item.Code == "backup-stale");
        Assert.Equal("danger", warning.Severity);
        Assert.Contains("13 hours old", warning.Message);
    }

    [Fact]
    public async Task Overview_shows_the_restore_receipt_unknown_off_host_archives_and_the_verification_scope()
    {
        // The lost host's archives are at the off-host target; this host's catalog is empty.
        Directory.CreateDirectory(_fixture.OffHost);
        var lost = "studio-backup-20261108T000000Z-111111";
        File.WriteAllBytes(Path.Combine(_fixture.OffHost, lost + BackupCatalog.ArchiveSuffix), [1, 2, 3]);
        File.WriteAllText(Path.Combine(_fixture.OffHost, lost + BackupCatalog.ArchiveSuffix + ".sha256"), "x");
        File.WriteAllBytes(Path.Combine(_fixture.OffHost, "studio-backup-20261108T060000Z-222222" + BackupCatalog.ArchiveSuffix + ".partial"), [1]);
        BackupRestoreReceipts.Write(_fixture.Workspace, new BackupRestoreReceipt(
            lost, lost + BackupCatalog.ArchiveSuffix, new string('e', 64), "2026-11-08T00:00:00.0000000+00:00", "host-a", "1.0.0",
            Start.ToString("O"), "owner@host-b", "a1b2c3d4e5f60718", "0011223344556677", "0011223344556677", 10, 1, []));
        var service = _fixture.Service(_clock, new DirectoryBackupTarget(_fixture.OffHost));

        var before = service.Overview();
        Assert.Equal(lost, before.LastRestore?.BackupId);
        Assert.Equal("owner@host-b", before.LastRestore?.RestoredBy);
        Assert.Contains(before.Warnings, warning => warning.Code == "backup-missing" && warning.Message.Contains(lost));
        var listing = before.OffHost.Listing!;
        Assert.Equal("listed", listing.State);
        Assert.Equal(1, listing.ArchiveCount);
        Assert.Equal(lost, Assert.Single(listing.NotInCatalog).Id);

        var record = await service.CreateAsync("test", CancellationToken.None);
        await service.UploadPendingAsync(force: true, "test", CancellationToken.None);
        await service.VerifyAsync(record.Id, "test", CancellationToken.None);
        var after = service.Overview();
        Assert.Equal(2, after.OffHost.Listing!.ArchiveCount);
        Assert.Equal(lost, Assert.Single(after.OffHost.Listing.NotInCatalog).Id);
        var verification = after.LastVerification!;
        Assert.Contains(verification.Proved!, line => line.Contains(record.KeyId, StringComparison.Ordinal));
        Assert.Contains(verification.NotProved!, line => line.Contains("origins", StringComparison.Ordinal));
        Assert.Contains(verification.NotProved!, line => line.Contains("backup-key.sh id", StringComparison.Ordinal)
                                                         && line.Contains("secret-key.sh id", StringComparison.Ordinal));
        Assert.Contains("escrowed key copies were not checked", service.List()[0].Verification!.Detail);

        // A write-only target says that it cannot list; a missing mount says that it is unreachable.
        var writeOnly = _fixture.Service(_clock, new FakeTarget()).Overview().OffHost.Listing!;
        Assert.Equal("not-listable", writeOnly.State);
        Assert.Contains("cannot list", writeOnly.Detail);
        var missing = _fixture.Service(_clock, new DirectoryBackupTarget(Path.Combine(_fixture.Root, "not-mounted"))).Overview();
        Assert.Equal("unreachable", missing.OffHost.Listing!.State);
    }

    [Fact]
    public void Overview_exposes_restored_host_ca_mismatch()
    {
        var ca = Path.Combine(_fixture.Root, "ca.pub");
        var state = Path.Combine(_fixture.Root, "host-certificates");
        Directory.CreateDirectory(state);
        File.WriteAllText(ca, "ssh-ed25519 " + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + " test\n");
        File.WriteAllText(Path.Combine(state, "ca-fingerprint"), "SHA256:old-ca\n");
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(_fixture.Configuration())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostCertificates:CaPrivateKeyFile"] = Path.Combine(_fixture.Root, "ca"),
                ["HostCertificates:StateDirectory"] = state,
            }).Build();
        var service = new StudioBackupService(configuration, new BackupWritePause(), _clock, targets: _ => new FakeTarget());
        var overview = service.Overview();
        Assert.Equal("failing", overview.State);
        Assert.Contains(overview.Warnings, warning => warning.Code == "host-ca-mismatch" && warning.Severity == "danger");
    }

    [Fact]
    public async Task Overview_reports_backups_off_without_a_key_and_creation_refuses()
    {
        var service = _fixture.Service(_clock, null, withKey: false);
        var overview = service.Overview();
        Assert.Equal("not-configured", overview.State);
        Assert.Contains(overview.Warnings, warning => warning.Code == "backup-not-configured");
        Assert.Equal("backup-not-configured",
            (await Assert.ThrowsAsync<BackupException>(() => service.CreateAsync("test", CancellationToken.None))).Code);
        await service.RunDueAsync(CancellationToken.None);
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task Failed_run_is_visible_and_a_corrupted_archive_fails_verification()
    {
        var service = _fixture.Service(_clock, null);
        var record = await service.CreateAsync("test", CancellationToken.None);
        var path = _fixture.Catalog.ArchivePath(record.Id);
        var bytes = File.ReadAllBytes(path);
        bytes[^20] ^= 0xff;
        File.WriteAllBytes(path, bytes);

        var verification = await service.VerifyAsync(record.Id, "test", CancellationToken.None);
        Assert.Equal("failed", verification.Outcome);
        Assert.Contains("checksum", verification.Detail);
        Assert.Contains(service.Overview().Warnings, warning => warning.Code == "verification-failed");

        Directory.Delete(_fixture.Workspace, recursive: true);
        await Assert.ThrowsAsync<BackupException>(() => service.CreateAsync("test", CancellationToken.None));
        var overview = service.Overview();
        Assert.Equal("failed", overview.LastRun?.Outcome);
        Assert.Contains(overview.Warnings, warning => warning.Code == "backup-failed");
    }

    [Fact]
    public async Task Scheduled_and_on_demand_backups_succeed_and_verify_while_the_kill_switch_holds_the_control_plane_lock()
    {
        // AGT-3729: the running API holds .security/control-plane.lock with FileShare.None for its lifetime.
        var configuration = _fixture.Configuration();
        var store = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock);
        using var http = new HttpClient();
        var killSwitch = new OperatorKillSwitch(store, new OperatorKillSwitchLog(store.SecurityFolder),
            new OperationsServerOperatorRevocation(http, OperationsServerRevocationOptions.From(configuration)),
            new DeferredOperatorKillNotifier(), _clock);
        var api = new OperatorKillSwitchService(killSwitch, store, new OperatorKillSwitchOptions(TimeSpan.FromSeconds(2), TimeSpan.Zero),
            _clock, NullLogger<OperatorKillSwitchService>.Instance);
        await api.StartAsync(CancellationToken.None);
        try
        {
            File.WriteAllText(Path.Combine(store.SecurityFolder, "probe.json"), "{\"kept\":true}");
            var lockPath = Path.Combine(store.SecurityFolder, OperatorKillSwitchHostLock.FileName);
            Assert.True(File.Exists(lockPath));
            Assert.Null(OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder));

            var service = _fixture.Service(_clock, new FakeTarget());
            await service.RunDueAsync(CancellationToken.None);
            var scheduled = Assert.Single(service.List());
            Assert.Equal("verified", scheduled.Verification?.Outcome);
            Assert.Empty(scheduled.Consistency.Unreadable ?? []);
            var overview = service.Overview();
            Assert.Equal("ok", overview.State);
            Assert.Equal("succeeded", overview.LastRun?.Outcome);
            Assert.Equal(scheduled.Id, overview.LastGood?.Id);

            var files = _fixture.ReadArchive(scheduled.Id);
            Assert.True(files.ContainsKey("workspace/.security/probe.json"));
            Assert.True(files.ContainsKey("workspace/.metadata/projects.json"));
            Assert.False(files.ContainsKey("workspace/.security/" + OperatorKillSwitchHostLock.FileName));
            using (var archive = File.OpenRead(_fixture.Catalog.ArchivePath(scheduled.Id)))
            {
                var (manifest, _) = BackupArchive.Read(archive, BackupKey.Load(_fixture.KeyFile), null, null, null);
                Assert.Contains("workspace: .security/" + OperatorKillSwitchHostLock.FileName, manifest.NotIncluded);
            }

            _clock.Advance(TimeSpan.FromMinutes(1));
            var manual = await service.CreateAsync("manual:owner", CancellationToken.None);
            Assert.Equal("verified", (await service.VerifyAsync(manual.Id, "manual:owner", CancellationToken.None)).Outcome);
            Assert.Null(OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder));
        }
        finally
        {
            await api.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_file_another_process_locks_is_listed_as_unreadable_and_the_backup_still_verifies()
    {
        var locked = Path.Combine(_fixture.Workspace, ".metadata", "held.json");
        File.WriteAllText(locked, "{}");
        var service = _fixture.Service(_clock, new FakeTarget());
        BackupRecord record;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            record = await service.CreateAsync("test", CancellationToken.None);

        var unreadable = Assert.Single(record.Consistency.Unreadable ?? []);
        Assert.Equal("workspace/.metadata/held.json", unreadable.Path);
        Assert.False(string.IsNullOrWhiteSpace(unreadable.Reason));
        var files = _fixture.ReadArchive(record.Id);
        Assert.False(files.ContainsKey("workspace/.metadata/held.json"));
        Assert.True(files.ContainsKey("workspace/.metadata/projects.json"));
        using (var archive = File.OpenRead(_fixture.Catalog.ArchivePath(record.Id)))
        {
            var (_, contents) = BackupArchive.Read(archive, BackupKey.Load(_fixture.KeyFile), null, null, null);
            Assert.Equal("workspace/.metadata/held.json", Assert.Single(contents.Consistency.Unreadable ?? []).Path);
        }
        Assert.Equal("verified", (await service.VerifyAsync(record.Id, "test", CancellationToken.None)).Outcome);
        var warning = Assert.Single(service.Overview().Warnings, item => item.Code == "backup-files-unreadable");
        Assert.Equal("warning", warning.Severity);
        Assert.Contains("workspace/.metadata/held.json", warning.Message);
    }

    [Fact]
    public async Task A_file_system_failure_fails_the_run_with_a_backup_exception_naming_the_path()
    {
        var blocker = Path.Combine(_fixture.Root, "not-a-directory");
        File.WriteAllText(blocker, "a file where the backup directory belongs");
        var service = _fixture.Service(_clock, null, backupDirectory: blocker);

        var failure = await Assert.ThrowsAsync<BackupException>(() => service.CreateAsync("test", CancellationToken.None));
        Assert.Equal("backup-io-failed", failure.Code);
        Assert.Contains("not-a-directory", failure.Message);
    }

    [Fact]
    public async Task Restore_onto_an_empty_host_brings_back_files_checkouts_studio_refs_and_the_secret_key()
    {
        var origin = Path.Combine(_fixture.Root, "remote", "app.git");
        var checkout = Path.Combine(_fixture.Projects, "app");
        CheckoutFileFixture.Git(_fixture.Root, "init", "-q", "--bare", origin);
        CheckoutFileFixture.Git(_fixture.Root, "init", "-q", "-b", "main", checkout);
        File.WriteAllText(Path.Combine(checkout, "README.md"), "app\n");
        Commit(checkout, "initial");
        CheckoutFileFixture.Git(checkout, "remote", "add", "origin", origin);
        CheckoutFileFixture.Git(checkout, "push", "-q", "origin", "main");
        Commit(checkout, "studio only");
        CheckoutFileFixture.Git(checkout, "update-ref", "refs/agent-studio/results/AGT-1", "HEAD");
        var studioRef = CheckoutFileFixture.Git(checkout, "rev-parse", "refs/agent-studio/results/AGT-1").Trim();
        CheckoutFileFixture.Git(checkout, "reset", "-q", "--hard", "HEAD~1");
        File.WriteAllText(Path.Combine(checkout, "local-commit.txt"), "not pushed\n");
        Commit(checkout, "local integration work");
        var localCommit = CheckoutFileFixture.Git(checkout, "rev-parse", "HEAD").Trim();
        CheckoutFileFixture.Git(checkout, "branch", "runner/local-work");
        var storage = Path.Combine(checkout, ".task-storage");
        Directory.CreateDirectory(storage);
        File.WriteAllText(Path.Combine(storage, "card.json"), "{\"id\":\"AGT-1\"}");
        CheckoutFileFixture.Git(checkout, "worktree", "add", "-q", Path.Combine(_fixture.Projects, "worktrees", "agt-1"), "-b", "agt-1");
        File.WriteAllText(Path.Combine(checkout, "untracked-only-here.txt"), "lost on purpose");
        var local = Path.Combine(_fixture.Projects, "scratch");
        CheckoutFileFixture.Git(_fixture.Root, "init", "-q", "-b", "main", local);
        File.WriteAllText(Path.Combine(local, "notes.txt"), "no remote\n");
        Commit(local, "local");
        Directory.CreateDirectory(Path.Combine(_fixture.Projects, ".task-storage", "other"));
        File.WriteAllText(Path.Combine(_fixture.Projects, ".task-storage", "other", "card.json"), "{}");

        Directory.CreateDirectory(Path.Combine(_fixture.Workspace, "Ready"));
        File.WriteAllText(Path.Combine(_fixture.Workspace, ".security-access.json"), "{\"users\":[]}");
        CheckoutFileFixture.Git(_fixture.Workspace, "init", "-q", "-b", "main");
        Commit(_fixture.Workspace, "cards");
        var sshKey = Path.Combine(_fixture.Home, ".ssh", "id_ed25519");
        Directory.CreateDirectory(Path.GetDirectoryName(sshKey)!);
        File.WriteAllText(sshKey, "fixture key");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(sshKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(Path.Combine(_fixture.Home, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_fixture.Home, "node_modules", "pkg", "index.js"), "cache");

        var kek = _fixture.CreateSecretKey();
        var service = _fixture.Service(_clock, null, secretKey: kek);
        service.StorageLocations = () => [storage];
        var record = await service.CreateAsync("test", CancellationToken.None);
        Assert.Equal("verified", (await service.VerifyAsync(null, "test", CancellationToken.None)).Outcome);

        // The new host: the data directories setup.sh lays out, the archive, the escrowed keys and the remote.
        var host = Path.Combine(_fixture.Root, "new-host");
        var targets = new[] { "workspace", "home", "projects" }.ToDictionary(name => name, name => Path.Combine(host, name));
        LayOutSetupDataDirectory(host);
        var archive = Path.Combine(_fixture.Root, "fetched", record.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.Copy(_fixture.Catalog.ArchivePath(record.Id), archive);
        File.Copy(_fixture.Catalog.ChecksumPath(record.Id), archive + ".sha256");
        var secretTarget = Path.Combine(host, "keys", "secrets.kek");
        var output = new StringWriter();
        var error = new StringWriter();
        var args = new List<string>
        {
            "restore", "--key", _fixture.KeyFile, "--archive", archive,
            "--secret-key", _fixture.SecretKeyFile, "--secret-key-target", secretTarget, "--restored-by", "owner@new-host",
        };
        foreach (var (name, path) in targets) args.AddRange(["--target", $"{name}={path}"]);

        _clock.Advance(TimeSpan.FromHours(30));
        Assert.Equal(0, StudioBackupCommand.Run(args.ToArray(), output, error, clock: _clock));
        Assert.Equal("", error.ToString());

        // AGT-3734: the restore leaves a receipt in the workspace and a row in the management audit.
        var receipt = BackupRestoreReceipts.Latest(targets["workspace"]);
        Assert.NotNull(receipt);
        Assert.Equal(record.Id, receipt.BackupId);
        Assert.Equal(record.CreatedAt, receipt.ArchiveCreatedAt);
        Assert.Equal(record.Sha256, receipt.ArchiveSha256);
        Assert.Equal(_clock.GetUtcNow().ToString("O"), receipt.RestoredAt);
        Assert.Equal("owner@new-host", receipt.RestoredBy);
        Assert.Equal(BackupKey.Load(_fixture.KeyFile).Id, receipt.KeyId);
        Assert.Equal(kek.Load(createIfMissing: false).Id, receipt.SecretKeyCopyId);
        Assert.Equal(receipt.SecretKeyId, receipt.SecretKeyCopyId);
        Assert.Equal(2, receipt.CheckoutsCloned);
        var audit = File.ReadAllLines(BackupRestoreReceipts.ManagementAuditPath(targets["workspace"]))
            .Select(line => JsonSerializer.Deserialize<ManagementAuditEvent>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Single(row => row.Kind == BackupRestoreReceipts.AuditKind);
        Assert.Equal("owner@new-host", audit.Actor);
        Assert.Contains(record.Id, audit.Summary);
        Assert.Contains($"work after {record.CreatedAt} is not in this installation", audit.Summary);
        Assert.Contains("is not in this installation", output.ToString());

        Assert.Equal("{\"users\":[]}", File.ReadAllText(Path.Combine(targets["workspace"], ".security-access.json")));
        Assert.True(Directory.Exists(Path.Combine(targets["workspace"], "Ready")));
        // The archived workspace repository replaces the one setup.sh initialized.
        Assert.Equal("cards", CheckoutFileFixture.Git(targets["workspace"], "log", "-1", "--format=%s").Trim());
        Assert.Equal("1", CheckoutFileFixture.Git(targets["workspace"], "rev-list", "--all", "--count").Trim());
        Assert.False(File.Exists(Path.Combine(targets["workspace"], ".gitignore")));
        Assert.Equal("fixture key", File.ReadAllText(Path.Combine(targets["home"], ".ssh", "id_ed25519")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(targets["home"], ".ssh", "id_ed25519")));
        Assert.False(Directory.Exists(Path.Combine(targets["home"], "node_modules")));
        var restoredCheckout = Path.Combine(targets["projects"], "app");
        Assert.Equal("app\n", File.ReadAllText(Path.Combine(restoredCheckout, "README.md")));
        Assert.Equal(studioRef, CheckoutFileFixture.Git(restoredCheckout, "rev-parse", "refs/agent-studio/results/AGT-1").Trim());
        Assert.Equal(localCommit, CheckoutFileFixture.Git(restoredCheckout, "rev-parse", "main").Trim());
        Assert.Equal(localCommit, CheckoutFileFixture.Git(restoredCheckout, "rev-parse", "runner/local-work").Trim());
        Assert.Equal(localCommit, CheckoutFileFixture.Git(restoredCheckout, "rev-parse", "agt-1").Trim());
        Assert.Equal("not pushed\n", File.ReadAllText(Path.Combine(restoredCheckout, "local-commit.txt")));
        Assert.Equal("main", CheckoutFileFixture.Git(restoredCheckout, "symbolic-ref", "--short", "HEAD").Trim());
        Assert.Equal("{\"id\":\"AGT-1\"}", File.ReadAllText(Path.Combine(restoredCheckout, ".task-storage", "card.json")));
        Assert.False(File.Exists(Path.Combine(restoredCheckout, "untracked-only-here.txt")));
        Assert.False(Directory.Exists(Path.Combine(targets["projects"], "worktrees", "agt-1")));
        Assert.Equal("no remote\n", File.ReadAllText(Path.Combine(targets["projects"], "scratch", "notes.txt")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(targets["projects"], ".task-storage", "other", "card.json")));
        Assert.Equal(File.ReadAllText(_fixture.SecretKeyFile).Trim(), File.ReadAllText(secretTarget).Trim());

        // A second restore onto the now populated host is refused and leaves it intact.
        var again = StudioBackupCommand.Run(args.ToArray(), new StringWriter(), error);
        Assert.Equal(1, again);
        Assert.Contains("restore-target-not-empty", error.ToString());
        Assert.True(File.Exists(Path.Combine(targets["workspace"], ".security-access.json")));
    }

    [Fact]
    public async Task Restore_ends_archived_sessions_and_flags_a_token_revoked_after_the_archive()
    {
        var live = new AccessSecurityStore(_fixture.Configuration(), NullLogger<AccessSecurityStore>.Instance, _clock);
        var owner = live.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner"));
        var token = live.CreateApiToken(new CreateApiTokenRequest("drill", StudioRoles.Viewer,
            ["*"], Start.UtcDateTime.AddDays(1)), owner.User.Id);
        var backup = await _fixture.Service(_clock, null).CreateAsync("test", CancellationToken.None);

        live.RevokeApiToken(token.Token.Id);
        Assert.Null(live.AuthenticateServiceAccount(token.Secret));

        var restoredWorkspace = Path.Combine(_fixture.Root, "restored", "workspace");
        var result = BackupRestore.Restore(_fixture.Catalog.ArchivePath(backup.Id), BackupKey.Load(_fixture.KeyFile),
            new BackupRestorePlan(new Dictionary<string, string> { ["workspace"] = restoredWorkspace }, false));
        var restoredConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = restoredWorkspace }).Build();
        var restored = new AccessSecurityStore(restoredConfig, NullLogger<AccessSecurityStore>.Instance, _clock);

        Assert.Null(restored.AuthenticateSession(owner.SessionToken, touch: false));
        Assert.Equal(backup.Id, result.Manifest.Id);
        var notice = Assert.IsType<RestoreNotice>(RestoreSecurityNotice.Read(restored.SecurityFolder));
        Assert.Equal(backup.Id, notice.ArchiveId);
        Assert.Equal(backup.CreatedAt, notice.ArchiveCreatedAt);
        // Revocations after the archive cannot be reconstructed. The Owner is
        // explicitly warned while the restored token awaits re-revocation.
        Assert.NotNull(restored.AuthenticateServiceAccount(token.Secret));
        RestoreSecurityNotice.Acknowledge(restored.SecurityFolder, notice.ArchiveId);
        Assert.Null(RestoreSecurityNotice.Read(restored.SecurityFolder));
    }

    [Theory]
    [InlineData("extra-file")]
    [InlineData("second-commit")]
    [InlineData("edited-gitignore")]
    public async Task Restore_refuses_a_setup_workspace_that_holds_more_than_the_installer_commit(string change)
    {
        var record = await _fixture.Service(_clock, null).CreateAsync("test", CancellationToken.None);
        var host = Path.Combine(_fixture.Root, "new-host");
        var workspace = LayOutSetupDataDirectory(host);
        Assert.True(BackupRestore.IsSetupWorkspace(workspace));
        switch (change)
        {
            case "extra-file":
                File.WriteAllText(Path.Combine(workspace, "card.md"), "kept");
                break;
            case "second-commit":
                Commit(workspace, "operator work");
                break;
            case "edited-gitignore":
                File.AppendAllText(Path.Combine(workspace, ".gitignore"), "/drafts/\n");
                break;
        }
        var error = new StringWriter();

        Assert.Equal(1, StudioBackupCommand.Run(
            ["restore", "--key", _fixture.KeyFile, "--archive", _fixture.Catalog.ArchivePath(record.Id),
             "--target", "workspace=" + workspace, "--target", "home=" + Path.Combine(host, "home")],
            new StringWriter(), error));
        Assert.Contains("restore-target-not-empty", error.ToString());
        Assert.True(File.Exists(Path.Combine(workspace, ".gitignore")));
        Assert.True(Directory.Exists(Path.Combine(workspace, ".git")));
    }

    [Theory]
    [InlineData("missing-archive")]
    [InlineData("wrong-key")]
    [InlineData("failed-clone")]
    public async Task A_failed_restore_leaves_the_setup_workspace_in_place(string failure)
    {
        var origin = Path.Combine(_fixture.Root, "remotes", "app.git");
        var checkout = Path.Combine(_fixture.Projects, "app");
        CheckoutFileFixture.Git(_fixture.Root, "init", "-q", "--bare", origin);
        CheckoutFileFixture.Git(_fixture.Root, "init", "-q", "-b", "main", checkout);
        File.WriteAllText(Path.Combine(checkout, "README.md"), "app\n");
        Commit(checkout, "initial");
        CheckoutFileFixture.Git(checkout, "remote", "add", "origin", origin);
        CheckoutFileFixture.Git(checkout, "push", "-q", "origin", "main");
        File.WriteAllText(Path.Combine(_fixture.Workspace, ".security-access.json"), "{\"users\":[]}");
        var record = await _fixture.Service(_clock, null).CreateAsync("test", CancellationToken.None);
        var archive = _fixture.Catalog.ArchivePath(record.Id);
        var key = BackupKey.Load(_fixture.KeyFile);
        switch (failure)
        {
            case "missing-archive":
                archive += ".missing";
                break;
            case "wrong-key":
                var otherKey = Path.Combine(_fixture.Root, "keys", "other-backup.key");
                BackupKey.Create(otherKey);
                key = BackupKey.Load(otherKey);
                break;
            case "failed-clone":
                Directory.Delete(origin, recursive: true);
                break;
        }
        var host = Path.Combine(_fixture.Root, "new-host");
        var workspace = LayOutSetupDataDirectory(host);
        var installerCommit = CheckoutFileFixture.Git(workspace, "rev-parse", "HEAD").Trim();

        Assert.ThrowsAny<Exception>(() => BackupRestore.Restore(archive, key, new BackupRestorePlan(
            new Dictionary<string, string>
            {
                ["workspace"] = workspace, ["home"] = Path.Combine(host, "home"), ["projects"] = Path.Combine(host, "projects"),
            }, CloneCheckouts: true)));

        Assert.True(BackupRestore.IsSetupWorkspace(workspace));
        Assert.Equal(installerCommit, CheckoutFileFixture.Git(workspace, "rev-parse", "HEAD").Trim());
        Assert.Equal("/.security/\n", File.ReadAllText(Path.Combine(workspace, ".gitignore")));
        Assert.Contains("/.security/", File.ReadAllText(Path.Combine(workspace, ".git", "info", "exclude")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(host, "projects")));
        Assert.False(File.Exists(Path.Combine(workspace, ".security-access.json")));
    }

    [Fact]
    public async Task Restore_refuses_a_wrong_secret_key_and_a_wrong_backup_key_before_writing()
    {
        var kek = _fixture.CreateSecretKey();
        var service = _fixture.Service(_clock, null, secretKey: kek);
        var record = await service.CreateAsync("test", CancellationToken.None);
        var archive = _fixture.Catalog.ArchivePath(record.Id);
        var host = Path.Combine(_fixture.Root, "new-host");
        var wrongSecret = Path.Combine(_fixture.Root, "wrong.kek");
        File.WriteAllText(wrongSecret, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var error = new StringWriter();

        Assert.Equal(1, StudioBackupCommand.Run(
            ["restore", "--key", _fixture.KeyFile, "--archive", archive, "--secret-key", wrongSecret,
             "--target", "workspace=" + Path.Combine(host, "workspace")], new StringWriter(), error));
        Assert.Contains("restore-secret-key-mismatch", error.ToString());

        var wrongKey = Path.Combine(_fixture.Root, "keys", "other-backup.key");
        BackupKey.Create(wrongKey);
        Assert.Equal(1, StudioBackupCommand.Run(
            ["restore", "--key", wrongKey, "--archive", archive, "--target", "workspace=" + Path.Combine(host, "workspace")],
            new StringWriter(), error));
        Assert.Contains("backup-key-mismatch", error.ToString());
        Assert.False(Directory.Exists(Path.Combine(host, "workspace")) && Directory.EnumerateFileSystemEntries(Path.Combine(host, "workspace")).Any());
    }

    [Fact]
    public async Task Configuration_refuses_a_key_or_backup_directory_inside_the_backed_up_data()
    {
        var keyInside = Path.Combine(_fixture.Workspace, "backup.key");
        BackupKey.Create(keyInside);
        var service = _fixture.Service(_clock, null, keyFile: keyInside);
        Assert.Equal("backup-key-inside-source",
            (await Assert.ThrowsAsync<BackupException>(() => service.CreateAsync("test", CancellationToken.None))).Code);

        var inside = _fixture.Service(_clock, null, backupDirectory: Path.Combine(_fixture.Home, "backups"));
        Assert.Equal("backup-directory-inside-source",
            (await Assert.ThrowsAsync<BackupException>(() => inside.CreateAsync("test", CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData("https://user:token@github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("https://ghp-fixture@github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("ssh://git@host.example/org/repo.git", "ssh://git@host.example/org/repo.git")]
    [InlineData("ssh://git:secret@host.example/org/repo.git", "ssh://git@host.example/org/repo.git")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    public void Recorded_origins_carry_no_credentials(string url, string expected)
        => Assert.Equal(expected, BackupCheckouts.StripCredentials(url));

    /// <summary>The data directories and workspace repository that setup.sh creates on a new host.</summary>
    private static string LayOutSetupDataDirectory(string host)
    {
        var workspace = Path.Combine(host, "workspace");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(host, "projects"));
        Directory.CreateDirectory(Path.Combine(host, "home", ".local", "share"));
        CheckoutFileFixture.Git(workspace, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(workspace, ".gitignore"), "/.security/\n");
        CheckoutFileFixture.Git(workspace, "add", ".gitignore");
        CheckoutFileFixture.Git(workspace, "-c", "user.name=Studio Installer", "-c", "user.email=installer@studio.invalid",
            "-c", "commit.gpgsign=false", "commit", "-qm", "Initialize Studio workspace");
        File.AppendAllText(Path.Combine(workspace, ".git", "info", "exclude"), "/.security/\n");
        return workspace;
    }

    private static void Commit(string repository, string message)
    {
        CheckoutFileFixture.Git(repository, "add", "-A");
        CheckoutFileFixture.Git(repository, "-c", "user.email=backup@test", "-c", "user.name=backup", "-c", "commit.gpgsign=false",
            "commit", "-q", "--allow-empty", "-m", message);
    }

    private static string Version(int n) => JsonSerializer.Serialize(new { version = n, padding = new string('x', 4096 + n) });

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] Encrypt(BackupKey key, byte[] plain)
    {
        using var output = new MemoryStream();
        using (var encrypting = BackupCipher.Encrypt(output, key)) encrypting.Write(plain);
        return output.ToArray();
    }

    private static byte[] Decrypt(BackupKey key, byte[] sealedBytes)
    {
        using var input = new MemoryStream(sealedBytes);
        using var decrypting = BackupCipher.Decrypt(input, key);
        using var output = new MemoryStream();
        decrypting.CopyTo(output);
        return output.ToArray();
    }

    private static BackupRecord Record(string id, DateTimeOffset createdAt)
        => new(id, id + BackupCatalog.ArchiveSuffix, createdAt.ToString("O"), 1, "", "k", null, 0, 0,
            new BackupConsistency(true, true, 0, false, 0, 0, 0, 0, 0, 0, []));

    private static BackupRunRecord Run(DateTimeOffset at, string outcome, string? id = null)
        => new(at.ToString("O"), "schedule", outcome, id, outcome == "failed" ? "boom" : null, 1);

    private sealed class FakeTarget : IBackupTarget
    {
        public int Failures { get; set; }
        public List<(string Name, byte[] Bytes)> Uploads { get; } = [];
        public string Kind => "fake";
        public string Description => "fake://off-host";

        public Task UploadAsync(string localPath, string remoteName, CancellationToken cancellationToken)
        {
            if (Failures > 0)
            {
                Failures--;
                throw new BackupException("backup-target-upload-failed", "target offline");
            }
            Uploads.Add((remoteName, File.ReadAllBytes(localPath)));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingTarget : IBackupTarget
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Kind => "fake";
        public string Description => "fake://pending";

        public async Task UploadAsync(string localPath, string remoteName, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeProcessRunner : IBackupProcessRunner
    {
        public int ExitCode { get; set; }
        public List<System.Diagnostics.ProcessStartInfo> Starts { get; } = [];
        public List<string?> Inputs { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(System.Diagnostics.ProcessStartInfo start, string? standardInput,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Starts.Add(start);
            Inputs.Add(standardInput);
            return Task.FromResult((ExitCode, ExitCode == 0 ? "" : "Permission denied"));
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;
        public List<(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), headers, body));
            return new HttpResponseMessage(Status) { Content = new StringContent(Status == System.Net.HttpStatusCode.OK ? "" : "AccessDenied") };
        }
    }

    private sealed class StalledHeadersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

/// <summary>A fake installation: data directories, keys outside them, a backup directory and an off-host directory.</summary>
internal sealed class BackupFixture : IDisposable
{
    public BackupFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "studio-backup-test-" + Guid.NewGuid().ToString("N"));
        Workspace = Path.Combine(Root, "data", "workspace");
        Home = Path.Combine(Root, "data", "home");
        Projects = Path.Combine(Root, "data", "projects");
        BackupDirectory = Path.Combine(Root, "backups");
        KeyFile = Path.Combine(Root, "keys", "backup.key");
        SecretKeyFile = Path.Combine(Root, "keys", "secrets.kek");
        OffHost = Path.Combine(Root, "off-host");
        Directory.CreateDirectory(Path.Combine(Workspace, ".metadata"));
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Projects);
        File.WriteAllText(Path.Combine(Workspace, ".metadata", "projects.json"), "{\"projects\":[]}");
        BackupKey.Create(KeyFile);
    }

    public string Root { get; }
    public string Workspace { get; }
    public string Home { get; }
    public string Projects { get; }
    public string BackupDirectory { get; }
    public string KeyFile { get; }
    public string SecretKeyFile { get; }
    public string OffHost { get; }
    public BackupCatalog Catalog => new(BackupDirectory);

    public IConfiguration Configuration(bool withKey = true, int retention = 12, string? keyFile = null, string? backupDirectory = null)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Workspace,
            ["Secrets:KeyFile"] = SecretKeyFile,
            ["Backup:Directory"] = backupDirectory ?? BackupDirectory,
            ["Backup:KeyFile"] = withKey ? keyFile ?? KeyFile : null,
            ["Backup:RetentionCount"] = retention.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Backup:Sources:home:Path"] = Home,
            ["Backup:Sources:projects:Path"] = Projects,
            ["Backup:Sources:projects:Projects"] = "true",
        }).Build();

    public BackupOptions Options(bool withKey = true) => BackupOptions.From(Configuration(withKey));

    public ConfiguredKeyEncryptionKeySource CreateSecretKey()
    {
        var source = new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null, _ => { });
        source.Load(createIfMissing: true);
        return source;
    }

    public StudioBackupService Service(TimeProvider clock, IBackupTarget? target, int retention = 12, bool withKey = true,
        IKeyEncryptionKeySource? secretKey = null, string? keyFile = null, string? backupDirectory = null)
        => new(Configuration(withKey, retention, keyFile, backupDirectory), new BackupWritePause(), clock, secretKey,
            targets: _ => target);

    /// <summary>Decrypts an archive of the catalog into member name and content.</summary>
    public Dictionary<string, byte[]> ReadArchive(string id)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = File.OpenRead(Catalog.ArchivePath(id));
        BackupArchive.Read(stream, BackupKey.Load(KeyFile), null, (name, data, _) =>
        {
            using var copy = new MemoryStream();
            data.CopyTo(copy);
            files[name] = copy.ToArray();
        }, null);
        return files;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SilentCatch.Note(ex, "Backup fixture cleanup"); }
    }
}
