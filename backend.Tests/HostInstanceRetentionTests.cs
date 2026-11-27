using AgentStudio.Pipeline;
using AgentStudio.TestSupport;
using AgentStudio.Tasks;
using AgentStudio.Review;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HostInstanceRetentionTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
    [Fact]
    public void Sweep_keeps_current_manifest_pending_evidence_and_reservation_but_removes_old_empty_state()
    {
        using var temp = new TempWorkspace("instance-retention");
        var now = _clock.GetUtcNow();
        var instances = Path.Combine(temp.Path, "instances");
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
            var root = Path.Combine(instances, name);
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
        var current = Path.Combine(temp.Path, "instances", "current");
        var abandoned = Path.Combine(temp.Path, "instances", "abandoned");
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
        var current = Path.Combine(temp.Path, "instances", "current");
        Directory.CreateDirectory(current);
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "evidence"), "retain");
        Directory.CreateSymbolicLink(Path.Combine(temp.Path, "instances", "linked"), outside);
        var old = Path.Combine(temp.Path, "instances", "old");
        Directory.CreateDirectory(old);
        Directory.CreateSymbolicLink(Path.Combine(old, "linked"), outside);
        Directory.SetLastWriteTimeUtc(old, _clock.GetUtcNow().UtcDateTime.AddDays(-30));
        var messages = new List<string>();

        Assert.Equal(0, HostStorageRetention.SweepInstances(current, _clock.GetUtcNow(), 14, messages.Add));

        Assert.True(File.Exists(Path.Combine(outside, "evidence")));
        Assert.True(Directory.Exists(old));
        Assert.Contains(messages, message => message.Contains("reason=linked-state", StringComparison.Ordinal));
    }
}
