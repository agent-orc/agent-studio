using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2818 acceptance for the one-off pickup-hold inventory. Both reported
/// cards are reconstructed here as they sat on disk, across two projects, so the
/// sweep is asserted against the real shapes rather than a synthetic one:
///
/// <list type="number">
///   <item>a release gate pointing at an archived, never released target,
///   reported as a gate that can never open (AGT-2373 waiting on AGT-2372);</item>
///   <item>a card carrying a durable runner refusal, reported with the runner,
///   code, reason, and how long it has been refused (AGT-2738);</item>
///   <item>a pickup-eligible card, which must not appear at all.</item>
/// </list>
/// </summary>
public sealed class PickupHoldSweepTests : IDisposable
{
    private const string App = "app";
    private const string Lib = "lib";
    private readonly string _workspaceRoot;
    private readonly string _appWatch;
    private readonly string _libWatch;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly FakeTimeProvider _clock;

    public PickupHoldSweepTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-pickup-hold-" + Guid.NewGuid().ToString("N"));
        _appWatch = Path.Combine(_workspaceRoot, "projects", App);
        _libWatch = Path.Combine(_workspaceRoot, "projects", Lib);
        foreach (var watch in new[] { _appWatch, _libWatch })
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(watch, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = App,
                ["WatchPaths:0:Path"] = _appWatch,
                ["WatchPaths:0:RootPath"] = _appWatch,
                ["WatchPaths:0:RepositoryPath"] = _appWatch,
                ["WatchPaths:1:Name"] = Lib,
                ["WatchPaths:1:Path"] = _libWatch,
                ["WatchPaths:1:RootPath"] = _libWatch,
                ["WatchPaths:1:RepositoryPath"] = _libWatch,
                ["TaskRepository"] = _workspaceRoot,
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _scanner.SetIndexCache(new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config));
        _settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ArchivedReleaseGate_IsReportedAsAGateThatCanNeverOpen()
    {
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372");
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");

        var held = Assert.Single(Sweep());

        Assert.Equal("AGT-2373", held.Key);
        Assert.Equal(App, held.ProjectName);
        Assert.Equal(TaskStates.Ready, held.State);
        Assert.Equal(PickupHoldMechanisms.DependencyGate, held.Hold.Mechanism);
        Assert.True(held.Hold.Unsatisfiable);
        Assert.Contains("AGT-2372", held.Hold.Reason, StringComparison.Ordinal);
        Assert.Contains("archived", held.Hold.Reason, StringComparison.OrdinalIgnoreCase);
        // A month of standstill, which is the number nobody could see.
        Assert.Equal(34, held.Hold.HeldForSeconds / 86400);
        Assert.Equal(
            new[] { PickupHoldResolutionKinds.DropDependency, PickupHoldResolutionKinds.RepointDependency, PickupHoldResolutionKinds.ArchiveWaitingCard },
            held.Hold.Resolutions.Select(resolution => resolution.Kind).ToArray());
    }

    [Fact]
    public void ReleasedArchivedTarget_LeavesTheDependentPickable()
    {
        // The first way out, taken by an operator: the sweep must go quiet.
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372", released: true);
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");

        Assert.Empty(Sweep());
    }

    [Fact]
    public void RefusedDispatch_IsReportedWithRunnerCodeReasonAndAge()
    {
        WriteJob(_appWatch, TaskStates.Ready, "installer-one-executable", "AGT-2738",
            enteredLaneAt: "2026-09-06T10:00:00Z",
            rejection: """
            {"code":"capability-mismatch","runnerId":"agent-runner-01","runnerName":"agent-runner-01",
             "reason":"Required capability 'task-server:connectivity' is advertised as unavailable.",
             "rejectedAtUtc":"2026-09-06T19:47:45Z"}
            """);

        var held = Assert.Single(Sweep());

        Assert.Equal("AGT-2738", held.Key);
        Assert.Equal(PickupHoldMechanisms.DispatchRejection, held.Hold.Mechanism);
        Assert.False(held.Hold.Unsatisfiable);
        Assert.Contains("agent-runner-01", held.Hold.Reason, StringComparison.Ordinal);
        Assert.Contains("capability-mismatch", held.Hold.Reason, StringComparison.Ordinal);
        Assert.Contains("task-server:connectivity", held.Hold.Reason, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 9, 6, 19, 47, 45, DateTimeKind.Utc), held.Hold.SinceUtc);
        Assert.Equal(7, held.Hold.HeldForSeconds / 86400);
    }

    [Fact]
    public void PickupEligibleCard_IsNotReported()
    {
        WriteJob(_appWatch, TaskStates.Ready, "workable", "APP-9");

        Assert.Empty(Sweep());
    }

    [Fact]
    public void HeldCardsAcrossProjects_AreReportedOldestHoldFirst()
    {
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372");
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");
        WriteJob(_libWatch, TaskStates.Ready, "epic-container", "LIB-7",
            kind: TaskKinds.Epic, enteredLaneAt: "2026-09-10T09:00:00Z");

        var held = Sweep();

        Assert.Equal(new[] { "AGT-2373", "LIB-7" }, held.Select(card => card.Key).ToArray());
        Assert.Equal(PickupHoldMechanisms.EpicContainer, held[1].Hold.Mechanism);
    }

    [Fact]
    public void RunOnce_ReportsTheBacklogWithoutChangingAnything()
    {
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372");
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");
        var before = File.ReadAllText(Path.Combine(_libWatch, TaskStates.Archive, "parity-suite", "task.json"));

        var held = BuildSweep().RunOnce();

        Assert.Single(held);
        // Report-only: releasing a validation gate is an operator decision, and
        // a boot sweep is not entitled to make it.
        Assert.Equal(
            before,
            File.ReadAllText(Path.Combine(_libWatch, TaskStates.Archive, "parity-suite", "task.json")));
        Assert.Equal(
            TaskStates.Ready,
            _scanner.ScanAllAutomationJobs().Single(job => job.Key == "AGT-2373").State);
    }

    private IReadOnlyList<HeldPickupCard> Sweep() => BuildSweep().Sweep();

    private PickupHoldSweep BuildSweep() =>
        new(_scanner, _settings, NullLogger<PickupHoldSweep>.Instance, _clock);

    private void WriteJob(
        string watchPath,
        string state,
        string slug,
        string key,
        string? dependsOn = null,
        bool releaseGate = false,
        bool released = false,
        string kind = TaskKinds.Task,
        string? enteredLaneAt = null,
        string? rejection = null)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var edge = dependsOn is null
            ? ""
            : $",\"references\":{{\"dependsOn\":[{{\"key\":\"{dependsOn}\",\"releaseGate\":{releaseGate.ToString().ToLowerInvariant()}}}]}}";
        var json =
            $"{{\"id\":\"{slug}\",\"key\":\"{key}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"kind\":\"{kind}\",\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"" +
            (released ? ",\"released\":true" : "") +
            (enteredLaneAt is null ? "" : $",\"enteredLaneAt\":\"{enteredLaneAt}\"") +
            (rejection is null ? "" : $",\"remoteDispatchRejection\":{rejection}") +
            $"{edge}}}";
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }
}
