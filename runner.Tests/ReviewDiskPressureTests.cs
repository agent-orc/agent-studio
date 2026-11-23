using AgentStudio.TaskServer.Contracts;
using Xunit;
using Microsoft.Extensions.Time.Testing;

namespace AgentRunner.Tests;

public sealed class ReviewDiskPressureTests
{
    private readonly FakeTimeProvider _clock = new();
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(21, 100, HostDiskPressureLevel.Healthy)]
    [InlineData(19, 100, HostDiskPressureLevel.Warning)]
    [InlineData(29, 200, HostDiskPressureLevel.Warning)]
    [InlineData(7, 100, HostDiskPressureLevel.Critical)]
    [InlineData(9, 200, HostDiskPressureLevel.Critical)]
    public void Defaults_use_larger_of_bytes_and_percentage(long free, long total, HostDiskPressureLevel level)
        => Assert.Equal(level, HostDiskPressurePolicy.Decide(
            [new("review work", "/work", free * GiB, total * GiB)], new()).Level);

    [Fact]
    public void Review_admission_names_disk_and_recovers_above_warning()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost", RunnerId = "review", RunnerName = "review",
            Hostname = "review-host", BackendName = "test", WorkDir = "/work", BaseBranch = "develop",
        };
        var full = new RunnerDiskAdmission(options, probe: new FixedHostFilesystemProbe(6 * GiB, 246 * GiB));
        var disk = full.Observe();
        var decision = ReviewSlotAdmissionPolicy.Decide(null, 0, 1, 1, diskPressure: disk);
        Assert.False(decision.Admitted);
        Assert.True(decision.ActiveSlotsContinue);
        Assert.Contains("disk", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("6.0 GiB", decision.Reason);
        Assert.Contains(disk.Filesystem!.Path, decision.Reason);
    }

    [Theory]
    [InlineData("error MSB3491: Could not write: No space left on device")]
    [InlineData("fatal: write error: Out of diskspace")]
    public void Command_disk_exhaustion_is_infrastructure(string output)
        => Assert.Equal(ReviewInfraAttribution.DiskFull,
            ReviewInfraAttributionPolicy.Attribute(false, output, ""));

    [Fact]
    public void Quoted_disk_error_in_a_test_name_does_not_hide_product_failure()
        => Assert.Equal(ReviewInfraAttribution.ProductOwned, ReviewInfraAttributionPolicy.Attribute(false,
            "  Failed Example.Test(message: No space left on device) [1 ms]\n    Error Message: assertion\nFailed: 1, Passed: 0", ""));

    [Fact]
    public void Failed_temp_delete_keeps_owner_for_retry_even_without_work_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-temp-retry-" + Guid.NewGuid().ToString("N"));
        var work = Path.Combine(root, "work");
        var attempt = Path.Combine(work, "review-old");
        var temp = ReviewAttemptTemp.Root(root, attempt);
        try
        {
            ReviewAttemptTemp.Ensure(temp, attempt, mustBeNew: true);
            File.WriteAllText(Path.Combine(temp, "locked"), "busy");
            Assert.False(ReviewAttemptTemp.Remove(temp, _ => throw new IOException("busy")));
            Assert.True(File.Exists(Path.Combine(temp, ReviewAttemptTemp.OwnerFileName)));
            var result = ReviewWorkspaceRetention.Sweep(work, [], _clock.GetUtcNow().UtcDateTime, _ => { }, root);
            Assert.Equal(1, result.TempRemoved);
            Assert.False(Directory.Exists(temp));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Budget_removes_oldest_inactive_attempts_across_live_and_stale_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-budget-" + Guid.NewGuid().ToString("N"));
        var now = _clock.GetUtcNow().UtcDateTime;
        try
        {
            string Attempt(string parent, string name, int age)
            {
                var path = Path.Combine(parent, name);
                Directory.CreateDirectory(path);
                File.WriteAllBytes(Path.Combine(path, "build.bin"), new byte[100]);
                Directory.SetLastWriteTimeUtc(path, now.AddHours(-age));
                return path;
            }
            var staleCache = Attempt(root + "-stale", ".dependency-cache", 4);
            var oldest = Attempt(root + "-stale", "review-old", 3);
            var newest = Attempt(root, "review-new", 1);
            var active = Attempt(root, "review-active", 4);
            var cache = Attempt(root, ".baseline-cache", 100);
            ReviewWorkspaceRetention.Sweep(root + Path.DirectorySeparatorChar, ["review-active"], now, _ => { },
                tempBase: Path.Combine(root, "temp"), maximumRetainedBytes: 100);
            Assert.False(Directory.Exists(staleCache));
            Assert.False(Directory.Exists(oldest));
            Assert.True(Directory.Exists(newest));
            Assert.True(Directory.Exists(active));
            Assert.True(Directory.Exists(cache));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(root + "-stale")) Directory.Delete(root + "-stale", true);
        }
    }
}
