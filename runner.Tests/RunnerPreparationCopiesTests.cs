using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using AgentStudio.TestSupport;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerPreparationCopiesTests : IDisposable
{
    private static readonly DateTime AttemptTime = new(2026, 11, 27, 23, 5, 0, DateTimeKind.Utc);
    private readonly string _work = Path.Combine(Path.GetTempPath(), "preparation-copies-" + Guid.NewGuid().ToString("N"));
    private readonly RunnerStateStore _state;

    public RunnerPreparationCopiesTests() => _state = new RunnerStateStore(Path.Combine(_work, "state"));

    private PersistedRunnerSlot Claim(string card = "card", string project = "project")
    {
        var lease = new RunLeaseInfoDto(card, "runner", "runner", "host", 0, "test",
            Guid.NewGuid().ToString("N"), 1, AttemptTime, AttemptTime.AddMinutes(5));
        return _state.Create(card, lease, Path.Combine(_work, project, "worktrees", card), projectId: project);
    }

    private string Prepare(PersistedRunnerSlot slot, int size, int days = 0)
    {
        var path = RunnerPreparationCopies.RootFor(_work, slot)!;
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "package"), new byte[size]);
        Directory.SetLastWriteTimeUtc(path, AttemptTime.AddDays(-days));
        return path;
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("superseded")]
    public async Task Ending_an_attempt_releases_its_copy(string end)
    {
        var slot = Claim();
        var path = Prepare(slot, 100);
        var next = Claim(end == "superseded" ? slot.TaskKey : "other-card");
        var nextPath = Prepare(next, 40);
        var task = RunnerPreparationCopies.DuringAttemptAsync(_work, slot, _state, CancellationToken.None,
            () => end switch
            {
                "success" => Task.FromResult(0),
                "failure" => Task.FromException<int>(new IOException("preparation failed")),
                "cancel" => Task.FromCanceled<int>(new CancellationToken(true)),
                "superseded" => Task.FromResult(3), // lease-loss return in RunPersistedCoreAsync
                _ => throw new InvalidOperationException()
            }, _ => { });
        if (end == "failure") await Assert.ThrowsAsync<IOException>(() => task);
        else if (end == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.Equal(end == "success" ? 0 : 3, await task);
        Assert.False(Directory.Exists(path));
        Assert.True(Directory.Exists(nextPath));
    }

    [SkippableFact]
    public async Task A_new_round_rehydrates_the_published_entry_after_the_previous_copy_is_released()
    {
        PlatformGate.RequiresPosixShell();
        var first = Claim();
        var config = Directory.CreateDirectory(Path.Combine(first.WorktreePath, ".agent-studio")).FullName;
        File.WriteAllText(Path.Combine(config, "project.yml"), """
            schemaVersion: 1
            stack: [node]
            commands:
              prepare: .agent-studio/prepare
            """);
        File.WriteAllText(Path.Combine(config, "prepare"), """
            #!/bin/sh
            set -eu
            test -f "$NPM_CONFIG_CACHE/package" || printf published > "$NPM_CONFIG_CACHE/package"
            """);
        var cache = Path.Combine(_work, first.ProjectId!, "caches", "preparation");
        async Task<bool> Round(PersistedRunnerSlot slot)
        {
            var result = await ProjectPreparationExecutor.RunAsync(slot.WorktreePath, cache,
                Path.Combine(_work, "manifest.json"), "test", null, TimeSpan.FromSeconds(30),
                CancellationToken.None, slot.PreparationCopyId);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal(RunnerPreparationCopies.RootFor(_work, slot), result.RunRoot);
            var package = Path.Combine(result.Environment["NPM_CONFIG_CACHE"], "package");
            Assert.Equal("published", File.ReadAllText(package));
            File.WriteAllText(package, "modified by this round");
            return result.CacheHit;
        }
        Assert.False(await RunnerPreparationCopies.DuringAttemptAsync(_work, first, _state, CancellationToken.None,
            () => Round(first), _ => { }));
        Assert.False(Directory.Exists(RunnerPreparationCopies.RootFor(_work, first)));
        var next = Claim();
        Assert.NotEqual(first.PreparationCopyId, next.PreparationCopyId);
        Assert.True(await RunnerPreparationCopies.DuringAttemptAsync(_work, next, _state, CancellationToken.None,
            () => Round(next), _ => { }));
    }

    [Fact]
    public async Task Daemon_handoff_preserves_a_worker_result_until_reattachment_finishes()
    {
        var slot = Claim();
        var path = Prepare(slot, 123);
        File.WriteAllText(Path.Combine(slot.WorkerDirectory, "result.json"), JsonSerializer.Serialize(
            new DetachedJobResult(0, "done", "", false, AttemptTime), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await RunnerPreparationCopies.DuringAttemptAsync(_work, slot, _state, new CancellationToken(true),
            () => Task.FromResult(0), _ => { });
        Assert.True(Directory.Exists(path));
        var replacement = new RunnerStateStore(_state.Root);
        Assert.Equal(123, RunnerPreparationCopies.Sweep(_work, replacement.LoadAll, 0, true, _ => { }));
        await RunnerPreparationCopies.DuringAttemptAsync(_work, replacement.LoadAll().Single(), replacement,
            CancellationToken.None, () => Task.FromResult(0), _ => { });
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Startup_discards_unknown_copies_and_keeps_a_live_legacy_worker_binding()
    {
        var slot = Claim();
        var path = Prepare(slot, 10, days: 3);
        var unknown = Claim("abandoned");
        var abandoned = Prepare(unknown, 12);
        _state.Delete(unknown);
        _state.Save(slot with { PreparationCopyId = null });
        File.WriteAllText(Path.Combine(slot.WorkerDirectory, "spec.json"), JsonSerializer.Serialize(new
        {
            environment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(path, "nuget") }
        }));

        Assert.Equal(10, RunnerPreparationCopies.Sweep(_work, _state.LoadAll, 100, true, _ => { }));
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(path));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task A_surviving_child_keeps_its_copy_through_terminal_cleanup_startup_and_pressure(bool recovered)
    {
        PlatformGate.LinuxOnly("legacy process cache bindings are read from /proc");
        var slot = Claim();
        var path = Prepare(slot, 21);
        _state.Delete(slot);
        var start = new ProcessStartInfo("sleep", "90") { UseShellExecute = false };
        start.Environment["NUGET_PACKAGES"] = Path.Combine(path, "nuget");
        using var process = Process.Start(start)!;
        try
        {
            if (recovered)
                RunnerPreparationCopies.Release(_work, slot, _ => { });
            else
                await RunnerPreparationCopies.DuringAttemptAsync(_work, slot, _state, CancellationToken.None,
                    () => Task.FromResult(0), _ => { });
            Assert.True(File.Exists(Path.Combine(path, "package")));
            Assert.Equal(21, RunnerPreparationCopies.Sweep(_work, _state.LoadAll, 0, false, _ => { }));
            Assert.Equal(21, RunnerPreparationCopies.Sweep(_work, _state.LoadAll, 0, true, _ => { }));
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
        Assert.Equal(0, RunnerPreparationCopies.Sweep(_work, _state.LoadAll, 100, false, _ => { }));
        Assert.False(Directory.Exists(path));
    }

    private string LinkRunsDirectory(PersistedRunnerSlot slot)
    {
        var copy = RunnerPreparationCopies.RootFor(_work, slot)!;
        var runs = Directory.GetParent(copy)!;
        runs.Parent!.Create();
        var target = Directory.CreateDirectory(Path.Combine(_work, "relocated")).FullName;
        Directory.CreateSymbolicLink(runs.FullName, target);
        return Path.Combine(target, Path.GetFileName(copy));
    }

    [SkippableTheory]
    [InlineData("cwd", false)]
    [InlineData("fd", false)]
    [InlineData("argument", false)]
    [InlineData("cwd", true)]
    [InlineData("fd", true)]
    [InlineData("argument", true)]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    [Trait("Category", "MachineBound")]
    public async Task Terminal_release_preserves_child_references_without_cache_environment(string reference, bool linkRuns)
    {
        PlatformGate.LinuxOnly("inspects a live child's procfs links");
        var slot = Claim();
        var workAlias = Directory.CreateSymbolicLink(Path.Combine(_work, "alias"), ".").FullName;
        var path = RunnerPreparationCopies.RootFor(workAlias, slot)!;
        var childPath = linkRuns ? LinkRunsDirectory(slot) : path;
        Prepare(slot, 21);
        _state.Delete(slot);
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.Environment.Clear();
        start.ArgumentList.Add("-c");
        // Supplying the working path over stdin keeps the fd and cwd cases out of argv.
        var bind = reference switch
        {
            "cwd" => "cd \"$root\"",
            "fd" => "exec 3<\"$root/package\"",
            _ => ":"
        };
        start.ArgumentList.Add($"set -eu; read -r root; {bind}; echo ready; read -r done");
        if (reference == "argument") start.ArgumentList.Add(Path.Combine(childPath, "package"));
        using var child = Process.Start(start)!;
        try
        {
            await child.StandardInput.WriteLineAsync(childPath);
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            RunnerPreparationCopies.Release(workAlias, slot, _ => { });
            Assert.True(File.Exists(Path.Combine(path, "package")));
            if (!linkRuns)
                Assert.Equal(21, RunnerPreparationCopies.Sweep(workAlias, _state.LoadAll, 0, true, _ => { }));
        }
        finally
        {
            child.StandardInput.Close();
            await child.WaitForExitAsync();
        }
        if (linkRuns)
            RunnerPreparationCopies.Release(workAlias, slot, _ => { });
        else
            Assert.Equal(0, RunnerPreparationCopies.Sweep(workAlias, _state.LoadAll, 100, false, _ => { }));
        Assert.False(Directory.Exists(path));
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void A_file_mapping_keeps_the_copy_after_its_file_descriptor_closes()
    {
        PlatformGate.LinuxOnly("inspects a package mapping in procfs");
        var slot = Claim();
        LinkRunsDirectory(slot);
        var path = Prepare(slot, 21);
        _state.Delete(slot);
        var package = Path.Combine(path, "package");
        using (var mapping = MemoryMappedFile.CreateFromFile(package, FileMode.Open))
        using (var view = mapping.CreateViewAccessor())
        {
            mapping.Dispose();
            RunnerPreparationCopies.Release(_work, slot, _ => { });
            Assert.True(File.Exists(package));
            Assert.Equal(0, view.ReadByte(0));
        }
        RunnerPreparationCopies.Release(_work, slot, _ => { });
        Assert.False(Directory.Exists(path));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    [Trait("Category", "MachineBound")]
    public async Task Sweep_retries_a_slotless_copy_after_its_last_process_exits(bool startup)
    {
        PlatformGate.LinuxOnly("inspects a live child's cache binding");
        var slot = Claim();
        var path = Prepare(slot, 21);
        _state.Delete(slot);
        var start = new ProcessStartInfo("sleep", "90") { UseShellExecute = false };
        start.Environment["NUGET_PACKAGES"] = Path.Combine(path, "nuget");
        using var child = Process.Start(start)!;
        try
        {
            Assert.Equal(21, RunnerPreparationCopies.Sweep(_work, _state.LoadAll, startup ? 100 : 0, startup, _ => { }));
            Assert.True(File.Exists(Path.Combine(path, "package")));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
        Assert.Equal(0, RunnerPreparationCopies.Sweep(_work, _state.LoadAll, 100, false, _ => { }));
        Assert.False(Directory.Exists(path));
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Failed_prepare_leaves_a_surviving_childs_copy_for_the_runner_to_release()
    {
        PlatformGate.LinuxOnly("a prepare script leaves a child with an inherited npm binding");
        var slot = Claim();
        var path = RunnerPreparationCopies.RootFor(_work, slot)!;
        var config = Directory.CreateDirectory(Path.Combine(slot.WorktreePath, ".agent-studio")).FullName;
        File.WriteAllText(Path.Combine(config, "project.yml"), """
            schemaVersion: 1
            stack: [node]
            commands:
              prepare: .agent-studio/prepare
            """);
        File.WriteAllText(Path.Combine(config, "prepare"), """
            #!/bin/sh
            sleep 90 </dev/null >/dev/null 2>&1 &
            echo $! > child.pid
            echo data > "$NPM_CONFIG_CACHE/package"
            exit 1
            """);
        var preparation = await RunnerPreparationCopies.DuringAttemptAsync(_work, slot, _state,
            CancellationToken.None, () => ProjectPreparationExecutor.RunAsync(slot.WorktreePath,
                Directory.GetParent(path)!.Parent!.FullName, Path.Combine(_work, "manifest.json"), "test",
                null, TimeSpan.FromSeconds(30), CancellationToken.None, slot.PreparationCopyId), _ => { });
        var pid = int.Parse(File.ReadAllText(Path.Combine(slot.WorktreePath, "child.pid")));
        using var child = Process.GetProcessById(pid);
        try
        {
            Assert.False(preparation.Succeeded);
            Assert.Equal("data", File.ReadAllText(Path.Combine(path, "npm", "package")).Trim());
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void Bound_counts_live_bytes_across_projects_and_removes_the_oldest_unreferenced_first()
    {
        var live = Prepare(Claim(), 20, days: 10);
        var old = Claim("old", "other");
        var oldest = Prepare(old, 30, days: 8);
        var newer = Claim("newer");
        var newest = Prepare(newer, 40);
        _state.Delete(old);
        _state.Delete(newer);
        var relativeWork = Path.GetRelativePath(Environment.CurrentDirectory, _work);
        Assert.Equal(60, RunnerPreparationCopies.Sweep(relativeWork, _state.LoadAll, 60, false, _ => { }));
        Assert.False(Directory.Exists(oldest));
        Assert.True(Directory.Exists(newest));
        Assert.Equal(20, RunnerPreparationCopies.Sweep(_work, _state.LoadAll, 1, false, _ => { }));
        Assert.True(Directory.Exists(live));
    }

    [Fact]
    public async Task Eighteen_active_preparations_survive_the_budget_and_end_without_residue()
    {
        // Logical payload size of the three cache bindings in this host's preparation manifest.
        // Linux uses sparse files for the host-size replay. Keep the fixture small on
        // filesystems where extending an ordinary file can allocate its entire length.
        var bytesPerAttempt = OperatingSystem.IsLinux() ? 150_456_139L + 532_692_328L + 658_600_745L : 4096;
        var limit = 15 * bytesPerAttempt;
        var slots = Enumerable.Range(1, 18).Select(i => Claim($"card-{i}")).ToList();
        foreach (var slot in slots)
        {
            var path = Prepare(slot, 0);
            using var payload = File.OpenWrite(Path.Combine(path, "package"));
            payload.SetLength(bytesPerAttempt);
        }
        Assert.Equal(18 * bytesPerAttempt, RunnerPreparationCopies.Sweep(_work, _state.LoadAll,
            limit, true, _ => { }));
        foreach (var slot in slots)
            await RunnerPreparationCopies.DuringAttemptAsync(_work, slot, _state, CancellationToken.None,
                () => Task.FromResult(0), _ => { });
        Assert.Equal(0, RunnerPreparationCopies.Sweep(_work, _state.LoadAll,
            limit, false, _ => { }));
    }

    [Fact]
    public void Host_sample_reports_the_measured_bytes_and_disk_hold_reason()
    {
        Prepare(Claim(), 180);
        var options = new RunnerOptions { WorkDir = _work, ServerUrl = "http://test", RunnerId = "runner",
            RunnerName = "runner", Hostname = "host", BackendName = "test", BaseBranch = "main" };
        var copies = new RunnerPreparationCopies(options, _state, _ => { });
        copies.Sweep(startup: true);
        var sample = new HostTelemetrySampler(options, new FixedHostFilesystemProbe(23_000_000_000, 460_000_000_000),
            () => copies.Bytes).SampleNow(1);
        Assert.Equal(180, sample.Filesystems!.First().OwnedBytes![HostDiskPressurePolicy.PreparationCopies]);
        Assert.Equal("disk pressure on the run host, 23 GB free, of which 0 GB preparation copies",
            HostDiskPressurePolicy.Decide(sample.Filesystems, new()).Reason);
    }

    public void Dispose() => Directory.Delete(_work, true);
}
