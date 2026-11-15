using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Management;
using AgentStudio.Secrets;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>A disposable two-project installation restored from the same off-host archives an operator fetches.</summary>
public sealed class StudioColdHostRecoveryTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Scheduled_and_manual_archives_restore_two_projects_and_running_team_records()
    {
        using var fixture = new BackupFixture();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        var key = fixture.CreateSecretKey();
        var access = new AccessSecurityStore(fixture.Configuration(), NullLogger<AccessSecurityStore>.Instance, clock, key);
        var owner = access.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner"));
        var operatorUser = access.CreateUser(new CreateUserRequest("operator", "Operator", StudioRoles.Operator,
            ["PROJ-A", "PROJ-B"], "temporary bridge phrase!"), owner.User.Id);
        var operatorSession = access.Login("operator", operatorUser.TemporaryPassword, "recovery-fixture");
        var principal = access.AuthenticateSession(operatorSession.SessionToken)!;
        var factor = access.BeginTotpEnrollment(principal);
        access.ConfirmTotpEnrollment(principal, SecondFactorCodes.CurrentTotp(factor, clock.GetUtcNow().UtcDateTime));
        var token = access.CreateApiToken(new CreateApiTokenRequest("integration", StudioRoles.Viewer,
            ["PROJ-A", "PROJ-B"], clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.User.Id);
        var runner = access.CreateEnrollment(new RunnerEnrollmentRequest("recovery-runner", [RunnerScopes.Claim], null, null,
            ["PROJ-A", "PROJ-B"]));
        var enrolled = access.EnrollRunner(runner.Code);
        Assert.NotNull(access.AuthenticateRunner(enrolled.Secret));

        var secrets = new BuiltInSecretStore(fixture.Configuration(), key);
        var provider = new SecretProvider(secrets, new SecretAuditLog(fixture.Configuration()),
            new SecretValueScrubber(clock), clock, NullLogger<SecretProvider>.Instance);
        var secretValue = "fixture-" + Guid.NewGuid().ToString("N");
        var secret = await provider.PutAsync(SecretScope.Workspace, "recovery-probe", secretValue,
            SecretActor.Human(owner.User.Id));

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        var projects = new[] { (Id: "PROJ-A", Name: "alpha"), (Id: "PROJ-B", Name: "beta") };
        foreach (var project in projects)
        {
            var remote = Path.Combine(fixture.Root, "remotes", project.Name + ".git");
            var checkout = Path.Combine(fixture.Projects, project.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
            CheckoutFileFixture.Git(fixture.Root, "init", "-q", "--bare", remote);
            CheckoutFileFixture.Git(fixture.Root, "init", "-q", "-b", "main", checkout);
            File.WriteAllText(Path.Combine(checkout, "README.md"), project.Id + "\n");
            Commit(checkout, "Initial " + project.Id);
            CheckoutFileFixture.Git(checkout, "remote", "add", "origin", remote);
            CheckoutFileFixture.Git(checkout, "push", "-q", "origin", "main");
            expected["checkout:" + project.Id] = CheckoutFileFixture.Git(checkout, "rev-parse", "HEAD").Trim();
            foreach (var lane in new[] { "Inbox", "Ready", "Active", "Auto Review", "Human Review", "Done" })
            {
                var id = project.Id + "-" + lane.Replace(" ", "", StringComparison.Ordinal);
                var relative = Path.Combine("projects", project.Id, lane, id, "task.json");
                Write(fixture.Workspace, relative, JsonSerializer.Serialize(new { id, projectId = project.Id, state = lane }));
                expected["card:" + id] = relative;
            }
        }
        Write(fixture.Workspace, Path.Combine(".metadata", "projects.json"),
            JsonSerializer.Serialize(new { projects = projects.Select(p => new { id = p.Id, name = p.Name }) }));
        var records = new[]
        {
            "attempts/PROJ-A-Active.json", "reviews/PROJ-B-AutoReview.json",
            "batches/red/manifest.json", "batches/published/manifest.json",
            "settlements/PROJ-A-Done.json", "integrations/PROJ-B-Done.json",
        };
        foreach (var record in records)
            Write(fixture.Home, Path.Combine(".local", "share", "agentstudio", record),
                JsonSerializer.Serialize(new { id = record, outcome = record.Contains("red") ? "red" : "recorded" }));
        Write(fixture.Workspace, ".gitignore", "/.security/\n");
        CheckoutFileFixture.Git(fixture.Workspace, "init", "-q", "-b", "main");
        Commit(fixture.Workspace, "Two project recovery fixture");

        Directory.CreateDirectory(fixture.OffHost);
        var target = new DirectoryBackupTarget(fixture.OffHost);
        var service = fixture.Service(clock, target, secretKey: key);
        await service.RunDueAsync(CancellationToken.None);
        var scheduled = Assert.Single(service.List());
        Assert.Equal("schedule", service.Overview().LastRun?.Trigger);
        Assert.Equal("uploaded", scheduled.Upload?.State);
        Assert.Equal("verified", scheduled.Verification?.Outcome);

        clock.Advance(TimeSpan.FromMinutes(7));
        Write(fixture.Workspace, Path.Combine("projects", "PROJ-A", "Active", "PROJ-A-Active", "progress.json"),
            "{\"stage\":\"after-scheduled-capture\"}");
        var manual = await service.CreateAsync("manual:recovery-drill", CancellationToken.None);
        await service.UploadPendingAsync(force: true, "manual:recovery-drill", CancellationToken.None);
        Assert.Equal("uploaded", service.List().Single(item => item.Id == manual.Id).Upload?.State);
        foreach (var record in new[] { scheduled, manual })
        {
            Assert.True(File.Exists(Path.Combine(fixture.OffHost, record.FileName)));
            Assert.True(File.Exists(Path.Combine(fixture.OffHost, record.FileName + ".sha256")));
        }

        var cardBytes = expected.Where(pair => pair.Key.StartsWith("card:", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Value, pair => File.ReadAllBytes(Path.Combine(fixture.Workspace, pair.Value)));
        var projectMetadata = File.ReadAllBytes(Path.Combine(fixture.Workspace, ".metadata", "projects.json"));
        var recordBytes = records.ToDictionary(record => record,
            record => File.ReadAllBytes(Path.Combine(fixture.Home, ".local", "share", "agentstudio", record)));
        var recordDigests = recordBytes.ToDictionary(pair => pair.Key,
            pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value)));
        Directory.Delete(fixture.Workspace, recursive: true);
        Directory.Delete(fixture.Home, recursive: true);
        Directory.Delete(fixture.Projects, recursive: true);

        var lossAt = clock.GetUtcNow().AddMinutes(3);
        var recoveryTimer = Stopwatch.StartNew();
        var host = Path.Combine(fixture.Root, "fresh-host");
        var destinations = new Dictionary<string, string>
        {
            ["workspace"] = Path.Combine(host, "workspace"),
            ["home"] = Path.Combine(host, "home"),
            ["projects"] = Path.Combine(host, "projects"),
        };
        InstallEmptyHost(host);
        var archive = Path.Combine(fixture.OffHost, manual.FileName);
        var restored = BackupRestore.Restore(archive, BackupKey.Load(fixture.KeyFile),
            new BackupRestorePlan(destinations, CloneCheckouts: true));
        File.Copy(fixture.SecretKeyFile, Path.Combine(host, "secrets.kek"));
        recoveryTimer.Stop();
        Assert.Equal(2, restored.CheckoutsCloned);
        Assert.Equal(manual.Id, restored.Manifest.Id);

        foreach (var project in projects)
            Assert.Equal(expected["checkout:" + project.Id],
                CheckoutFileFixture.Git(Path.Combine(destinations["projects"], project.Name), "rev-parse", "HEAD").Trim());
        foreach (var (relative, content) in cardBytes)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(destinations["workspace"], relative)));
        foreach (var (record, content) in recordBytes)
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(destinations["home"], ".local", "share", "agentstudio", record)));
        Assert.Equal(projectMetadata, File.ReadAllBytes(Path.Combine(destinations["workspace"], ".metadata", "projects.json")));
        Assert.True(File.Exists(Path.Combine(destinations["workspace"], "projects", "PROJ-A", "Active",
            "PROJ-A-Active", "progress.json")));

        var restoredConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = destinations["workspace"],
            ["Secrets:KeyFile"] = Path.Combine(host, "secrets.kek"),
        }).Build();
        var reopenedAccess = new AccessSecurityStore(restoredConfig, NullLogger<AccessSecurityStore>.Instance, clock);
        Assert.Equal(owner.User.Id, reopenedAccess.ListUsers().Single(user => user.Username == "owner").Id);
        Assert.Equal(operatorUser.User.Id, reopenedAccess.ListUsers().Single(user => user.Username == "operator").Id);
        Assert.Equal(token.Token.Id, Assert.Single(reopenedAccess.ListApiTokens()).Id);
        Assert.Null(reopenedAccess.AuthenticateSession(operatorSession.SessionToken, touch: false));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(reopenedAccess.Login("operator", operatorUser.TemporaryPassword, "fresh-host",
            SecondFactorCodes.CurrentTotp(factor, clock.GetUtcNow().UtcDateTime), requireSecondFactor: true));
        Assert.NotNull(reopenedAccess.AuthenticateRunner(enrolled.Secret));
        var reopenedSecrets = new BuiltInSecretStore(restoredConfig,
            new ConfiguredKeyEncryptionKeySource(restoredConfig, _ => null, _ => { }));
        Assert.Equal(secret.Id, (await reopenedSecrets.FindAsync(SecretScope.Workspace, "recovery-probe", default))?.Id);
        Assert.Equal(Encoding.UTF8.GetBytes(secretValue),
            await reopenedSecrets.ReadValueAsync(SecretScope.Workspace, "recovery-probe", 1, default));

        // Compare against the source snapshot, then reopen the restored stores. File equality alone
        // does not prove that a live scheduler would avoid replaying work after a process restart.
        foreach (var (record, digest) in recordDigests)
            Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(destinations["home"],
                ".local", "share", "agentstudio", record)))));
        Assert.Single(Directory.GetFiles(Path.Combine(destinations["home"], ".local", "share", "agentstudio", "attempts")));
        Assert.Single(Directory.GetFiles(Path.Combine(destinations["home"], ".local", "share", "agentstudio", "reviews")));
        Assert.Single(Directory.GetFiles(Path.Combine(destinations["home"], ".local", "share", "agentstudio", "batches", "published")));
        Assert.ThrowsAny<Exception>(() => BackupRestore.Restore(archive, BackupKey.Load(fixture.KeyFile),
            new BackupRestorePlan(destinations, CloneCheckouts: true)));

        var evidence = new
        {
            schema = "agent-studio.cold-host-recovery/v1",
            scheduledArchive = scheduled.Id,
            manualArchive = manual.Id,
            restoredArchive = restored.Manifest.Id,
            projectIds = projects.Select(project => project.Id).ToArray(),
            cardIds = expected.Keys.Where(key => key.StartsWith("card:", StringComparison.Ordinal)).ToArray(),
            recordDigests,
            userIds = reopenedAccess.ListUsers().Select(user => user.Id).ToArray(),
            tokenId = token.Token.Id,
            secretId = secret.Id,
            runnerId = enrolled.Runner.Id,
            measuredRecoveryPointSeconds = (lossAt - DateTimeOffset.Parse(manual.CreatedAt)).TotalSeconds,
            measuredRecoveryTimeSeconds = recoveryTimer.Elapsed.TotalSeconds,
            recoveryTimeBasis = "archive fetch from local off-host directory through files restored and key installed",
            liveRestartDeduplicationProved = false,
        };
        if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { Length: > 0 } results)
        {
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "cold-host-recovery.json"), JsonSerializer.Serialize(evidence,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Commit(string repository, string message)
    {
        CheckoutFileFixture.Git(repository, "add", "-A");
        CheckoutFileFixture.Git(repository, "-c", "user.name=Recovery fixture", "-c", "user.email=recovery@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
    }

    private static void InstallEmptyHost(string root)
    {
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(root, "home", ".local", "share"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        CheckoutFileFixture.Git(workspace, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(workspace, ".gitignore"), "/.security/\n");
        Commit(workspace, "Initialize Studio workspace");
        File.AppendAllText(Path.Combine(workspace, ".git", "info", "exclude"), "/.security/\n");
    }
}
