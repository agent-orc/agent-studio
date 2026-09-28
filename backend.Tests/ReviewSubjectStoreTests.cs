using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewSubjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-subject-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateCurrentAttempt_EmbeddedFlatStorage_UsesFolderKeyWhenTaskJsonIsUnavailable()
    {
        var folder = Path.Combine(_root, ".orchestrator", "jobs", "tasks", "000", "TE-38");
        Directory.CreateDirectory(folder);
        var (authority, subject) = CompletedSubject("TE-38");

        var valid = ReviewSubjectStore.TryValidateCurrentAttempt(
            folder,
            subject,
            authority,
            out var error);

        Assert.True(valid, error);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCurrentAttempt_LegacyStorage_ReadsKeyFieldCaseInsensitively()
    {
        var folder = Path.Combine(_root, "5-human-review", "legacy-task");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), """{"Key":"TE-38"}""");
        var (authority, subject) = CompletedSubject("TE-38");

        var valid = ReviewSubjectStore.TryValidateCurrentAttempt(
            folder,
            subject,
            authority,
            out var error);

        Assert.True(valid, error);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCurrentAttempt_ChangedImmutableResultRef_IsRejected()
    {
        var folder = Path.Combine(_root, "5-human-review", "changed-ref");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), """{"key":"TE-38"}""");
        var (authority, subject) = CompletedSubject(
            "TE-38", "refs/agent-studio/results/original");

        var valid = ReviewSubjectStore.TryValidateCurrentAttempt(
            folder,
            subject with { ImmutableResultRef = "refs/agent-studio/results/changed" },
            authority,
            out var error);

        Assert.False(valid);
        Assert.Contains("result ref", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCurrentAttempt_MatchingImmutableResultRef_IsAccepted()
    {
        var folder = Path.Combine(_root, "5-human-review", "matching-ref");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), """{"key":"TE-39"}""");
        var (authority, subject) = CompletedSubject(
            "TE-39", "refs/heads/runner/fixture/TE-39");

        Assert.True(ReviewSubjectStore.TryValidateCurrentAttempt(
            folder, subject, authority, out var error), error);
    }

    private (AttemptAuthorityService Authority, ReviewSubjectRecord Subject) CompletedSubject(
        string taskKey, string? immutableRef = null)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            })
            .Build();
        var authority = new AttemptAuthorityService(
            configuration,
            NullLogger<AttemptAuthorityService>.Instance);
        var run = authority.AcquireRun(
            taskKey,
            "PROJ-TE",
            null,
            "agent-runner-01",
            "host-a",
            60,
            "claim").RunAttempt!;
        var sha = new string('a', 40);
        var envelope = immutableRef is null ? null : new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
            run.RepositoryId, run.AttemptId, new string('0', 40), sha,
            immutableRef, null, new string('1', 64));
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "settle"),
            Outcome = "done",
            ResultSha = sha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = envelope is null
                ? null
                : AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
        });
        Assert.True(settled.Accepted);

        return (authority, new ReviewSubjectRecord
        {
            TaskKey = taskKey,
            RunAttemptId = run.AttemptId,
            ResultSha = sha,
            ImmutableResultRef = immutableRef,
            AttemptChainId = run.Lease!.LeaseId,
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
