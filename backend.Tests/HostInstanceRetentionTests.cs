using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using AgentStudio.Tasks;
using AgentStudio.Review;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

[CollectionDefinition("Instance retention", DisableParallelization = true)]
public sealed class InstanceRetentionCollection { }

[Collection("Instance retention")]
public sealed class HostInstanceRetentionTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));

    public HostInstanceRetentionTests()
    {
        Assert.StartsWith(TestTempRoot.SuiteRoot + Path.DirectorySeparatorChar,
            LocalStateRoot.Resolve(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sweep_keeps_current_manifest_pending_evidence_and_reservation_but_removes_old_empty_state()
    {
        using var state = new InstanceState();
        var now = _clock.GetUtcNow();
        var current = Create("current");
        var manifest = Create("manifest", "batch-gates/batch/manifest.json");
        var reservation = Create("reservation", "batch-push-reservations/project/push.pending");
        var pending = Create("pending", "batch-gates/pending/review.json");
        var unknown = Create("unknown", "one-shot-jobs/job.json");
        var empty = Create("empty", "batch-state-origin");
        Directory.CreateDirectory(Path.Combine(empty, "claimed-gate-canary"));
        Directory.CreateDirectory(Path.Combine(empty, "pull-gates"));
        var recent = Create("recent");
        Directory.SetLastWriteTimeUtc(recent, now.UtcDateTime);
        foreach (var root in new[] { current, manifest, reservation, pending, unknown, empty })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(file, now.AddDays(-30).UtcDateTime);
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
                Directory.SetLastWriteTimeUtc(dir, now.AddDays(-30).UtcDateTime);
        }
        var messages = new List<string>();

        Assert.Equal(1, HostStorageRetention.SweepInstances(current, now,
            HostStorageRetention.InstanceRetentionDays, messages.Add));

        Assert.False(Directory.Exists(empty));
        foreach (var root in new[] { current, manifest, reservation, pending, unknown, recent })
            Assert.True(Directory.Exists(root), root);
        foreach (var reason in new[] { "current-identity", "batch-manifest", "pending-reservation", "pending-evidence", "unrecognized-content", "recent" })
            Assert.Contains(messages, message => message.Contains("reason=" + reason, StringComparison.Ordinal));

        string Create(string name, string? file = null)
        {
            var root = state.PathFor(name);
            Directory.CreateDirectory(root);
            if (file is not null)
            {
                var path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{}");
            }
            return root;
        }
    }

    [Fact]
    public void Existing_host_retention_pass_also_sweeps_abandoned_instances()
    {
        using var temp = new TempWorkspace("host-retention-owner");
        using var state = new InstanceState();
        var current = state.PathFor("current");
        var abandoned = state.PathFor("abandoned");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(abandoned);
        Directory.SetLastWriteTimeUtc(abandoned, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(temp.Path, "workspace"),
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var messages = new List<string>();

        Assert.Equal(1, HostStorageRetention.Sweep(new BatchGateStore(Path.Combine(current, "batch-gates")),
            scanner, _clock.GetUtcNow(), 14, messages.Add));

        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(current));
    }

    [Fact]
    public void Sweep_does_not_follow_links_to_other_state()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new TempWorkspace("instance-retention-link");
        using var state = new InstanceState();
        var current = state.PathFor("current");
        Directory.CreateDirectory(current);
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "evidence"), "retain");
        Directory.CreateSymbolicLink(state.PathFor("linked"), outside);
        var old = state.PathFor("old");
        Directory.CreateDirectory(old);
        Directory.CreateSymbolicLink(Path.Combine(old, "linked"), outside);
        Directory.SetLastWriteTimeUtc(old, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        var messages = new List<string>();

        Assert.Equal(0, HostStorageRetention.SweepInstances(current, _clock.GetUtcNow(), 14, messages.Add));

        Assert.True(File.Exists(Path.Combine(outside, "evidence")));
        Assert.True(Directory.Exists(old));
        Assert.Contains(messages, message => message.Contains("reason=linked-state", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("instances")]
    [InlineData("agentstudio/instances")]
    public void Foreign_store_cannot_authorize_cleanup_even_with_matching_directory_names(string relative)
    {
        using var temp = new TempWorkspace("foreign-instance-retention");
        var instances = Path.Combine(temp.Path, relative);
        var current = Path.Combine(instances, "current");
        var abandoned = Path.Combine(instances, "abandoned");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(abandoned);
        Directory.SetLastWriteTimeUtc(abandoned, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        var messages = new List<string>();

        Assert.Equal(0, HostStorageRetention.SweepInstances(current, _clock.GetUtcNow(), 14, messages.Add));
        Assert.True(Directory.Exists(abandoned));
        Assert.Equal(0, SweepStore(Path.Combine(current, "batch-gates"), temp.Path, messages));
        Assert.True(Directory.Exists(abandoned));
        Assert.Contains(messages, message => message.Contains("reason=outside-instance-root", StringComparison.Ordinal));
    }

    [Fact]
    public void A_store_in_the_wrong_folder_of_the_current_instance_cannot_authorize_cleanup()
    {
        using var temp = new TempWorkspace("wrong-instance-store");
        using var state = new InstanceState();
        var current = state.PathFor("current");
        var abandoned = state.PathFor("abandoned");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(abandoned);
        Directory.SetLastWriteTimeUtc(abandoned, _clock.GetUtcNow().UtcDateTime.AddDays(-30));

        Assert.Equal(0, SweepStore(Path.Combine(current, "custom-store"), temp.Path, []));
        Assert.True(Directory.Exists(abandoned));
    }

    [Fact]
    public void Linked_current_identity_cannot_authorize_sibling_cleanup()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new TempWorkspace("linked-current-retention");
        using var state = new InstanceState();
        var current = state.PathFor("current");
        Directory.CreateSymbolicLink(current, temp.Path);
        var abandoned = state.PathFor("abandoned");
        Directory.CreateDirectory(abandoned);
        Directory.SetLastWriteTimeUtc(abandoned, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        var messages = new List<string>();

        Assert.Equal(0, HostStorageRetention.SweepInstances(current, _clock.GetUtcNow(), 14, messages.Add));
        Assert.True(Directory.Exists(abandoned));
        Assert.Contains(messages, message => message.Contains("reason=linked-instance-root", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("agentstudio")]
    [InlineData("agentstudio/instances")]
    public void Linked_state_ancestors_cannot_redirect_cleanup_outside_the_state_root(string relative)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new TempWorkspace("linked-instance-ancestor");
        var linked = LocalStateRoot.Combine(relative);
        var backup = Path.Combine(temp.Path, "original");
        var target = Path.Combine(temp.Path, "foreign");
        var suffix = Path.GetRelativePath(linked, LocalStateRoot.Combine("agentstudio", "instances"));
        var current = Path.Combine(target, suffix, "current");
        var abandoned = Path.Combine(target, suffix, "abandoned");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(abandoned);
        Directory.SetLastWriteTimeUtc(abandoned, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        var hadOriginal = Directory.Exists(linked);
        if (hadOriginal) Directory.Move(linked, backup);
        try
        {
            Directory.CreateSymbolicLink(linked, target);
            var messages = new List<string>();
            var currentThroughLink = Path.Combine(linked, suffix, "current");

            Assert.Equal(0, HostStorageRetention.SweepInstances(currentThroughLink,
                _clock.GetUtcNow(), 14, messages.Add));
            Assert.True(Directory.Exists(abandoned));
            Assert.Contains(messages, message => message.Contains("reason=linked-instance-root", StringComparison.Ordinal));
        }
        finally
        {
            if (new DirectoryInfo(linked).LinkTarget is not null) Directory.Delete(linked);
            if (hadOriginal) Directory.Move(backup, linked);
        }
    }

    [Theory]
    [InlineData("state")]
    [InlineData("agentstudio")]
    [InlineData("instances")]
    [InlineData("candidate")]
    [InlineData("candidate-directory")]
    [InlineData("child")]
    public void Concurrent_path_replacement_cannot_redirect_deletion(string replacement)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempWorkspace("instance-retention-swap");
        using var state = new InstanceState();
        var current = state.PathFor("current");
        var abandoned = state.PathFor("abandoned");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(Path.Combine(abandoned, "empty", "nested"));
        File.WriteAllText(Path.Combine(abandoned, "batch-state-origin"), "owned");
        foreach (var directory in new[] { Path.Combine(abandoned, "empty", "nested"), Path.Combine(abandoned, "empty"), abandoned })
            Directory.SetLastWriteTimeUtc(directory, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        File.SetLastWriteTimeUtc(Path.Combine(abandoned, "batch-state-origin"),
            _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        var swapped = replacement switch
        {
            "state" => LocalStateRoot.Resolve(),
            "agentstudio" => LocalStateRoot.Combine("agentstudio"),
            "instances" => state.Root,
            "candidate" or "candidate-directory" => abandoned,
            _ => Path.Combine(abandoned, "empty"),
        };
        var foreign = Path.Combine(temp.Path, "foreign");
        var backup = Path.Combine(temp.Path, "original");
        var foreignCandidate = Path.Combine(foreign, Path.GetRelativePath(swapped, abandoned));
        if (replacement == "child") foreignCandidate = foreign;
        var foreignChild = replacement == "child" ? Path.Combine(foreign, "nested") : Path.Combine(foreignCandidate, "empty", "nested");
        Directory.CreateDirectory(foreignChild);
        if (replacement != "child")
            File.WriteAllText(Path.Combine(foreignCandidate, "batch-state-origin"), "foreign");
        var didSwap = false;
        try
        {
            HostStorageRetention.SweepInstances(current, _clock.GetUtcNow(), 14, _ => { }, candidate =>
            {
                if (candidate != abandoned) return;
                Directory.Move(swapped, backup);
                if (replacement == "candidate-directory")
                    Directory.CreateDirectory(swapped);
                else
                    Directory.CreateSymbolicLink(swapped, foreign);
                didSwap = true;
            });

            Assert.True(didSwap);
            var movedCandidate = replacement == "child"
                ? backup
                : Path.Combine(backup, Path.GetRelativePath(swapped, abandoned));
            var movedChild = replacement == "child"
                ? Path.Combine(movedCandidate, "nested")
                : Path.Combine(movedCandidate, "empty", "nested");
            Assert.True(Directory.Exists(movedChild), "Retention modified the moved directory outside local state.");
            if (replacement != "child")
                Assert.Equal("owned", File.ReadAllText(Path.Combine(movedCandidate, "batch-state-origin")));
            if (replacement == "candidate-directory")
                Assert.True(Directory.Exists(swapped), "Retention removed a replacement before it qualified.");
            Assert.True(Directory.Exists(foreignChild), "Deletion followed the substituted path outside local state.");
            if (replacement != "child")
                Assert.Equal("foreign", File.ReadAllText(Path.Combine(foreignCandidate, "batch-state-origin")));
        }
        finally
        {
            if (didSwap)
            {
                if (Directory.Exists(swapped)) Directory.Delete(swapped);
                Directory.CreateDirectory(Path.GetDirectoryName(swapped)!);
                Directory.Move(backup, swapped);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Linux_kernel_denies_mutation_through_handles_moved_outside_the_authorized_ancestors(bool moveAnchor)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempWorkspace("instance-kernel-confinement");
        using var state = new InstanceState();
        var abandoned = state.PathFor("abandoned");
        Directory.CreateDirectory(Path.Combine(abandoned, "empty", "nested"));
        File.WriteAllText(Path.Combine(abandoned, "batch-state-origin"), "owned");
        using var anchor = HostStorageRetention.PinnedRetentionDirectory.OpenRoot(state.Root);
        using var candidate = anchor.OpenChild(Path.GetFileName(abandoned));
        using var worker = new HostStorageRetention.InstanceDeletionWorker(anchor.AncestorDescriptors.ToArray());
        Assert.True(worker.Available, "The retention verification host needs enabled Landlock support.");
        var moved = Path.Combine(temp.Path, "moved");
        Directory.Move(moveAnchor ? state.Root : abandoned, moved);
        var movedCandidate = moveAnchor ? Path.Combine(moved, Path.GetFileName(abandoned)) : moved;
        try
        {
            // Deliberately bypass VerifyLocation: these operations exercise the
            // kernel boundary even if a rename occurs after the last managed check.
            Assert.Throws<UnauthorizedAccessException>(() => worker.Run(() =>
                File.Delete(Path.Combine(candidate.IoPath, "batch-state-origin"))));
            // Unix rmdir access denial is surfaced as IOException by Directory.Delete.
            Assert.Throws<IOException>(() => worker.Run(() =>
                Directory.Delete(Path.Combine(candidate.IoPath, "empty", "nested"))));
            Assert.True(Directory.Exists(Path.Combine(movedCandidate, "empty", "nested")));
            Assert.Equal("owned", File.ReadAllText(Path.Combine(movedCandidate, "batch-state-origin")));

            // The caller retains its normal permissions after confined work.
            var sentinel = Path.Combine(temp.Path, "caller-owned");
            File.WriteAllText(sentinel, "keep caller permissions");
            File.Delete(sentinel);
            Assert.False(File.Exists(sentinel));
        }
        finally
        {
            Directory.Move(moved, moveAnchor ? state.Root : abandoned);
        }
    }

    [Fact]
    public void Evidence_written_after_inspection_prevents_directory_removal()
    {
        using var state = new InstanceState();
        var current = state.PathFor("current");
        var abandoned = state.PathFor("abandoned");
        var child = Path.Combine(abandoned, "pull-gates");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(child);
        Directory.SetLastWriteTimeUtc(child, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        Directory.SetLastWriteTimeUtc(abandoned, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        var evidence = Path.Combine(child, "evidence.json");
        var messages = new List<string>();

        Assert.Equal(0, HostStorageRetention.SweepInstances(current, _clock.GetUtcNow(), 14, messages.Add,
            candidate => { if (candidate == abandoned) File.WriteAllText(evidence, "pending"); }));

        Assert.Equal("pending", File.ReadAllText(evidence));
        Assert.Contains(messages, message => message.Contains("reason=unreadable-or-changed", StringComparison.Ordinal));
    }

    private int SweepStore(string store, string workspace, List<string> messages)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        return HostStorageRetention.Sweep(new BatchGateStore(store), scanner,
            _clock.GetUtcNow(), 14, messages.Add);
    }

    // All paths are in the suite-owned state root. Running this collection
    // alone also keeps retention scans away from concurrent host fixtures.
    private sealed class InstanceState : IDisposable
    {
        private readonly string _prefix = "retention-" + Guid.NewGuid().ToString("N") + "-";
        private readonly List<string> _paths = [];
        public string Root { get; } = LocalStateRoot.Combine("agentstudio", "instances");

        public string PathFor(string name)
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, _prefix + name);
            _paths.Add(path);
            return path;
        }

        public void Dispose()
        {
            foreach (var path in _paths)
            {
                if (new DirectoryInfo(path).LinkTarget is not null) Directory.Delete(path);
                else TestTempRoot.TryDelete(path);
            }
        }
    }
}
