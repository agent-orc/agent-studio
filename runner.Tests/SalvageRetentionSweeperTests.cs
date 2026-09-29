using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>The retention sweep over a real salvage directory and a real Git origin (AGT-2999).</summary>
public sealed class SalvageRetentionSweeperTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "salvage-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly string _salvage;
    private readonly string _state;
    private readonly string _workDir;
    private readonly List<string> _logs = [];

    public SalvageRetentionSweeperTests()
    {
        _salvage = Path.Combine(_root, "salvage");
        _state = Path.Combine(_root, "state");
        _workDir = Path.Combine(_root, "work");
        Directory.CreateDirectory(_salvage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Apply_deletes_expired_and_over_cap_tarballs_and_logs_each_with_its_size()
    {
        Write("PROJ-002-AGT-1-0100.tgz", 100, Now.AddDays(-40));
        Write("PROJ-002-AGT-1-0200.tgz", 200, Now.AddDays(-39));
        Write("AGT-2-0100.tgz", 10, Now.AddDays(-10));
        Write("AGT-2-0200.tgz", 20, Now.AddDays(-9));
        Write("AGT-2-0300.tgz", 30, Now.AddDays(-8));
        Write("AGT-2-0400.tgz", 40, Now.AddDays(-7));
        Write("AGT-3-0100.tgz", 5, Now.AddDays(-50));
        Write("snap.sh", 1, Now.AddDays(-80));
        var sweeper = Sweeper(
            Cards(
                new SalvageCardFacts("AGT-1", SalvageCardLifecycle.Terminal, Now.AddDays(-20)),
                new SalvageCardFacts("AGT-2", SalvageCardLifecycle.Open, null),
                new SalvageCardFacts("AGT-3", SalvageCardLifecycle.Terminal, Now.AddDays(-2))));

        var sweep = await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(
            ["AGT-2-0200.tgz", "AGT-2-0300.tgz", "AGT-2-0400.tgz", "AGT-3-0100.tgz", "snap.sh"],
            Directory.EnumerateFiles(_salvage).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(3, sweep.TarballsDeleted);
        Assert.Equal(310, sweep.TarballBytesDeleted);
        Assert.Equal("completed", sweep.Status);
        Assert.Contains(_logs, line => line ==
            "salvage-retention deleted kind=tarball entry=PROJ-002-AGT-1-0200.tgz card=AGT-1 bytes=200 reason=retention-elapsed");
        Assert.Contains(_logs, line => line ==
            "salvage-retention deleted kind=tarball entry=AGT-2-0100.tgz card=AGT-2 bytes=10 reason=over-per-card-limit");
        Assert.Contains(_logs, line => line.StartsWith(
            "salvage-retention sweep mode=apply status=completed tarballs=7 tarballsEligible=3 tarballsDeleted=3 bytesDeleted=310",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Report_mode_logs_candidates_and_deletes_nothing()
    {
        Write("AGT-1-0100.tgz", 100, Now.AddDays(-40));
        var sweeper = Sweeper(
            Cards(new SalvageCardFacts("AGT-1", SalvageCardLifecycle.Terminal, Now.AddDays(-20))),
            mode: SalvageRetentionSweeper.ModeReport);

        var sweep = await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_salvage, "AGT-1-0100.tgz")));
        Assert.Equal(1, sweep.TarballsEligible);
        Assert.Equal(100, sweep.TarballBytesEligible);
        Assert.Equal(0, sweep.TarballsDeleted);
        Assert.Contains(_logs, line => line ==
            "salvage-retention would-delete kind=tarball entry=AGT-1-0100.tgz card=AGT-1 bytes=100 reason=retention-elapsed");
    }

    [Fact]
    public async Task A_run_that_starts_while_facts_are_collected_still_protects_its_card()
    {
        Write("AGT-1-0100.tgz", 100, Now.AddDays(-40));
        Write("AGT-2-0100.tgz", 100, Now.AddDays(-40));
        var reads = 0;
        var sweeper = Sweeper(
            Cards(
                new SalvageCardFacts("AGT-1", SalvageCardLifecycle.Terminal, Now.AddDays(-20)),
                new SalvageCardFacts("AGT-2", SalvageCardLifecycle.Terminal, Now.AddDays(-20))),
            activeCardKeys: () => ++reads == 1 ? ["agt-2"] : ["AGT-1", "AGT-2"]);

        var sweep = await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_salvage, "AGT-1-0100.tgz")));
        Assert.True(File.Exists(Path.Combine(_salvage, "AGT-2-0100.tgz")));
        Assert.Equal(2, sweep.ProtectedByActiveRun);
        Assert.Equal(0, sweep.TarballsDeleted);
    }

    [Fact]
    public async Task Store_snapshot_reports_size_count_oldest_and_persists_the_last_sweep()
    {
        Write("AGT-1-0100.tgz", 100, Now.AddDays(-40));
        Write("AGT-2-0100.tgz", 30, Now.AddDays(-5));
        Write("snap.log", 3, Now.AddDays(-3));
        var sweeper = Sweeper(Cards(new SalvageCardFacts("AGT-1", SalvageCardLifecycle.Terminal, Now.AddDays(-20))));

        await sweeper.SweepOnceAsync(CancellationToken.None);
        var store = sweeper.RefreshInventory();

        Assert.True(store.Exists);
        Assert.Equal(33, store.SizeBytes);
        Assert.Equal(2, store.EntryCount);
        Assert.Equal(1, store.TarballCount);
        Assert.Equal(1, store.UnrecognizedCount);
        Assert.Equal("AGT-2-0100.tgz", store.OldestEntry);
        Assert.Equal(SalvageRetentionSweeper.ModeApply, store.Mode);
        Assert.Equal(14, store.RetentionDays);
        Assert.Equal(3, store.MaxPerCard);
        Assert.Equal(100, store.LastSweep!.TarballBytesDeleted);

        var persisted = JsonSerializer.Deserialize<SalvageSweepDto>(
            File.ReadAllText(Path.Combine(_state, SalvageRetentionSweeper.StateFileName)));
        Assert.Equal(store.LastSweep, persisted);
        var restarted = Sweeper(Cards());
        Assert.Equal(store.LastSweep, restarted.RefreshInventory().LastSweep);
    }

    [Fact]
    public void Store_snapshot_travels_in_host_telemetry()
    {
        Write("AGT-1-0100.tgz", 100, Now.AddDays(-40));
        var store = Sweeper(Cards()).RefreshInventory();
        var sample = new HostTelemetrySample(Now, null, null, null, null, null, null, null, null, null, null, 4, 0);

        var telemetry = RunnerCapabilityProbe.Telemetry(sample, salvageStore: store);
        var roundTrip = JsonSerializer.Deserialize<HostTelemetrySnapshotDto>(JsonSerializer.Serialize(telemetry));

        Assert.Equal(100, roundTrip!.SalvageStore!.SizeBytes);
        Assert.Equal(_salvage, roundTrip.SalvageStore.Path);
    }

    [Fact]
    public async Task Integrated_refs_of_a_completed_card_are_deleted_on_origin_and_everything_else_is_kept()
    {
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        await Git(_root, "init", "--bare", "--initial-branch=main", origin);
        await Git(_root, "init", "--initial-branch=main", seed);
        await Commit(seed, "base");
        var merged = await Commit(seed, "merged work");
        await Git(seed, "remote", "add", "origin", origin);
        await Git(seed, "push", "origin", "main");
        await Git(seed, "checkout", "-b", "side");
        var unmerged = await Commit(seed, "unmerged work");
        var mergedRef = $"agent-studio/salvage/runner-test/AGT-1/attempt-1/fence-1/{merged}";
        var unmergedRef = $"agent-studio/salvage/runner-test/AGT-1/attempt-2/fence-2/{unmerged}";
        var openRef = $"agent-studio/salvage/runner-test/AGT-2/attempt-1/fence-1/{merged}";
        var foreignRef = $"agent-studio/salvage/other-runner/AGT-1/attempt-1/fence-1/{merged}";
        await Git(seed, "push", "origin",
            $"{merged}:refs/heads/{mergedRef}",
            $"{unmerged}:refs/heads/{unmergedRef}",
            $"{merged}:refs/heads/{openRef}",
            $"{merged}:refs/heads/{foreignRef}");
        var clone = Path.Combine(_workDir, "PROJ-002", "repo");
        Directory.CreateDirectory(Path.GetDirectoryName(clone)!);
        await Git(_root, "clone", origin, clone);
        var sweeper = Sweeper(
            Cards(
                new SalvageCardFacts("AGT-1", SalvageCardLifecycle.Terminal, Now.AddDays(-20)),
                new SalvageCardFacts("AGT-2", SalvageCardLifecycle.Open, null)),
            refs: new GitSalvageRefStore(Options()));

        var sweep = await sweeper.SweepOnceAsync(CancellationToken.None);

        var remaining = (await Git(_root, "--git-dir", origin, "for-each-ref", "--format=%(refname:short)", "refs/heads/agent-studio"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(mergedRef, remaining);
        Assert.Contains(unmergedRef, remaining);
        Assert.Contains(openRef, remaining);
        Assert.Contains(foreignRef, remaining);
        Assert.Equal(3, sweep.RefsInspected);
        Assert.Equal(1, sweep.RefsDeleted);
        Assert.Equal(0, sweep.Failures);
        Assert.Contains(_logs, line => line ==
            $"salvage-retention deleted kind=ref ref={mergedRef} card=AGT-1 sha={merged} integration=main reason=retention-elapsed");
    }

    [Fact]
    public async Task Unreadable_project_clone_contributes_no_deletions_and_counts_as_a_failure()
    {
        var clone = Path.Combine(_workDir, "PROJ-002", "repo");
        Directory.CreateDirectory(clone);
        await Git(clone, "init", "--initial-branch=main");
        await Git(clone, "remote", "add", "origin", Path.Combine(_root, "missing.git"));
        var sweeper = Sweeper(Cards(), refs: new GitSalvageRefStore(Options()));

        var sweep = await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal("completed-with-failures", sweep.Status);
        Assert.Equal(1, sweep.Failures);
        Assert.Equal(0, sweep.RefsInspected);
        Assert.Contains(_logs, line => line.StartsWith("salvage-retention refs-skipped repo=", StringComparison.Ordinal));
    }

    private SalvageRetentionSweeper Sweeper(
        IReadOnlyDictionary<string, SalvageCardFacts> cards,
        string mode = SalvageRetentionSweeper.ModeApply,
        Func<IReadOnlyCollection<string>>? activeCardKeys = null,
        ISalvageRefStore? refs = null)
        => new(
            Options(mode),
            new FixedCards(cards),
            refs,
            activeCardKeys ?? (() => []),
            _logs.Add,
            () => Now);

    private RunnerOptions Options(string mode = SalvageRetentionSweeper.ModeApply) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-test",
        RunnerName = "runner-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = _workDir,
        StateDir = _state,
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
        SalvageDir = _salvage,
        SalvageRetentionMode = mode,
    };

    private static Dictionary<string, SalvageCardFacts> Cards(params SalvageCardFacts[] facts)
        => facts.ToDictionary(item => item.CardKey, StringComparer.Ordinal);

    private void Write(string name, int bytes, DateTime modified)
    {
        var path = Path.Combine(_salvage, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, modified);
    }

    private static async Task<string> Commit(string repo, string message)
    {
        await File.AppendAllTextAsync(Path.Combine(repo, "file.txt"), message + "\n");
        await Git(repo, "add", "--all");
        await Git(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", message);
        return await Git(repo, "rev-parse", "HEAD");
    }

    private static async Task<string> Git(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
        return result.StdOut.Trim();
    }

    private sealed class FixedCards(IReadOnlyDictionary<string, SalvageCardFacts> cards) : ISalvageCardDirectory
    {
        public Task<IReadOnlyDictionary<string, SalvageCardFacts>> ResolveAsync(
            IReadOnlyCollection<SalvageCardReference> references,
            CancellationToken ct)
            => Task.FromResult(cards);
    }
}
