using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: lease observations are explicit fixed instants; creation assertions never compare timestamps.

public sealed class DispatchSourceWaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-wave-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void Batch_assigns_one_wave_per_source_before_creating_any_card(int count, bool wave)
    {
        var cards = Enumerable.Range(0, count).Select(i => new CreateTaskRequest
            { Title = "Finding " + i, Project = "p", CreationSource = "playthrough" }).ToArray();
        var prepared = DispatchSourceMetadata.Prepare(cards.Concat([
            new() { Title = "Review", Project = "p", CreationSource = "review-findings" },
            new() { Title = "Other source", Project = "q", CreationSource = "scout" },
            new() { Title = "Explicit", Project = "p", CreationSource = "review-findings", WaveId = "source-wave" },
        ]).ToArray());
        Assert.Equal(wave, prepared[0].WaveId is not null);
        Assert.Single(prepared.Take(count).Select(c => c.WaveId).Distinct());
        Assert.Null(prepared[count].WaveId);
        Assert.Null(prepared[count + 1].WaveId);
        Assert.Equal("source-wave", prepared[count + 2].WaveId);
        Assert.All(cards, c => Assert.Null(c.WaveId));
    }

    [Fact]
    public void Source_threshold_covers_the_whole_request_across_destination_projects()
    {
        var cards = DispatchSourceMetadata.Prepare(Enumerable.Range(0, 6).Select(i => new CreateTaskRequest
            { Title = "Finding " + i, Project = i % 2 == 0 ? "p" : "q", CreationSource = "playthrough" }).ToArray());
        Assert.NotNull(cards[0].WaveId);
        Assert.Single(cards.Select(c => c.WaveId).Distinct());
    }

    [Theory]
    [InlineData(999, "wave", 999)]
    [InlineData(0, "wave", 0)]
    [InlineData(999, null, 10)]
    public void Source_defaults_are_unpinned_and_explicit_owner_positions_survive(int requested, string? wave, int expected)
        => Assert.Equal(expected, DispatchWavePolicy.InitialOrder(requested, 10, wave));

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData(" ../outside.cs ")]
    [InlineData(" /absolute.cs ")]
    [InlineData("/absolute.cs")]
    [InlineData("a\\b.cs")]
    [InlineData("a.cs:12")]
    public void Cited_paths_are_repository_relative_files(string path)
        => Assert.False(DispatchSourceMetadata.Valid(new() { CitedPaths = [path] }));

    [Theory]
    [InlineData("shadow", TaskStates.Backlog)]
    [InlineData("live", TaskStates.Backlog)]
    [InlineData("off", TaskStates.Ready)]
    public void Creation_persists_source_metadata_and_obeys_wave_landing(string mode, string expected)
    {
        var path = Path.Combine(_root, "project");
        Directory.CreateDirectory(path);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root, ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = path, ["WatchPaths:0:RootPath"] = path,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetDispatchMode("demo", mode);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        machine.EnsureStateFoldersAndMigrate();
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, dispatchSettings: settings);
        var id = mutations.CreateJob(new() { Title = "Wave finding", WatchPath = path, WaveId = "wave-1",
            CreationSource = "playthrough", CitedPaths = ["backend/A.cs", "backend/A.cs"], TargetState = TaskStates.Ready });
        var card = scanner.FindJob(id!, path)!;
        Assert.Equal(expected, card.State);
        Assert.Equal("wave-1", card.WaveId);
        Assert.Equal("playthrough", card.CreationSource);
        Assert.Equal(["backend/A.cs"], card.CitedPaths);
        var ordinary = mutations.CreateJob(new() { Title = "Owner card", WatchPath = path, TargetState = TaskStates.Ready });
        Assert.Equal(TaskStates.Ready, scanner.FindJob(ordinary!, path)!.State);
    }

    [Fact]
    public void Unfinished_wave_release_blocks_a_planned_admission_and_a_completed_wave_keeps_it()
    {
        var planned = new DispatchPickupDecision(true, "first-slot");
        Assert.False(DispatchWavePolicy.Pickup(planned, "wave", false).Admit);
        Assert.Same(planned, DispatchWavePolicy.Pickup(planned, "wave", true));
        Assert.Same(planned, DispatchWavePolicy.Pickup(planned, null, false));
    }

    [Fact]
    public void Release_marker_survives_restart_and_stale_leases_cannot_open_pickup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Dispatch:StoragePath"] = _root }).Build();
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new DispatchStore(config);
        var lease = store.AcquireLease("p", "area-warden", 0, now, TimeSpan.FromHours(1), true, true, true)!;
        var release = new DispatchWaveReleaseV1("decision", "area-warden", lease.Fence, ["A", "B"], false);
        Assert.True(store.WriteWaveRelease("p", "wave", release, now));
        var restarted = new DispatchStore(config);
        Assert.False(restarted.ReadWaveRelease("p", "wave")!.Complete);
        Assert.False(restarted.WriteWaveRelease("p", "wave", release with { Complete = true }, now.AddHours(1)));
        Assert.False(restarted.ReadWaveRelease("p", "wave")!.Complete);
        Assert.True(restarted.WriteWaveRelease("p", "wave", release with { Complete = true }, now));
        Assert.True(new DispatchStore(config).ReadWaveRelease("p", "wave")!.Complete);
    }

    [Theory]
    [InlineData("shadow", true, true, true, false)]
    [InlineData("off", true, true, true, false)]
    [InlineData("live", false, true, true, false)]
    [InlineData("live", true, false, true, false)]
    [InlineData("live", true, true, false, false)]
    [InlineData("live", true, true, true, true)]
    public void Release_requires_live_authority_whole_wave_plan_and_settled_recut(string mode, bool lease,
        bool plan, bool recut, bool expected)
        => Assert.Equal(expected, DispatchWavePolicy.MayRelease(mode, lease, plan, recut));
}
