using System.Diagnostics;
using AgentStudio.CliHosting;
using AgentStudio.TaskServer.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace AgentRunner.Tests;

public sealed class RunnerCleanContextsTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 3, 10, 0, TimeSpan.Zero);
    private readonly string _fixture = Path.Combine(Path.GetTempPath(), "clean-context-sweep-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_fixture, "clean-context");
    private string User => Path.Combine(_fixture, "user");

    private TaskCleanContextLease Seed(string key, string provider, int bytes, DateTimeOffset used, bool runnerManaged = false)
    {
        var lease = TaskCleanContextStore.Acquire(provider, key, User, Root, used,
            retention: TimeSpan.FromDays(30), runnerManaged: runnerManaged);
        File.WriteAllBytes(Path.Combine(lease.HomePath, "fixture-rollout"), new byte[bytes]);
        return lease;
    }

    [Fact]
    public async Task Sweep_removes_terminal_and_expired_homes_keeps_active_work_and_reports_usage()
    {
        if (!OperatingSystem.IsLinux()) return;
        var account = Path.Combine(User, ".codex", "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(account)!);
        var placeholder = Guid.NewGuid().ToByteArray();
        File.WriteAllBytes(account, placeholder);
        var active = new RunnerStateStore(Path.Combine(_fixture, "state"));
        var lease = new RunLeaseInfoDto("AGT-3", "runner", "runner", "host", 0, "test", "lease", 1,
            Now.UtcDateTime, Now.UtcDateTime.AddMinutes(5));
        active.Create("AGT-3", lease, Path.Combine(_fixture, "worktree"));
        using var completed = Seed("AGT-1", "codex", 1024, Now);
        using var archived = Seed("AGT-2", "claude", 2048, Now);
        using var running = Seed("AGT-3", "codex", 4096, Now.AddDays(-3));
        using var expired = Seed("AGT-4", "claude", 8192, Now.AddDays(-3));
        using var open = Seed("AGT-5", "codex", 16384, Now.AddDays(-2));
        Assert.Contains(completed.SeededFiles, seeded => seeded.Method == CleanContextSeedMethod.HardLink);

        var sweeper = new RunnerCleanContexts(Root, active.LoadAll, (hashes, _) =>
        {
            var terminal = new[] { TaskCleanContextStore.TaskIdentityHash("AGT-1"), TaskCleanContextStore.TaskIdentityHash("AGT-2") };
            return Task.FromResult(hashes.Intersect(terminal).ToArray());
        }, _ => { });
        var before = TaskCleanContextStore.Inventory(Root);
        var beforeBytes = before.Sum(home => RunnerPreparationCopies.Measure(new DirectoryInfo(home.Path)));
        Assert.Equal(5, before.Count);

        await sweeper.SweepAsync(Now, default);

        Assert.False(Directory.Exists(completed.HomePath));
        Assert.False(Directory.Exists(archived.HomePath));
        Assert.False(Directory.Exists(expired.HomePath));
        Assert.True(Directory.Exists(running.HomePath));
        Assert.True(Directory.Exists(open.HomePath));
        Assert.True(File.ReadAllBytes(account).SequenceEqual(placeholder), "The fixture account file must survive unchanged.");
        var after = Assert.IsType<CleanContextUsage>(sweeper.Usage);
        Assert.Equal(2, after.Count);
        Assert.True(after.Bytes < beforeBytes);
        output.WriteLine($"Clean contexts before: {before.Count} homes, {beforeBytes} bytes. After: {after.Count} homes, {after.Bytes} bytes.");

        var sampler = new HostTelemetrySampler(probe: FixedHostFilesystemProbe.Healthy, cleanContextUsage: () => after);
        var disk = Assert.Single(sampler.SampleNow(1).Filesystems!, disk => disk.Purpose == "clean-context");
        Assert.Equal(after.Bytes, disk.OwnedBytes!["clean-context (2 homes)"]);
    }

    [Theory]
    [InlineData("codex", "CODEX_HOME")]
    [InlineData("claude", "CLAUDE_CONFIG_DIR")]
    public async Task Process_environment_protects_an_expired_context_until_exit(string provider, string variable)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var expired = Seed("AGT-1", provider, 16, Now.AddDays(-3));
        var start = new ProcessStartInfo("/bin/sleep", "120") { UseShellExecute = false };
        start.Environment[variable] = expired.HomePath;
        using var child = Process.Start(start)!;
        var sweeper = new RunnerCleanContexts(Root, () => [], (_, _) => Task.FromResult(Array.Empty<string>()), _ => { });

        try
        {
            await sweeper.SweepAsync(Now, default);
            Assert.True(Directory.Exists(expired.HomePath));
        }
        finally
        {
            child.Kill();
            await child.WaitForExitAsync();
        }
        await sweeper.SweepAsync(Now, default);
        Assert.False(Directory.Exists(expired.HomePath));
    }

    [Fact]
    public async Task Launch_reservation_protects_a_context_before_the_provider_starts()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pending = Seed("AGT-1", "codex", 16, Now.AddDays(-3), runnerManaged: true);
        var sweeper = new RunnerCleanContexts(Root, () => [], (_, _) => Task.FromResult(Array.Empty<string>()), _ => { });
        await sweeper.SweepAsync(Now, default);
        Assert.True(Directory.Exists(pending.HomePath));
    }

    [Fact]
    public async Task Finished_auth_probe_releases_the_stripe_for_an_expired_task_home()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string provider = "codex";
        var probes = new ProviderAuthProbeContexts(
            cli => TaskCleanContextStore.Acquire(cli, ProviderAuthProbe.CleanContextIdentity, User, Root,
                nowUtc: Now.AddDays(-3), runnerManaged: true), _ => { });
        var sweeper = new RunnerCleanContexts(Root, () => [], (_, _) => Task.FromResult(Array.Empty<string>()), _ => { });
        using var probe = probes.Acquire(provider);
        Assert.NotNull(probe);
        var stripe = Path.GetFileName(probe.HomePath)[..2];
        var key = Enumerable.Range(1, 4096).Select(index => $"probe-stripe-task-{index}").First(task =>
            Path.GetFileName(TaskCleanContextStore.ResolveTaskHome(Root, provider, task))
                .StartsWith(stripe, StringComparison.Ordinal));
        using var expired = Seed(key, provider, 16, Now.AddDays(-3));
        using var overlappingProbe = probes.Acquire(provider);
        Assert.NotNull(overlappingProbe);
        probe.Dispose();
        await sweeper.SweepAsync(Now, default);
        Assert.True(Directory.Exists(expired.HomePath));

        overlappingProbe.Dispose();
        await sweeper.SweepAsync(Now, default);
        Assert.False(Directory.Exists(expired.HomePath));
        Assert.True(Directory.Exists(probe.HomePath));
    }

    [Fact]
    public async Task Sweep_skips_linked_roots_and_homes_and_unlinks_nested_links()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var expired = Seed("AGT-1", "codex", 16, Now.AddDays(-3));
        var outside = Directory.CreateDirectory(Path.Combine(_fixture, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep"), "untouched");
        Directory.CreateSymbolicLink(Path.Combine(Root, "claude"), outside);
        Directory.CreateSymbolicLink(TaskCleanContextStore.ResolveTaskHome(Root, "codex", "AGT-2"), outside);
        Directory.CreateSymbolicLink(Path.Combine(expired.HomePath, "outside"), outside);
        var linkedRoot = Path.Combine(_fixture, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, Root);
        RunnerCleanContexts Sweep(string root) => new(root, () => [], (_, _) => Task.FromResult(Array.Empty<string>()), _ => { });

        await Sweep(linkedRoot).SweepAsync(Now, default);
        Assert.True(Directory.Exists(expired.HomePath));
        await Sweep(Root).SweepAsync(Now, default);
        Assert.False(Directory.Exists(expired.HomePath));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "keep")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixture)) Directory.Delete(_fixture, recursive: true);
    }
}
