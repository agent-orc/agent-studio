extern alias Runner;

using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;
using RClient = Runner::AgentRunner.TaskServerClient;

namespace AgentStudio.Tests;

public sealed partial class RemoteRunnerEndToEndTests
{
    [Fact]
    public async Task Large_bounded_review_report_replays_with_the_same_key_but_rejects_changed_payload()
    {
        const string reviewer = "bounded-report-reviewer";
        const string instance = "bounded-report-host:4243";
        SeedTask(TaskStates.AutoReview, TaskKey, "Bounded report replay", "Build and verify.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        SeedReviewAttempt(factory.Services, includeResultEnvelope: true);
        await RegisterReviewExecutorAsync(http, reviewer, instance);
        using var client = new RClient(http, reviewer, usesDurableTaskServer: true);
        var claim = await client.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewer, instance, 120), CancellationToken.None);
        Assert.Equal("claimed", claim.Status);

        var bytes = new byte[1024 * 1024];
        Array.Fill(bytes, (byte)'x');
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var report = PassingV1ReviewReport(claim, "bounded-report-replay") with
        {
            Artifacts = [new Contract.ReviewArtifactEvidenceDto(
                "stdout", "text/plain", digest, bytes.Length, Convert.ToBase64String(bytes))],
        };
        var first = await client.ReportReviewAsync(claim.Attempt!.AttemptId, report, CancellationToken.None);
        var task = factory.Services.GetRequiredService<TaskScannerService>().FindJob(TaskKey, _watchPath)!;
        var journal = RemoteReviewSettlementJournal.Read(task.FolderPath, claim.Attempt.AttemptId);
        Assert.Equal(RemoteReviewSettlementReadStatus.Ready, journal.Status);
        Assert.Null(Assert.Single(journal.Entry!.Report.Artifacts).ContentBase64);
        Assert.InRange(new FileInfo(RemoteReviewSettlementJournal.PathFor(task.FolderPath, claim.Attempt.AttemptId)).Length,
            1, RemoteReviewSettlementJournal.MaximumFileBytes);

        var replay = await client.ReportReviewAsync(claim.Attempt.AttemptId, report, CancellationToken.None);
        Assert.Equal(first.ReportId, replay.ReportId);
        Assert.Equal(Contract.ReviewEvidenceProjectionStatus.Duplicate, replay.EvidenceProjection);

        var conflict = await Assert.ThrowsAsync<Runner::AgentRunner.TaskServerException>(() =>
            client.ReportReviewAsync(claim.Attempt.AttemptId,
                report with { Summary = "A different report under the same key." }, CancellationToken.None));
        Assert.Equal((int)HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency-conflict", conflict.ErrorCode);
    }
}
