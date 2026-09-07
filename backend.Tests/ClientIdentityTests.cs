using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the registration boundary contract: round-trip register/get/list/soft-delete,
/// the bootstrap default identity is created on first load, and legacy task.json
/// without ownerClientId is migrated to <see cref="DefaultClientIdentity.Id"/>
/// on first scan.
/// </summary>
public class ClientIdentityTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchPath;

    public ClientIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-taskboard-clients-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "watch");
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private IConfiguration BuildConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Environment:DefaultIdentityName"] = "Test Default",
                ["Environment:DefaultIdentityEmoji"] = "🦊",
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
    }

    private ClientIdentityStore BuildStore(IConfiguration config)
        => new(config, NullLogger<ClientIdentityStore>.Instance);

    private ClientIdentityStore BuildStore(
        IConfiguration config,
        ILogger<ClientIdentityStore> logger,
        IAtomicJsonFileWriter? writer = null)
        => writer is null ? new(config, logger) : new(config, logger, writer);

    private TaskScannerService BuildScanner(IConfiguration config)
    {
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    [Fact]
    public void EnsureLoaded_CreatesBootstrapDefault()
    {
        var store = BuildStore(BuildConfig());
        store.EnsureLoaded();

        var defaults = store.Find(DefaultClientIdentity.Id);
        Assert.NotNull(defaults);
        Assert.Equal("Test Default", defaults!.DisplayName);
        Assert.Equal("🦊", defaults.Emoji);
        Assert.True(File.Exists(Path.Combine(_root, "identities", DefaultClientIdentity.Id + ".json")));
    }

    [Fact]
    public void Register_AssignsSlug_AndIsIdempotentOnDisplayName()
    {
        var store = BuildStore(BuildConfig());

        var first = store.Register(new RegisterClientRequest
        {
            DisplayName = "Robert's Workshop",
            Emoji = "🛠️",
            Kind = ClientIdentityKinds.Human
        });
        Assert.Equal("robert-s-workshop", first.Id);
        Assert.Equal(ClientIdentityKind.Human, first.Kind);

        var second = store.Register(new RegisterClientRequest
        {
            DisplayName = "Robert's Workshop",
            Emoji = "🦊"
        });
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("🦊", second.Emoji); // refreshed
    }

    [Fact]
    public void Register_DuplicateSlug_AppendsCounter()
    {
        var store = BuildStore(BuildConfig());
        var a = store.Register(new RegisterClientRequest { DisplayName = "Alpha" });
        var b = store.Register(new RegisterClientRequest { DisplayName = "alpha!" }); // sanitises to alpha
        Assert.Equal("alpha", a.Id);
        Assert.Equal("alpha-2", b.Id);
    }

    [Fact]
    public void IsRegistered_RejectsRetiredAndUnknown()
    {
        var store = BuildStore(BuildConfig());
        var live = store.Register(new RegisterClientRequest { DisplayName = "Live Client" });
        Assert.True(store.IsRegistered(live.Id));
        Assert.False(store.IsRegistered("nope-not-here"));

        var changed = store.SoftDelete(live.Id);
        Assert.True(changed);
        Assert.False(store.IsRegistered(live.Id));
        var afterDelete = store.Find(live.Id);
        Assert.NotNull(afterDelete);
        Assert.Equal(ClientIdentityKind.Retired, afterDelete!.Kind);
    }

    [Fact]
    public void ListAll_ReturnsBootstrapPlusRegistered()
    {
        var store = BuildStore(BuildConfig());
        store.Register(new RegisterClientRequest { DisplayName = "One" });
        store.Register(new RegisterClientRequest { DisplayName = "Two" });

        var list = store.ListAll();
        Assert.Equal(3, list.Count); // bootstrap + 2 registered
        Assert.Contains(list, c => c.Id == DefaultClientIdentity.Id);
        Assert.Contains(list, c => c.DisplayName == "One");
        Assert.Contains(list, c => c.DisplayName == "Two");
    }

    [Fact]
    public void Store_SurvivesRoundTripAcrossInstances()
    {
        var config = BuildConfig();
        var store1 = BuildStore(config);
        store1.Register(new RegisterClientRequest { DisplayName = "Persisted" });

        var store2 = BuildStore(config);
        var found = store2.Find("persisted");
        Assert.NotNull(found);
        Assert.Equal("Persisted", found!.DisplayName);
    }

    [Fact]
    public void EnsureLoaded_NulIdentity_LogsErrorAndKeepsHealthyIdentitiesVisible()
    {
        var config = BuildConfig();
        var seeded = BuildStore(config);
        var healthy = seeded.Register(new RegisterClientRequest { DisplayName = "Healthy Runner", Kind = ClientIdentityKinds.Service });
        var corruptPath = Path.Combine(_root, "identities", "agent-runner-01.json");
        File.WriteAllBytes(corruptPath, new byte[4481]);
        File.SetLastWriteTimeUtc(corruptPath, new DateTime(2026, 8, 5, 14, 35, 0, DateTimeKind.Utc));
        var logger = new RecordingLogger<ClientIdentityStore>();

        var reloaded = BuildStore(config, logger);
        reloaded.EnsureLoaded();

        Assert.NotNull(reloaded.Find(healthy.Id));
        Assert.NotNull(reloaded.Find(DefaultClientIdentity.Id));
        var diagnostic = Assert.Single(reloaded.ListDiagnostics());
        Assert.Equal("agent-runner-01", diagnostic.IdentityId);
        Assert.Equal("agent-runner-01.json", diagnostic.FileName);
        Assert.Equal("identity file corrupt: agent-runner-01.json", diagnostic.Message);
        Assert.Contains("POST /api/clients/register", diagnostic.RestoreHint, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("identity-file-corrupt", StringComparison.Ordinal)
            && entry.Message.Contains("agent-runner-01.json", StringComparison.Ordinal));
    }

    [Fact]
    public void AtomicWriteFailure_LeavesPublishedIdentityParseableAndUnchanged()
    {
        var config = BuildConfig();
        var writer = new ControllableAtomicJsonFileWriter();
        var store = BuildStore(config, NullLogger<ClientIdentityStore>.Instance, writer);
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "atomic-runner",
            Kind = ClientIdentityKinds.Service,
        });
        var path = Path.Combine(_root, "identities", runner.Id + ".json");
        var published = File.ReadAllText(path);
        writer.ShouldFail = (candidate, writeNumber) => candidate == path && writeNumber == 2;

        Assert.Throws<IOException>(() =>
            store.RecordRunnerActivity(runner.Id, activeSlots: 1, availableSlots: 1, claimed: true));

        Assert.Equal(published, File.ReadAllText(path));
        using var parsed = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(runner.Id, parsed.RootElement.GetProperty("Id").GetString());
        Assert.DoesNotContain('\0', File.ReadAllText(path));
    }

    [Fact]
    public void AtomicWrite_OrphanedNulTempFileDoesNotReplacePublishedIdentity()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "interrupted-runner",
            Kind = ClientIdentityKinds.Service,
        });
        var path = Path.Combine(_root, "identities", runner.Id + ".json");
        var orphanedTemp = $"{path}.{Guid.NewGuid():N}.tmp";

        // Simulate termination after staging bytes but before the atomic rename.
        File.WriteAllBytes(orphanedTemp, new byte[4481]);

        var reloaded = BuildStore(config);
        var published = reloaded.Find(runner.Id);
        Assert.NotNull(published);
        Assert.Equal(runner.DisplayName, published!.DisplayName);
        Assert.Empty(reloaded.ListDiagnostics());
        using var parsed = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(runner.Id, parsed.RootElement.GetProperty("Id").GetString());
    }

    [Fact]
    public void Find_ReloadsAnExternallyRestoredCorruptIdentityWithoutRestart()
    {
        var config = BuildConfig();
        var identities = Path.Combine(_root, "identities");
        Directory.CreateDirectory(identities);
        var path = Path.Combine(identities, "agent-runner-01.json");
        File.WriteAllBytes(path, new byte[128]);
        var store = BuildStore(config);
        store.EnsureLoaded();
        Assert.Single(store.ListDiagnostics());
        var restored = new ClientIdentity
        {
            Id = "agent-runner-01",
            DisplayName = "agent-runner-01",
            Kind = ClientIdentityKind.Service,
            RegisteredAt = DateTime.UtcNow.AddDays(-1),
            LastSeenAt = DateTime.UtcNow,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(restored));

        var found = store.Find("agent-runner-01");

        Assert.NotNull(found);
        Assert.Equal(ClientIdentityKind.Service, found!.Kind);
        Assert.Empty(store.ListDiagnostics());
    }

    [Fact]
    public void Register_ReplacesACorruptIdentityThroughTheDocumentedHealingPath()
    {
        var config = BuildConfig();
        var identities = Path.Combine(_root, "identities");
        Directory.CreateDirectory(identities);
        File.WriteAllBytes(Path.Combine(identities, "agent-runner-01.json"), new byte[4481]);
        var store = BuildStore(config);
        store.EnsureLoaded();

        var repaired = store.Register(new RegisterClientRequest
        {
            DisplayName = "agent-runner-01",
            Kind = ClientIdentityKinds.Service,
        });

        Assert.Equal("agent-runner-01", repaired.Id);
        Assert.Empty(store.ListDiagnostics());
        Assert.Equal(ClientIdentityKind.Service, BuildStore(config).Find(repaired.Id)?.Kind);
    }

    [Fact]
    public void RecordSeen_PersistsAtMostOncePerThirtySecondWindow()
    {
        var config = BuildConfig();
        var identities = Path.Combine(_root, "identities");
        Directory.CreateDirectory(identities);
        var record = new ClientIdentity
        {
            Id = "seen-runner",
            DisplayName = "seen-runner",
            Kind = ClientIdentityKind.Service,
            RegisteredAt = DateTime.UtcNow.AddDays(-1),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-1),
        };
        var path = Path.Combine(identities, record.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(record));
        var writer = new ControllableAtomicJsonFileWriter();
        var store = BuildStore(config, NullLogger<ClientIdentityStore>.Instance, writer);
        store.EnsureLoaded();

        store.RecordSeen(record.Id);
        store.RecordSeen(record.Id);

        Assert.Equal(1, writer.WritesFor(path));
        Assert.True(BuildStore(config).Find(record.Id)?.LastSeenAt > record.LastSeenAt);
    }

    [Fact]
    public void Retire_PersistsAcrossRestart_AndCompletesOnlyAfterActiveWorkEnds()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest { DisplayName = "agent-runner-01", Kind = ClientIdentityKinds.Service });

        store.RecordRunnerActivity(runner.Id, activeSlots: 1, availableSlots: 1, claimed: true);
        var draining = store.RequestDrain(runner.Id, retireAfterDrain: true);
        Assert.NotNull(draining?.RetireRequestedAt);
        Assert.Equal(ClientIdentityKind.Service, draining!.Kind);

        store.RecordRunnerActivity(runner.Id, activeSlots: 0, availableSlots: 2, claimed: false);
        var afterRestart = BuildStore(config).Find(runner.Id);
        Assert.Equal(ClientIdentityKind.Retired, afterRestart?.Kind);
        Assert.False(BuildStore(config).IsRegistered(runner.Id));
    }

    [Fact]
    public void Retire_DoesNotCompleteWhenClaimPollOmitsTelemetry()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest { DisplayName = "telemetry-gap-runner", Kind = ClientIdentityKinds.Service });

        store.RecordRunnerActivity(runner.Id, activeSlots: 1, availableSlots: 1, claimed: true);
        store.RequestDrain(runner.Id, retireAfterDrain: true);

        var duringTelemetryGap = store.RecordRunnerActivity(
            runner.Id, activeSlots: null, availableSlots: 1, claimed: false);

        Assert.Equal(ClientIdentityKind.Service, duringTelemetryGap?.Kind);
        Assert.Equal(1, duringTelemetryGap?.RunnerActiveSlots);
        Assert.Equal(ClientIdentityKind.Service, BuildStore(config).Find(runner.Id)?.Kind);
    }

    [Fact]
    public void HostCapacity_IsSeededOnFirstContact_AndThenOwnedByTheOperator()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "capacity-runner",
            Kind = ClientIdentityKinds.Service,
        });

        var seeded = store.RecordRunnerActivity(
            runner.Id, activeSlots: 0, availableSlots: 1, claimed: false, seedMaxParallelism: 6);
        Assert.Equal(6, seeded?.RunnerDesiredMaxParallelism);
        Assert.Equal(HostCapacityPolicy.DefaultTargetLoadPercent, seeded?.RunnerTargetLoadPercent);
        Assert.Equal(RunnerRampStrategies.Balanced, seeded?.RunnerRampStrategy);

        var operatorSet = store.SetRunnerCapacity(runner.Id, 12, 85, "conservative");
        Assert.Equal(12, operatorSet?.RunnerDesiredMaxParallelism);
        Assert.Equal(85, operatorSet?.RunnerTargetLoadPercent);
        Assert.Equal(RunnerRampStrategies.Conservative, operatorSet?.RunnerRampStrategy);
        Assert.NotNull(operatorSet?.RunnerCapacityUpdatedAt);

        // A later daemon poll must not push its own bootstrap value back over
        // the operator's ceiling.
        var afterPoll = store.RecordRunnerActivity(
            runner.Id, activeSlots: 2, availableSlots: 99, claimed: false, seedMaxParallelism: 2);
        Assert.Equal(12, afterPoll?.RunnerDesiredMaxParallelism);
        Assert.Equal(12, BuildStore(config).Find(runner.Id)?.RunnerDesiredMaxParallelism);
    }

    [Fact]
    public void SlotLedger_DerivesFreeSlotsFromTheCeiling_NotFromTheDaemonReport()
    {
        var store = BuildStore(BuildConfig());
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "ledger-runner",
            Kind = ClientIdentityKinds.Service,
        });
        store.SetRunnerCapacity(runner.Id, 8, 80, RunnerRampStrategies.Balanced);

        // The daemon's breathing "1 free" used to become the ledger total
        // (active + 1); free must follow the ceiling instead.
        var busy = store.RecordRunnerActivity(
            runner.Id, activeSlots: 7, availableSlots: 1, claimed: true);
        Assert.Equal(7, busy?.RunnerActiveSlots);
        Assert.Equal(1, busy?.RunnerAvailableSlots);

        var quieter = store.RecordRunnerActivity(
            runner.Id, activeSlots: 2, availableSlots: 1, claimed: false);
        Assert.Equal(2, quieter?.RunnerActiveSlots);
        Assert.Equal(6, quieter?.RunnerAvailableSlots);
    }

    [Fact]
    public void HostCapacity_RecordsWhichCeilingTheDaemonAdopted()
    {
        var store = BuildStore(BuildConfig());
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "adoption-runner",
            Kind = ClientIdentityKinds.Service,
        });
        store.SetRunnerCapacity(runner.Id, 10, 80, RunnerRampStrategies.Balanced);

        var appliedAt = new DateTime(2026, 7, 27, 9, 30, 0, DateTimeKind.Utc);
        var reported = store.RecordRunnerActivity(
            runner.Id,
            activeSlots: 1,
            availableSlots: 0,
            claimed: false,
            effectiveMaxParallelism: 4,
            effectiveMaxParallelismAppliedAt: appliedAt);

        Assert.Equal(10, reported?.RunnerDesiredMaxParallelism);
        Assert.Equal(4, reported?.RunnerEffectiveMaxParallelism);
        Assert.Equal(appliedAt, reported?.RunnerEffectiveMaxParallelismAppliedAt);
    }

    [Fact]
    public void SetRunnerCapacity_ClampsOutOfRangeTargets_AndRefusesUnknownHosts()
    {
        var store = BuildStore(BuildConfig());
        var runner = store.Register(new RegisterClientRequest
        {
            DisplayName = "clamp-runner",
            Kind = ClientIdentityKinds.Service,
        });

        var clamped = store.SetRunnerCapacity(runner.Id, 9999, 10, "nonsense");
        Assert.Equal(256, clamped?.RunnerDesiredMaxParallelism);
        Assert.Equal(50, clamped?.RunnerTargetLoadPercent);
        Assert.Equal(RunnerRampStrategies.Balanced, clamped?.RunnerRampStrategy);
        Assert.Null(store.SetRunnerCapacity("no-such-host", 4, 80, "balanced"));
    }

    [Fact]
    public void SetRunnerCapacity_WithoutAMaxParallelism_KeepsTheCeiling_AndNeverInventsOne()
    {
        var store = BuildStore(BuildConfig());
        var undeclared = store.Register(new RegisterClientRequest
        {
            DisplayName = "undeclared-runner",
            Kind = ClientIdentityKinds.Service,
        });

        // Changing only the ramp on a host that never declared a capacity must
        // not conjure a ceiling: the server would start enforcing a cap nobody
        // asked for.
        var rampOnly = store.SetRunnerCapacity(undeclared.Id, null, null, "conservative");
        Assert.Null(rampOnly?.RunnerDesiredMaxParallelism);
        Assert.Equal(RunnerRampStrategies.Conservative, rampOnly?.RunnerRampStrategy);
        Assert.Equal(HostCapacityPolicy.DefaultTargetLoadPercent, rampOnly?.RunnerTargetLoadPercent);

        // With a ceiling in place, an omitted value keeps it unchanged.
        store.SetRunnerCapacity(undeclared.Id, 5, null, null);
        var loadOnly = store.SetRunnerCapacity(undeclared.Id, null, 90, null);
        Assert.Equal(5, loadOnly?.RunnerDesiredMaxParallelism);
        Assert.Equal(90, loadOnly?.RunnerTargetLoadPercent);
    }

    [Fact]
    public void RetiredHost_CanBeRevived_ThenPermanentlyDeleted()
    {
        var config = BuildConfig();
        var store = BuildStore(config);
        var runner = store.Register(new RegisterClientRequest { DisplayName = "revivable-runner", Kind = ClientIdentityKinds.Service });
        var retired = store.RequestDrain(runner.Id, retireAfterDrain: true);
        Assert.Equal(ClientIdentityKind.Retired, retired?.Kind);

        var revived = store.Revive(runner.Id);
        Assert.Equal(ClientIdentityKind.Service, revived?.Kind);
        Assert.True(store.IsRegistered(runner.Id));

        Assert.True(store.SoftDelete(runner.Id));
        Assert.True(store.PermanentlyDelete(runner.Id));
        Assert.Null(BuildStore(config).Find(runner.Id));
    }

    [Fact]
    public void DeletePolicy_RefusesEveryAuthorityGuard()
    {
        var now = DateTime.UtcNow;
        var retired = new ClientIdentity
        {
            Id = "guarded-runner",
            DisplayName = "guarded-runner",
            Kind = ClientIdentityKind.Retired,
            LastSeenAt = now,
        };

        Assert.Null(ClientIdentityDeletionPolicy.BlockedBy(
            retired, new ClientAttemptAuthorityFacts(false, false), now));
        Assert.Equal("runner-online", ClientIdentityDeletionPolicy.BlockedBy(
            retired with { RunnerDaemonState = "running" }, new ClientAttemptAuthorityFacts(false, false), now));
        Assert.Equal("active-lease", ClientIdentityDeletionPolicy.BlockedBy(
            retired, new ClientAttemptAuthorityFacts(true, false), now));
        Assert.Equal("process-unknown-attempt", ClientIdentityDeletionPolicy.BlockedBy(
            retired, new ClientAttemptAuthorityFacts(false, true), now));
    }

    [Fact]
    public void Scanner_MigratesLegacyJobToLocalDefault()
    {
        var config = BuildConfig();
        var scanner = BuildScanner(config);

        var dir = Path.Combine(_watchPath, TaskStates.Ready, "legacy-job");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            "{\"id\":\"legacy-job\",\"title\":\"Legacy\",\"state\":\"2-ready\",\"order\":1,\"agent\":\"copilot\"}");

        var jobs = scanner.ScanAllJobs();
        var legacy = Assert.Single(jobs);
        Assert.Equal(DefaultClientIdentity.Id, legacy.OwnerClientId);

        // The migration is sticky: the file on disk now contains the field.
        var rewritten = File.ReadAllText(Path.Combine(dir, "task.json"));
        Assert.Contains("\"ownerClientId\"", rewritten);
        Assert.Contains(DefaultClientIdentity.Id, rewritten);
    }

    [Fact]
    public void Scanner_KeepsExplicitOwnerClientId()
    {
        var config = BuildConfig();
        var scanner = BuildScanner(config);

        var dir = Path.Combine(_watchPath, TaskStates.Ready, "owned-job");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            "{\"id\":\"owned-job\",\"title\":\"Owned\",\"state\":\"2-ready\",\"order\":1,\"agent\":\"copilot\",\"ownerClientId\":\"layer-3-review\"}");

        var jobs = scanner.ScanAllJobs();
        var owned = Assert.Single(jobs);
        Assert.Equal("layer-3-review", owned.OwnerClientId);
    }

    [Fact]
    public void DefaultIdentityCannotBeSoftDeletedTwice()
    {
        var store = BuildStore(BuildConfig());
        store.EnsureLoaded();
        // The endpoint blocks default identity deletion; the store itself
        // allows it for tests / direct use, but the second call returns false
        // because the kind is already retired.
        Assert.True(store.SoftDelete(DefaultClientIdentity.Id));
        Assert.False(store.SoftDelete(DefaultClientIdentity.Id));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
