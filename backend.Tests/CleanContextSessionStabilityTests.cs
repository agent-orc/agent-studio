using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using AgentStudio.CliHosting;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the session-state stability contract for clean-context runs
/// (MKT-8 / WEB-14 "Codex rollout state loss"): all attempts/recoveries of the
/// same task must reuse ONE isolated config home so the CLI's session state
/// (Codex <c>sessions/rollout-*.jsonl</c>, Claude transcripts) survives a
/// restart between attempts, and the stored session id stays resumable. A
/// fresh home may be cut only on first task use or after inactivity retention.
/// Before this contract, every <c>StartAsync</c> seeded a brand-new CODEX_HOME,
/// which deleted the rollout
/// mid-task and forced each continuation into full-context recovery
/// ("Codex rollout is absent from the new clean-context CODEX_HOME").
/// </summary>
public class CleanContextSessionStabilityTests
{
    /// <summary>
    /// Minimal engine whose behavior supports clean context with a real
    /// on-disk task home per preparation - no process spawn involved; the
    /// tests drive <see cref="GenericCliExecutionService.AcquireCleanContext"/>
    /// directly.
    /// </summary>
    private sealed class FakeCleanCliService : GenericCliExecutionService, IDisposable
    {
        private readonly string _root;

        public FakeCleanCliService(string? root = null)
            : this(
                root ?? Path.Combine(Path.GetTempPath(), "clean-context-session-tests", Guid.NewGuid().ToString("N")),
                Path.Combine(Path.GetTempPath(), "clean-context-session-user", Guid.NewGuid().ToString("N")))
        {
        }

        private FakeCleanCliService(string root, string userHome)
            : base(
                BuildBehavior(root, userHome),
                NullLogger<FakeCleanCliService>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [TaskCleanContextStore.RootOverrideEnvironmentVariable] = root,
                }).Build())
        {
            _root = root;
            Directory.CreateDirectory(userHome);
        }

        private static CliBehavior BuildBehavior(string root, string userHome) => new()
        {
            CliType = CliTypes.Codex,
            GetCliPath = _ => "unused",
            SupportsCleanContext = true,
            PrepareCleanContext = (_, jobKey, _) => CleanContextPreparer.PrepareCodex(
                userHome,
                jobKey,
                NullLogger.Instance,
                root),
        };

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { /* test cleanup is best-effort */ }
        }
    }

    [Fact]
    public void AcquireCleanContext_SecondAttemptOfSameTask_ReusesTheHome()
    {
        using var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-1";

        var (first, firstReused) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        var (second, secondReused) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        try
        {
            Assert.NotNull(first);
            Assert.False(firstReused);
            Assert.NotNull(second);
            Assert.True(secondReused);
            // Same preparation object → same home → the rollout written by
            // attempt 1 is still there for attempt 2's resume.
            Assert.Same(first, second);
            Assert.True(Directory.Exists(second!.TempHome));
        }
        finally { first?.Dispose(); }
    }

    [Fact]
    public void AcquireCleanContext_DifferentTasks_GetIsolatedHomes()
    {
        using var svc = new FakeCleanCliService();

        var (a, _) = svc.AcquireCleanContext("proj::task-a", Path.GetTempPath());
        var (b, _) = svc.AcquireCleanContext("proj::task-b", Path.GetTempPath());
        try
        {
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotEqual(a!.TempHome, b!.TempHome);
        }
        finally
        {
            a?.Dispose();
            b?.Dispose();
        }
    }

    [Fact]
    public void AcquireCleanContext_HomeVanished_RecreatesTheDeterministicTaskPath()
    {
        using var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-2";

        var (first, _) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        Assert.NotNull(first);
        Directory.Delete(first!.TempHome, recursive: true);

        var (second, secondReused) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        try
        {
            Assert.NotNull(second);
            Assert.False(secondReused);
            Assert.Equal(first.TempHome, second!.TempHome);
            Assert.True(Directory.Exists(second.TempHome));
        }
        finally { second?.Dispose(); }
    }

    [Fact]
    public void GetPersistentCleanContextHome_ReflectsLiveRegistration()
    {
        using var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-3";

        Assert.Null(svc.GetPersistentCleanContextHome(jobKey));

        var (prep, _) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        Assert.NotNull(prep);
        Assert.Equal(prep!.TempHome, svc.GetPersistentCleanContextHome(jobKey));

        // A vanished home must read as "nothing to resume", not a dead path.
        Directory.Delete(prep.TempHome, recursive: true);
        Assert.Null(svc.GetPersistentCleanContextHome(jobKey));
    }

    [Fact]
    public void GetPersistentCleanContextHome_FeedsCodexResumeViability()
    {
        // End-to-end over the two pieces the runner composes: the persistent
        // per-task home plus CodexRolloutStore.CanResume. A rollout written by
        // attempt 1 into the shared task home makes the clean-context resume
        // viable for attempt 2.
        using var svc = new FakeCleanCliService();
        const string jobKey = "proj::task-4";
        const string sessionId = "019dee65-7a9b-7843-bfd9-06e555fff02b";

        var (prep, _) = svc.AcquireCleanContext(jobKey, Path.GetTempPath());
        Assert.NotNull(prep);
        try
        {
            var day = Path.Combine(prep!.TempHome, "sessions", "2026", "07", "30");
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, $"rollout-2026-07-30T10-00-00-{sessionId}.jsonl"), "{}\n");

            Assert.True(CodexRolloutStore.CanResume(
                sessionId, "clean",
                sharedHome: null,
                cleanHome: svc.GetPersistentCleanContextHome(jobKey)));
        }
        finally { prep?.Dispose(); }
    }

    [Fact]
    public void PersistentHome_SurvivesServiceRestart_AndKeepsCodexResumeViable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "clean-context-restart-tests",
            Guid.NewGuid().ToString("N"));
        const string jobKey = "proj::restart-task";
        const string sessionId = "019dee65-7a9b-7843-bfd9-06e555fff02b";

        using (var firstService = new FakeCleanCliService(root))
        {
            var (first, _) = firstService.AcquireCleanContext(jobKey, Path.GetTempPath());
            Assert.NotNull(first);
            var day = Path.Combine(first!.TempHome, "sessions", "2026", "08", "09");
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, $"rollout-2026-08-09T10-00-00-{sessionId}.jsonl"), "{}\n");
            first.Dispose();

            // A fresh service instance has an empty in-memory registry. The
            // marker-validated task path still resolves before StartAsync.
            using var restartedService = new FakeCleanCliService(root);
            var reopened = restartedService.GetPersistentCleanContextHome(jobKey);
            Assert.Equal(first.TempHome, reopened);
            Assert.True(CodexRolloutStore.CanResume(
                sessionId,
                CliContextModes.Clean,
                sharedHome: null,
                cleanHome: reopened));
        }
    }
}
