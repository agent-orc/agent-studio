using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ReleaseSuiteVerdictStoreTests
{
    [Fact]
    public async Task Verdict_survives_restart_and_is_scoped_by_sha_and_command_digest()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var first = new TaskServerStore(options, clock);
        await first.InitializeAsync();
        var verdict = new ReleaseSuiteVerdict("Project", new('a', 40), new('b', 64),
            "red", clock.GetUtcNow());
        await first.PutReleaseSuiteVerdictAsync(verdict);
        var second = new TaskServerStore(options, clock);
        await second.InitializeForBackupAsync();
        Assert.Equal("red", (await second.GetReleaseSuiteVerdictAsync(
            verdict.Project, verdict.CommitSha, verdict.CommandDigest))?.Status);
        Assert.Null(await second.GetReleaseSuiteVerdictAsync(
            verdict.Project, verdict.CommitSha, new string('c', 64)));
        Assert.Null(await second.GetReleaseSuiteVerdictAsync(
            verdict.Project, new string('d', 40), verdict.CommandDigest));
        var green = verdict with { Status = "green", FinishedAtUtc = verdict.FinishedAtUtc.AddMinutes(1) };
        Assert.Equal("green", (await first.PutReleaseSuiteVerdictAsync(green)).Status);
        Assert.Equal("green", (await second.GetReleaseSuiteVerdictAsync(
            verdict.Project, verdict.CommitSha, verdict.CommandDigest))?.Status);
        Assert.Equal("green", (await first.PutReleaseSuiteVerdictAsync(verdict)).Status);
    }
}
