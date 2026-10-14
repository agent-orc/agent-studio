using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoReviewResumeRetryPolicyTests
{
    [Fact]
    public void Unproductive_resume_starts_at_most_five_times_and_backs_off()
    {
        var clock = new FakeTimeProvider();
        var attempts = 0;
        DateTimeOffset? next = null;
        var starts = 0;

        for (var minute = 0; minute < 60; minute++)
        {
            if (AutoReviewResumeRetryPolicy.CanStart(attempts, next, clock.GetUtcNow()))
            {
                attempts++;
                starts++;
                next = clock.GetUtcNow() + AutoReviewResumeRetryPolicy.Delay(attempts);
            }
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(AutoReviewResumeRetryPolicy.MaxAttempts, starts);
        Assert.False(AutoReviewResumeRetryPolicy.CanStart(attempts, next, clock.GetUtcNow()));
        Assert.Equal(TimeSpan.FromMinutes(1), AutoReviewResumeRetryPolicy.Delay(1));
        Assert.Equal(TimeSpan.FromMinutes(8), AutoReviewResumeRetryPolicy.Delay(5));
    }

    [Fact]
    public void Retry_record_survives_a_new_reader_and_is_fenced_to_its_attempt()
    {
        var clock = new FakeTimeProvider();
        var folder = Path.Combine(Path.GetTempPath(), "resume-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var due = clock.GetUtcNow() + AutoReviewResumeRetryPolicy.Delay(1);
            AutoReviewResumeRetryStore.Write(folder,
                new AutoReviewResumeRetryRecord("review-first", 1, due, "lane write failed"));

            var restored = Assert.IsType<AutoReviewResumeRetryRecord>(
                AutoReviewResumeRetryStore.Read(folder, "review-first"));
            Assert.False(AutoReviewResumeRetryPolicy.CanStart(restored.Attempts, restored.NextAtUtc,
                clock.GetUtcNow()));
            Assert.Equal("lane write failed", restored.Reason);
            Assert.Null(AutoReviewResumeRetryStore.Read(folder, "review-successor"));

            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(AutoReviewResumeRetryPolicy.CanStart(restored.Attempts, restored.NextAtUtc,
                clock.GetUtcNow()));
            AutoReviewResumeRetryStore.Clear(folder, "review-first");
            Assert.Null(AutoReviewResumeRetryStore.Read(folder, "review-first"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_retry_file_does_not_exhaust_a_healthy_delivery()
    {
        var clock = new FakeTimeProvider();
        var folder = Path.Combine(Path.GetTempPath(), "resume-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            AutoReviewResumeRetryStore.Write(folder, new AutoReviewResumeRetryRecord(
                "review-first", 2, clock.GetUtcNow(), "temporary failure"));
            var path = Path.Combine(folder, "logs", "remote-delivery-resume-retry.json");
            File.WriteAllBytes(path, new byte[64]);
            Assert.Null(AutoReviewResumeRetryStore.Read(folder, "review-first"));
            AutoReviewResumeRetryStore.Write(folder, new AutoReviewResumeRetryRecord(
                "review-first", 1, clock.GetUtcNow(), "recovered"));
            Assert.Equal(1, AutoReviewResumeRetryStore.Read(folder, "review-first")!.Attempts);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
