using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Management;
using AgentStudio.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
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
    public async Task Upload_failure_is_recorded_retried_with_backoff_and_then_succeeds()
    {
        var target = new FakeTarget { Failures = 1 };
        var service = _fixture.Service(_clock, target);
        var record = await service.CreateAsync("test", CancellationToken.None);
        Assert.Equal("pending", Assert.Single(service.List()).Upload?.State);

        Assert.Equal(0, await service.UploadPendingAsync(force: false, "test", CancellationToken.None));
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
            "--secret-key", _fixture.SecretKeyFile, "--secret-key-target", secretTarget,
        };
        foreach (var (name, path) in targets) args.AddRange(["--target", $"{name}={path}"]);

        Assert.Equal(0, StudioBackupCommand.Run(args.ToArray(), output, error));
        Assert.Equal("", error.ToString());

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
