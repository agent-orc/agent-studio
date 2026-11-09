using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

// clock-independent: the executor and its lease run on an injected fixed clock.
/// <summary>
/// The shadow package attempt on a real Git origin (AGT-3400 S4): members are
/// stacked on the current integration tip in stack order with their own
/// commits, every finding is attributed to the member whose hunk holds the
/// cited line, an unowned line goes to the later member as cross-card, and a
/// conflict or an oversized package is reported without running any aspect.
/// </summary>
public sealed class PackageReviewExecutorTests : IDisposable
{
    private const string Dossier = "docs/review-gate-packages/index.html";
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "package-review-" + Guid.NewGuid().ToString("N"));
    private string Origin => Path.Combine(_root, "origin.git");
    private string Seed => Path.Combine(_root, "seed");

    public void Dispose() => ResilientDirectory.TryDelete(_root);

    [Fact]
    public async Task Stacks_members_on_the_integration_tip_with_their_own_commits_and_hunk_owners()
    {
        var (a, b) = await SeedTwoMembersAsync();
        var tipSha = await AdvanceMainAsync();
        var subject = Subject(a, b);
        var workspace = new PackageReviewWorkspace(Options(), subject, Lease(), _ => { }, () => Now, CacheRoot);

        var tip = await workspace.MaterializeAsync(default);
        var stack = await workspace.StackAsync(tip, default);
        var material = await workspace.MaterialAsync(stack, default);

        Assert.Equal(tipSha, tip);
        Assert.Null(stack.ConflictMember);
        Assert.Equal(["AGT-1", "AGT-2"], stack.Steps.Select(step => step.TaskKey));
        Assert.All(stack.Steps, step => Assert.Equal(ReviewPackageStackStrategies.Linear, step.Strategy));
        Assert.All(stack.Steps, step => Assert.Single(step.CommitShas));
        Assert.Equal(tip, stack.Steps[0].TipBeforeSha);
        Assert.Equal(stack.Steps[0].TipAfterSha, stack.Steps[1].TipBeforeSha);
        Assert.Equal(stack.CandidateSha, stack.Steps[1].TipAfterSha);
        Assert.False(material.OverBudget);
        Assert.Equal(3, material.Files);
        Assert.Contains(new ReviewPackageOwnedRange("src/limit.cs", 1, 1, "AGT-2"), material.Ownership.Ranges);
        Assert.Contains(new ReviewPackageOwnedRange("src/limit.cs", 12, 12, "AGT-1"), material.Ownership.Ranges);
        Assert.Contains(new ReviewPackageOwnedRange("src/a.cs", 1, 2, "AGT-1"), material.Ownership.Ranges);
        Assert.Contains("### Member 1: `AGT-1` (linear, 1 commit(s))", material.Text);
        Assert.Contains("+cap4", material.Text);
        workspace.Delete();
    }

    [Fact]
    public async Task Reports_per_member_verdicts_with_hunk_owned_and_cross_card_findings()
    {
        var (a, b) = await SeedTwoMembersAsync();
        await AdvanceMainAsync();
        var plane = new FakePlane();
        var prompts = new List<ReviewCommandDto>();
        var executor = new PackageReviewExecutor(Options(), plane, () => "instance-a", _ => { }, () => Now, CacheRoot,
            (_, _, command, _) =>
            {
                prompts.Add(command);
                return Task.FromResult(new PackageAgentRun(Reply(command.Aspect), 0, null, Now, Now.AddSeconds(5),
                    new RemoteAgentUsage("gpt-6-sol", 100, 20, 10, 0, true)));
            });

        var receipt = await executor.RunClaimedAsync(Claim(Subject(a, b)), default);

        Assert.NotNull(receipt);
        var report = Assert.Single(plane.Reports);
        Assert.Equal(ReviewPackageOutcomes.Completed, report.Outcome);
        Assert.Equal(4, report.Commands.Count);
        Assert.All(report.Commands, command => Assert.Equal(100, command.InputTokens));
        Assert.Equal(8, report.Verdicts.Count);
        Assert.Equal("block", Assert.Single(report.Verdicts,
            verdict => verdict.TaskKey == "AGT-1" && verdict.Aspect == "code-quality").Status);
        var owned = Assert.Single(report.Findings, finding => finding.Aspect == "code-quality" && finding.Line == 12);
        Assert.Equal(("AGT-1", false), (owned.OwnerTaskKey, owned.CrossCard));
        var shared = Assert.Single(report.Findings, finding => finding.Aspect == "cross-card-consistency");
        Assert.Equal(("AGT-2", true), (shared.OwnerTaskKey, shared.CrossCard));
        Assert.Equal(["AGT-1"], shared.RelatedTaskKeys);
        var crossCard = Assert.Single(prompts, command => command.Aspect == ReviewPackageAspects.CrossCardConsistency);
        Assert.Contains("## Dossier excerpt", crossCard.Prompt);
        Assert.Contains("Decision PK5: at most 4 members.", crossCard.Prompt);
        Assert.DoesNotContain("<script", crossCard.Prompt);
        Assert.All(prompts.Where(command => command.Aspect != ReviewPackageAspects.CrossCardConsistency),
            command => Assert.DoesNotContain("## Dossier excerpt", command.Prompt));
        Assert.All(prompts, command => Assert.Contains("## Package review material", command.Prompt));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "review-work"), PackageReviewWorkspace.AttemptPrefix + "*"));
    }

    [Fact]
    public async Task Cli_input_refusal_reports_package_review_infrastructure()
    {
        var (a, b) = await SeedTwoMembersAsync();
        var plane = new FakePlane();
        var executor = new PackageReviewExecutor(Options(), plane, () => "instance-a", _ => { }, () => Now,
            CacheRoot, (_, _, _, _) => Task.FromResult(new PackageAgentRun(
                "turn/start failed: input_too_large", 1, null, Now, Now, null)));

        await executor.RunClaimedAsync(Claim(Subject(a, b)), default);

        var report = Assert.Single(plane.Reports);
        Assert.Equal(ReviewPackageOutcomes.Infrastructure, report.Outcome);
        Assert.Contains(ReviewCliRefusalPolicy.InputTooLarge, report.Summary);
        Assert.Equal(ReviewCliRefusalPolicy.InputTooLarge, Assert.Single(report.Commands).Classification);
    }

    [Fact]
    public async Task A_member_that_does_not_stack_is_reported_as_a_stack_conflict_without_any_aspect()
    {
        await SeedMainAsync();
        var a = await MemberAsync("results/AGT-1", ("src/limit.cs", Lines(10).Replace("line5", "five-a")));
        var b = await MemberAsync("results/AGT-2", ("src/limit.cs", Lines(10).Replace("line5", "five-b")));
        var plane = new FakePlane();
        var calls = 0;
        var executor = new PackageReviewExecutor(Options(), plane, () => "instance-a", _ => { }, () => Now, CacheRoot,
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(new PackageAgentRun("", 0, null, Now, Now, null));
            });

        await executor.RunClaimedAsync(Claim(Subject(a, b)), default);

        var report = Assert.Single(plane.Reports);
        Assert.Equal(ReviewPackageOutcomes.StackConflict, report.Outcome);
        Assert.Contains("AGT-2", report.Summary);
        Assert.Single(report.Steps);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_member_range_with_a_merge_commit_is_merged_and_attributed_to_that_merge()
    {
        await SeedMainAsync();
        await GitAsync(Seed, "checkout", "-b", "member-merge", "main");
        await CommitAsync(("src/m.cs", "m1\n"));
        await AdvanceMainAsync();
        await GitAsync(Seed, "checkout", "member-merge");
        await GitAsync(Seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "merge", "--no-ff", "--no-edit", "main");
        await GitAsync(Seed, "push", "origin", "member-merge:refs/heads/results/AGT-1");
        var sha = (await GitAsync(Seed, "rev-parse", "HEAD")).StdOut.Trim();
        var subject = Subject(sha) with { Members = [Member("AGT-1", 0, sha)] };
        var workspace = new PackageReviewWorkspace(Options(), subject, Lease(), _ => { }, () => Now, CacheRoot);

        var stack = await workspace.StackAsync(await workspace.MaterializeAsync(default), default);

        var step = Assert.Single(stack.Steps);
        Assert.Equal(ReviewPackageStackStrategies.Merge, step.Strategy);
        Assert.Equal(step.TipAfterSha, step.CommitShas[^1]);
        var material = await workspace.MaterialAsync(stack, default);
        Assert.Equal([new ReviewPackageOwnedRange("src/m.cs", 1, 1, "AGT-1")], material.Ownership.Ranges);
        workspace.Delete();
    }

    [Fact]
    public async Task A_package_over_the_material_budget_splits_instead_of_truncating()
    {
        var (a, b) = await SeedTwoMembersAsync();
        var plane = new FakePlane();
        var subject = Subject(a, b);
        subject = subject with { Plan = subject.Plan with { MaximumLines = 5 } };
        var executor = new PackageReviewExecutor(Options(), plane, () => "instance-a", _ => { }, () => Now, CacheRoot,
            (_, _, _, _) => throw new InvalidOperationException("No aspect may run over budget."));

        await executor.RunClaimedAsync(Claim(subject), default);

        var report = Assert.Single(plane.Reports);
        Assert.Equal(ReviewPackageOutcomes.OverBudget, report.Outcome);
        Assert.Contains("never truncated", report.Summary);
        Assert.Empty(report.Commands);
    }

    [Fact]
    public async Task The_package_slot_polls_once_a_minute_and_backs_off_from_a_server_without_the_plane()
    {
        var plane = new FakePlane { ClaimStatusCode = 404 };
        var slot = new PackageReviewSlot(Options(), plane, () => "instance-a", _ => { });

        await slot.PollAsync(Now, default);
        await slot.PollAsync(Now.AddMinutes(5), default);
        Assert.Equal(1, plane.Claims);
        await slot.PollAsync(Now.AddMinutes(31), default);
        Assert.Equal(2, plane.Claims);

        plane.ClaimStatusCode = null;
        var disabled = new PackageReviewSlot(Options(packageReview: false), plane, () => "instance-a", _ => { });
        await disabled.PollAsync(Now, default);
        Assert.Equal(2, plane.Claims);
    }

    private string CacheRoot => Path.Combine(_root, "source-cache");

    private static string Reply(string aspect) => aspect switch
    {
        "code-quality" => """
            Checked src/limit.cs.
            ```package-review
            { "members": [ { "taskKey": "AGT-1", "status": "block", "summary": "Cap is hard-coded." },
                           { "taskKey": "AGT-2", "status": "pass", "summary": "Clean." } ],
              "findings": [ { "file": "src/limit.cs", "line": 12, "severity": "block", "summary": "Cap 4 hard-coded." } ] }
            ```
            [[TASK_DONE]]
            """,
        "cross-card-consistency" => """
            ```package-review
            { "members": [ { "taskKey": "AGT-1", "status": "pass", "summary": "ok" },
                           { "taskKey": "AGT-2", "status": "concerns", "summary": "drift" } ],
              "findings": [ { "file": "src/limit.cs", "line": 5, "severity": "concern", "summary": "Untouched line both rely on." } ] }
            ```
            """,
        _ => """
            ```package-review
            { "members": [ { "taskKey": "AGT-1", "status": "pass", "summary": "ok" },
                           { "taskKey": "AGT-2", "status": "pass", "summary": "ok" } ], "findings": [] }
            ```
            """,
    };

    private async Task<(string A, string B)> SeedTwoMembersAsync()
    {
        await SeedMainAsync();
        var a = await MemberAsync("results/AGT-1", ("src/limit.cs", Lines(10) + "cap4\n"), ("src/a.cs", "a1\na2\n"));
        var b = await MemberAsync("results/AGT-2", ("src/limit.cs", "header\n" + Lines(10)), ("src/b.cs", "b1\n"));
        return (a, b);
    }

    private async Task SeedMainAsync()
    {
        Directory.CreateDirectory(_root);
        await GitAsync(_root, "init", "--bare", Origin);
        await GitAsync(_root, "init", "-b", "main", Seed);
        await GitAsync(Seed, "remote", "add", "origin", Origin);
        await CommitAsync(("src/limit.cs", Lines(10)),
            (Dossier, "<html><script>alert(1)</script><p>Decision PK5: at most 4 members.</p></html>"));
        await GitAsync(Seed, "push", "origin", "main");
    }

    private async Task<string> AdvanceMainAsync()
    {
        await GitAsync(Seed, "checkout", "main");
        await CommitAsync(("README.md", "tip\n"));
        await GitAsync(Seed, "push", "origin", "main");
        return (await GitAsync(Seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private async Task<string> MemberAsync(string branch, params (string Path, string Content)[] files)
    {
        await GitAsync(Seed, "checkout", "-B", branch.Replace('/', '-'), "main");
        await CommitAsync(files);
        await GitAsync(Seed, "push", "origin", $"HEAD:refs/heads/{branch}");
        var sha = (await GitAsync(Seed, "rev-parse", "HEAD")).StdOut.Trim();
        await GitAsync(Seed, "checkout", "main");
        return sha;
    }

    private async Task CommitAsync(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(Seed, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }
        await GitAsync(Seed, "add", "--all");
        await GitAsync(Seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture");
    }

    private static string Lines(int count)
        => string.Concat(Enumerable.Range(1, count).Select(index => $"line{index}\n"));

    private RunnerOptions Options(bool packageReview = true) => new()
    {
        PackageReview = packageReview,
        ServerUrl = "http://localhost",
        RunnerId = "runner-a",
        RunnerName = "runner-a",
        Hostname = "review-host",
        BackendName = "remote-review",
        Role = "review",
        WorkDir = Path.Combine(_root, "coding"),
        ReviewWorkDir = Path.Combine(_root, "review-work"),
        BaseBranch = "main",
        ClaudeCliBin = "unused",
        CodexCliBin = "unused",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
    };

    private ReviewPackageSubjectDto Subject(params string[] shas)
    {
        var commands = ReviewPackageAspects.Agent.Select(aspect => new ReviewCommandDto(
            "package-aspect-" + aspect, aspect, "codex", [], ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Review " + aspect, CliType: "codex", Model: "gpt-6-sol", ThinkingLevel: "medium")).ToArray();
        return new ReviewPackageSubjectDto("rpkg_1", ReviewSubjectKinds.Package, "Demo", Dossier, "digest",
            ReviewPackageModes.Shadow, shas.Select((sha, index) => Member($"AGT-{index + 1}", index, sha)).ToArray(),
            new ReviewPackagePlanDto("repo", Origin, "main", commands), Now);
    }

    private static ReviewPackageMemberDto Member(string key, int order, string sha)
        => new(key, "run-" + key, sha, order, ResultRef: $"refs/heads/results/{key}");

    private static ReviewPackageLeaseDto Lease() => new("rpkg_1-a1", "rpkg_1", 1, "lease-1", 1, Now.AddMinutes(10));

    private static ReviewPackageClaimResponse Claim(ReviewPackageSubjectDto subject)
        => new("claimed", subject, Lease());

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    private sealed class FakePlane : IReviewPackagePlaneClient
    {
        public List<ReviewPackageReportRequest> Reports { get; } = [];
        public int Claims { get; private set; }
        public int? ClaimStatusCode { get; set; }

        public Task<ReviewPackageClaimResponse> ClaimReviewPackageAsync(ReviewPackageClaimRequest request, CancellationToken ct)
        {
            Claims++;
            if (ClaimStatusCode is { } status) throw new TaskServerException(status, "No package plane.");
            return Task.FromResult(new ReviewPackageClaimResponse("empty", null, null));
        }

        public Task<ReviewPackageLeaseDto> RenewReviewPackageAsync(string attemptId, ReviewPackageRenewRequest request,
            CancellationToken ct)
            => Task.FromResult(Lease());

        public Task<ReviewPackageReportDto> ReportReviewPackageAsync(string attemptId, ReviewPackageReportRequest request,
            CancellationToken ct)
        {
            Reports.Add(request);
            return Task.FromResult(new ReviewPackageReportDto("rprp_1", attemptId, "rpkg_1", request.Outcome, "hash", Now, false));
        }
    }
}
