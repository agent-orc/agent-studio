using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-4239: the production start path (hosted services on, repair sweeps on)
/// on a workspace shaped like the installation whose start-up hung on 27
/// November 2026: a project with a repository and an origin, many cards with
/// persisted commits, an interrupted integration journal, conflict recovery
/// receipts on Ready and Progress cards, and an orchestration occurrence in
/// "applying". Before the fix the commit repository backfill read Git and
/// walked the whole task tree once per card before the listener, so this host
/// was not started within the deadline. The repairs now run behind the
/// started host; the interrupted integration is still repaired behind it.
/// <c>scripts/startup-fixture.sh</c> builds the same workspace for the product
/// start check.
/// <para>
/// AGT-4244: the start time is a wall-clock measurement, so the class runs in
/// a collection that does not run in parallel with the rest of the suite, and
/// its deadline is derived from the start of the same host on an empty
/// workspace measured in the same run (never below 60s, never above 180s). A
/// start that takes minutes, the incident class, still fails; a busy machine
/// only stretches the baseline and the deadline together. The failure names
/// the start-up phase that was running, read from the AGT-4239 phase log.
/// </para>
/// </summary>
[Collection(ProductStartupFixtureCollection.Name)]
public sealed class ProductStartupFixtureTests : IDisposable
{
    private const int Cards = 1500;
    // The incident morning; no recovery decision here depends on the age of a record.
    private static readonly DateTimeOffset FixtureTime = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    // The fixture start may take this many empty-workspace starts; the
    // fixed start measured 12 to 18s alone against a baseline of a few seconds.
    private const int BaselineFactor = 10;
    private static readonly TimeSpan MinStartDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxStartDeadline = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan RepairDeadline = TimeSpan.FromSeconds(120);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "startup-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly string _repo;
    private readonly string _interruptedCard;
    // Long-running installations carry keys in the project's short code
    // (auto-discovered as FIX) and owners, so first-boot migrations are no-ops.
    private int _nextKey;

    public ProductStartupFixtureTests()
    {
        _project = Path.Combine(_root, "projects", "fixture");
        _repo = Path.Combine(_root, "repo");
        _interruptedCard = Path.Combine(_project, TaskStates.AutoReview, "fixture-interrupted");
        SeedWorkspace();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A git or host handle can outlive the test briefly; the temp root is disposable.
        }
    }

    [Fact]
    public async Task The_real_start_path_is_healthy_before_its_repairs_and_the_repairs_then_finish()
    {
        var baseline = await MeasureEmptyWorkspaceStartAsync();
        var deadline = TimeSpan.FromTicks(Math.Clamp(
            baseline.Ticks * BaselineFactor, MinStartDeadline.Ticks, MaxStartDeadline.Ticks));

        var phases = new PhaseCapture();
        using var factory = ProductionStartHost(_root, _project, _repo, phases);

        var clock = Stopwatch.StartNew();
        var start = Task.Run(() => factory.CreateClient());
        var finished = await Task.WhenAny(start, Task.Delay(deadline));
        Assert.True(finished == start,
            $"The production start path was not started within {deadline.TotalSeconds:0}s "
            + $"({BaselineFactor} x the empty-workspace start of {baseline.TotalSeconds:0.0}s) on the {Cards}-card fixture. "
            + phases.Describe());
        using var client = await start;
        var started = clock.Elapsed;
        Console.WriteLine($"ProductStartupFixture: empty-workspace start {baseline.TotalSeconds:0.0}s, "
            + $"fixture start {started.TotalSeconds:0.0}s, deadline {deadline.TotalSeconds:0}s");
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var repairs = factory.Services.GetRequiredService<StartupRepairAdmission>();
        Assert.Contains(repairs.Snapshot(), repair => repair.Repair == "CommitRepositoryBackfill");
        await repairs.Drained.WaitAsync(RepairDeadline);
        Assert.Null(repairs.WaitingReason(StartupAdmission.Integration | StartupAdmission.Push));
        Assert.DoesNotContain(repairs.Snapshot(), repair => repair.Status != "completed");

        // The interrupted integration is repaired behind the started host too.
        var journalDeadline = Stopwatch.StartNew();
        while (IntegrationGateJournal.Read(_interruptedCard) is not null && journalDeadline.Elapsed < RepairDeadline)
            await Task.Delay(250);
        Assert.Null(IntegrationGateJournal.Read(_interruptedCard));
        Assert.True(started < deadline, $"started after {started}; {phases.Describe()}");
    }

    private static async Task<TimeSpan> MeasureEmptyWorkspaceStartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "startup-baseline-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "projects", "baseline");
        Directory.CreateDirectory(project);
        try
        {
            using var factory = ProductionStartHost(root, project, project, new PhaseCapture());
            var clock = Stopwatch.StartNew();
            var start = Task.Run(() => factory.CreateClient());
            var finished = await Task.WhenAny(start, Task.Delay(MaxStartDeadline));
            Assert.True(finished == start, $"The empty-workspace start did not finish within {MaxStartDeadline.TotalSeconds:0}s.");
            using var client = await start;
            return clock.Elapsed;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A host handle can outlive the baseline briefly; the temp root is disposable.
            }
        }
    }

    private static WebApplicationFactory<Program> ProductionStartHost(string root, string project, string repo, PhaseCapture phases)
        => new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("Startup:HostedServices", "true");
            builder.UseSetting("Startup:RepairSweeps", "true");
            builder.UseSetting("TaskRepository", root);
            builder.UseSetting("WatchPaths:0:Name", Path.GetFileName(project));
            builder.UseSetting("WatchPaths:0:Path", project);
            builder.UseSetting("WatchPaths:0:RootPath", repo);
            builder.UseSetting("WatchPaths:0:RepositoryPath", repo);
            builder.UseSetting("ReviewDecisionOrchestrator:Enabled", "false");
            builder.ConfigureLogging(logging => logging.AddProvider(phases));
        });

    /// <summary>
    /// Keeps the <c>startup-phase</c> and <c>startup-phase-slow</c> lines of
    /// the AGT-4239 phase log so a missed deadline names the running phase.
    /// </summary>
    private sealed class PhaseCapture : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public string Describe()
        {
            var lines = _lines.ToArray();
            var slow = lines.Where(line => line.StartsWith("startup-phase-slow", StringComparison.Ordinal)).ToList();
            var completed = lines.LastOrDefault(line => line.StartsWith("startup-phase ", StringComparison.Ordinal));
            var running = slow.Count > 0
                ? "Still running per the watchdog: " + string.Join(" | ", slow.TakeLast(3)) + "."
                : "No phase passed the watchdog threshold.";
            return $"{running} Last completed phase: {completed ?? "none (the phase logger was not attached yet)"}.";
        }

        public ILogger CreateLogger(string categoryName) => categoryName == "AgentStudio.StartupPhases" ? this : NullLoggerInstance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
        public void Dispose() { }

        private static readonly ILogger NullLoggerInstance = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    private void SeedWorkspace()
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(_project);
        Git(_root, "init", "-q", "--bare", "-b", "develop", origin);
        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "startup-fixture@example.invalid");
        Git(_repo, "config", "user.name", "Startup Fixture");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "fixture: base");
        Git(_repo, "remote", "add", "origin", origin);
        var shas = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            File.AppendAllText(Path.Combine(_repo, "a.txt"), $"change {i}\n");
            Git(_repo, "commit", "-q", "-am", $"fixture: change {i}");
            shas.Add(Git(_repo, "rev-parse", "HEAD"));
        }
        Git(_repo, "push", "-q", "origin", "develop");
        var develop = Git(_repo, "rev-parse", "HEAD");
        File.AppendAllText(Path.Combine(_repo, "a.txt"), "interrupted\n");
        Git(_repo, "commit", "-q", "-am", "fixture: interrupted merge");
        var gated = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "update-ref", "refs/heads/fixture-interrupted", gated);
        Git(_repo, "reset", "-q", "--hard", develop);

        string[] lanes = [TaskStates.Ready, TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Completed, TaskStates.Archive];
        for (var i = 0; i < Cards; i++)
        {
            var sha = shas[i % shas.Count];
            WriteCard(lanes[i % lanes.Length], $"fixture-{i}", i, [new
            {
                sha, shortSha = sha[..7], message = $"fixture: delivery {i}", filesChanged = 1, files = new[] { "a.txt" },
            }]);
        }

        WriteCard(TaskStates.AutoReview, "fixture-interrupted", 0, []);
        IntegrationGateJournal.Open(_interruptedCard, new IntegrationGateJournalEntry
        {
            Project = "fixture", JobId = "fixture-interrupted", RepoRoot = _repo, IntegrationBranch = "develop",
            PreMergeTip = develop, GatedSha = gated, StartedAt = FixtureTime,
            Phase = IntegrationGatePhase.GateRunning, Owner = "previous-process",
        });

        foreach (var lane in new[] { TaskStates.Ready, TaskStates.Progress })
        {
            var folder = WriteCard(lane, "fixture-conflict-" + lane, 0, []);
            File.WriteAllText(Path.Combine(folder, "integration-conflict-recovery.json"), JsonSerializer.Serialize(new
            {
                deliverySha = shas[0], target = "develop", originalDeliveredAtUtc = FixtureTime.AddHours(-1),
                result = new { outcome = "Conflict", conflictedFiles = new[] { "a.txt" } },
                recoveryQueued = true, reservedPaths = new[] { "a.txt" },
            }));
        }

        var occurrences = Path.Combine(_root, ".orchestration-occurrences");
        Directory.CreateDirectory(occurrences);
        var now = FixtureTime.UtcDateTime;
        File.WriteAllText(Path.Combine(occurrences, "orch_startupfixture.json"), JsonSerializer.Serialize(new
        {
            run = new
            {
                runId = "orch_startupfixture", projectId = "fixture", taskId = "fixture-0", definitionVersion = 1,
                status = "applying", currentStage = 0, payloadJson = "{}", reissueAttempts = 0,
                createdAt = now, updatedAt = now, completedAt = (DateTime?)null,
            },
            idempotencyKey = "startup-fixture-applying", expectedSourceState = TaskStates.AutoReview,
            authorityAttemptId = "startup-fixture-attempt", authorityFence = 1, authorityEpoch = 1, creationSequence = 1,
        }));
    }

    private string WriteCard(string lane, string id, int order, object[] commits)
    {
        var folder = Path.Combine(_project, lane, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, key = $"FIX-{++_nextKey}", title = "Startup fixture " + id, state = lane, order, agent = "codex",
            kind = "task", ownerClientId = "local-default", commits,
        }));
        return folder;
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductStartupFixtureCollection
{
    public const string Name = "ProductStartupFixture";
}
