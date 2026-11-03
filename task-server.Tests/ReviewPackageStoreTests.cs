using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Review subject kind <c>package</c> in the standalone review store (AGT-3400
/// S4): its own tables beside the unchanged per-card <c>review_subjects</c>,
/// one package per member delivery, the fenced attempt lease, and a report
/// that changes no task.
/// </summary>
public sealed class ReviewPackageStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_package_subject_lives_beside_the_unchanged_per_card_subject_table()
    {
        using var temp = new TempDirectory();
        var store = await StoreAsync(temp, new ManualTimeProvider(Start));

        var subject = await store.CreateReviewPackageSubjectAsync(Request(), "orchestrator", default);
        var replay = await store.CreateReviewPackageSubjectAsync(Request(), "orchestrator", default);

        Assert.Equal(ReviewSubjectKinds.Package, subject.Kind);
        Assert.Equal(subject.PackageId, replay.PackageId);
        Assert.Equal(ReviewPackageMembershipDigest.Compute("docs/x/index.html", subject.Members), subject.MembershipDigest);
        Assert.Equal(["AGT-1", "AGT-2"], subject.Members.Select(member => member.TaskKey));
        Assert.Equal(0L, await CountAsync(temp, "review_subjects"));
        Assert.Equal(2L, await CountAsync(temp, "review_package_members"));
        Assert.Equal(
            "id,task_id,source_run_id,repository_id,repository_url,expected_result_sha,result_ref,"
            + "source_bundle_artifact_id,source_bundle_sha256,coding_host_id,review_policy_hash,plan_json,"
            + "idempotency_key,created_at",
            await ColumnsAsync(temp, "review_subjects"));
        Assert.Equal(subject.PackageId,
            (await store.GetReviewPackageMembershipAsync("AGT-2", "run-AGT-2", default))!.PackageId);
        Assert.Null(await store.GetReviewPackageMembershipAsync("AGT-2", "run-other", default));
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CreateReviewPackageSubjectAsync(
            Request(members: [Member("AGT-2", 0, 'b')], key: "other"), "orchestrator", default));
        Assert.Equal("member-already-packaged", conflict.Code);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateReviewPackageSubjectAsync(
            Request(key: "dossier") with { Mode = ReviewPackageModes.Dossier }, "orchestrator", default));
    }

    [Fact]
    public async Task A_package_attempt_is_fenced_requeued_once_and_its_report_changes_no_task()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp, clock);
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Demo", "AGT"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Card", "Do it", "2-ready"), "test", default);
        await store.CreateReviewPackageSubjectAsync(Request(), "orchestrator", default);

        var first = await store.ClaimReviewPackageAsync(new ReviewPackageClaimRequest("runner-a", "instance-a", 120), "runner-a", default);
        Assert.Equal("claimed", first.Status);
        Assert.Equal("empty", (await store.ClaimReviewPackageAsync(
            new ReviewPackageClaimRequest("runner-b", "instance-b", 120), "runner-b", default)).Status);
        await Assert.ThrowsAsync<ReviewPackageConflictException>(() => store.RenewReviewPackageAsync(first.Lease!.AttemptId,
            new ReviewPackageRenewRequest("runner-b", "instance-b", first.Lease.LeaseId, first.Lease.Fence), default));

        clock.Advance(TimeSpan.FromMinutes(3));
        var second = await store.ClaimReviewPackageAsync(new ReviewPackageClaimRequest("runner-b", "instance-b", 600), "runner-b", default);
        Assert.Equal(2, second.Lease!.AttemptNumber);
        var stale = Report(first.Lease!, "runner-a", "instance-a");
        await Assert.ThrowsAsync<ReviewPackageConflictException>(() =>
            store.ReportReviewPackageAsync(first.Lease.AttemptId, stale, "runner-a", default));

        var report = Report(second.Lease, "runner-b", "instance-b");
        var receipt = await store.ReportReviewPackageAsync(second.Lease.AttemptId, report, "runner-b", default);
        var duplicate = await store.ReportReviewPackageAsync(second.Lease.AttemptId, report, "runner-b", default);

        Assert.False(receipt.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(receipt.ReportId, duplicate.ReportId);
        var record = Assert.Single(await store.ListReviewPackagesAsync("demo", default));
        Assert.Equal(ReviewPackageAttemptStatuses.Reported, record.Status);
        Assert.Equal("block", Assert.Single(record.Report!.Verdicts,
            verdict => verdict.TaskKey == "AGT-1" && verdict.Aspect == ReviewPackageAspects.Grade).Status);
        var after = await store.GetTaskAsync(project.ProjectId, task.TaskId, default);
        Assert.Equal((task.State, task.Version), (after!.State, after.Version));
    }

    private static async Task<TaskServerStore> StoreAsync(TempDirectory temp, TimeProvider clock)
    {
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        return store;
    }

    private static CreateReviewPackageSubjectRequest Request(
        IReadOnlyList<ReviewPackageMemberDto>? members = null, string key = "review-package:key")
    {
        var commands = ReviewPackageAspects.Agent.Select(aspect => new ReviewCommandDto(
            "package-aspect-" + aspect, aspect, "codex", [], ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Review " + aspect, CliType: "codex", Model: "gpt-6-sol")).ToArray();
        return new CreateReviewPackageSubjectRequest("Demo", "docs/x/index.html", ReviewPackageModes.Shadow,
            members ?? [Member("AGT-1", 0, 'a'), Member("AGT-2", 1, 'b')],
            new ReviewPackagePlanDto("repo", "https://example.invalid/demo.git", "main", commands), key);
    }

    private static ReviewPackageMemberDto Member(string key, int order, char sha)
        => new(key, "run-" + key, new string(sha, 40), order);

    private static ReviewPackageReportRequest Report(ReviewPackageLeaseDto lease, string executor, string instance)
        => new(executor, instance, lease.LeaseId, lease.Fence, $"report:{lease.AttemptId}", ReviewPackageOutcomes.Completed,
            "done", new string('1', 40), new string('2', 40), [],
            [new ReviewPackageMemberVerdictDto("AGT-1", "code-quality", "block", "Cap differs.")],
            [new ReviewPackageFindingDto("code-quality", "src/a.cs", 3, "block", "Cap is 5.", "AGT-1", false, [])],
            [], 2, 10);

    private static async Task<long> CountAsync(TempDirectory temp, string table)
    {
        await using var connection = Open(temp);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ColumnsAsync(TempDirectory temp, string table)
    {
        await using var connection = Open(temp);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT group_concat(name, ',') FROM pragma_table_info('{table}');";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static SqliteConnection Open(TempDirectory temp)
    {
        var path = Directory.EnumerateFiles(temp.Path, "*.db", SearchOption.AllDirectories).First();
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }
}
