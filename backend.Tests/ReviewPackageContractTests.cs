using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: lease and report rules receive explicit timestamps as pure inputs.
/// <summary>
/// The shared package contract of AGT-3400 S4: membership digest, the
/// structured per-member output, hunk ownership of a stacked candidate,
/// finding attribution, report admission and the attempt lease.
/// </summary>
public sealed class ReviewPackageContractTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Members = ["AGT-1", "AGT-2"];

    [Fact]
    public void Membership_digest_binds_dossier_order_run_and_result()
    {
        var members = Subject().Members;
        var digest = ReviewPackageMembershipDigest.Compute("docs/x/index.html", members);

        Assert.Equal(digest, ReviewPackageMembershipDigest.Compute(" docs/x/index.html ", members.Reverse()));
        Assert.NotEqual(digest, ReviewPackageMembershipDigest.Compute("docs/x/index.html",
            [members[0], members[1] with { ResultSha = new string('c', 40) }]));
        Assert.NotEqual(digest, ReviewPackageMembershipDigest.Compute("docs/x/index.html",
            [members[0], members[1] with { RunAttemptId = "run-other" }]));
    }

    [Fact]
    public void Parser_reads_one_verdict_per_member_and_only_cited_findings()
    {
        const string output = """
            Reasoning about both members.
            ```package-review
            {
              "members": [
                { "taskKey": "agt-1", "status": "block", "summary": "Limit differs.",
                  "acceptanceCriteria": [ { "criterion": "Cap 4", "status": "unmet" } ] }
              ],
              "findings": [
                { "file": "src/a.cs", "line": 12, "severity": "block", "summary": "Cap is 5.", "member": "AGT-1" },
                { "file": "b/src/b.cs:40", "severity": "concern", "summary": "Path form." },
                { "file": "src/c.cs", "severity": "note", "summary": "No line." }
              ]
            }
            ```
            [[TASK_DONE]]
            """;

        var parsed = ReviewPackageOutputParser.Parse(output, ReviewPackageAspects.CodeQuality, Members);

        var first = Assert.Single(parsed.Verdicts, verdict => verdict.TaskKey == "AGT-1");
        Assert.Equal("block", first.Status);
        Assert.Equal("unmet", Assert.Single(first.AcceptanceCriteria!).Status);
        var missing = Assert.Single(parsed.Verdicts, verdict => verdict.TaskKey == "AGT-2");
        Assert.Equal("missing", missing.Status);
        Assert.Equal(ReviewPackageOutputParser.MissingMemberVerdict, missing.Classification);
        Assert.Equal(2, parsed.Findings.Count);
        Assert.Equal(("src/b.cs", 40), (parsed.Findings[1].File, parsed.Findings[1].Line));
        Assert.Contains(ReviewPackageOutputParser.FindingWithoutCitation, parsed.Malformed);
    }

    [Fact]
    public void Parser_without_a_block_gives_every_member_a_missing_verdict()
    {
        var parsed = ReviewPackageOutputParser.Parse("[[ASPECT_VERDICT: status=pass]]", "code-quality", Members);

        Assert.All(parsed.Verdicts, verdict => Assert.Equal("missing", verdict.Status));
        Assert.Equal([ReviewPackageOutputParser.NoBlock], parsed.Malformed);
        Assert.Empty(parsed.Findings);
    }

    [Fact]
    public void Ownership_follows_later_hunks_that_shift_replace_or_delete_earlier_lines()
    {
        const string first = """
            diff --git a/src/a.cs b/src/a.cs
            index 1111111..2222222 100644
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -9,0 +10,3 @@ class A
            +one
            +two
            +three
            diff --git a/src/old.cs b/src/old.cs
            new file mode 100644
            --- /dev/null
            +++ b/src/old.cs
            @@ -0,0 +1,2 @@
            +x
            +y
            """;
        const string second = """
            diff --git a/src/a.cs b/src/a.cs
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -5,0 +6,2 @@ class A
            +inserted
            +inserted
            @@ -11 +13 @@ class A
            -two
            +TWO
            diff --git a/src/old.cs b/src/new.cs
            similarity index 90%
            rename from src/old.cs
            rename to src/new.cs
            """;

        var ownership = ReviewPackageOwnershipIndex.Build([("AGT-1", first), ("AGT-2", second)]);

        Assert.Equal(
            [
                new ReviewPackageOwnedRange("src/a.cs", 6, 7, "AGT-2"),
                new ReviewPackageOwnedRange("src/a.cs", 12, 12, "AGT-1"),
                new ReviewPackageOwnedRange("src/a.cs", 13, 13, "AGT-2"),
                new ReviewPackageOwnedRange("src/a.cs", 14, 14, "AGT-1"),
                new ReviewPackageOwnedRange("src/new.cs", 1, 2, "AGT-1"),
            ],
            ownership.Ranges);
        Assert.Equal(["AGT-1", "AGT-2"], ownership.FileMembers["src/a.cs"]);
        Assert.Equal(["AGT-1", "AGT-2"], ownership.FileMembers["src/new.cs"]);
        Assert.False(ownership.FileMembers.ContainsKey("src/old.cs"));
    }

    [Fact]
    public void A_finding_on_an_owned_line_names_the_hunk_owner()
    {
        var finding = ReviewPackageFindingAttribution.Attribute("code-quality",
            Raw("./src/a.cs", 12, member: "AGT-2"), Ownership(), Members);

        Assert.Equal("AGT-1", finding.OwnerTaskKey);
        Assert.False(finding.CrossCard);
        Assert.Equal("src/a.cs", finding.File);
        Assert.Equal("AGT-2", finding.ClaimedTaskKey);
    }

    [Fact]
    public void A_finding_without_an_owner_is_cross_card_and_goes_to_the_later_member()
    {
        var shared = ReviewPackageFindingAttribution.Attribute("cross-card-consistency",
            Raw("src/a.cs", 2), Ownership(), Members);
        Assert.True(shared.CrossCard);
        Assert.Equal("AGT-2", shared.OwnerTaskKey);
        Assert.Equal(["AGT-1"], shared.RelatedTaskKeys);

        var untouched = ReviewPackageFindingAttribution.Attribute("cross-card-consistency",
            Raw("docs/start/README.md", 7), Ownership(), Members);
        Assert.True(untouched.CrossCard);
        Assert.Equal("AGT-2", untouched.OwnerTaskKey);

        var named = ReviewPackageFindingAttribution.Attribute("cross-card-consistency",
            Raw("docs/start/README.md", 7, member: "AGT-1"), Ownership(), Members);
        Assert.Equal("AGT-1", named.OwnerTaskKey);
        Assert.Empty(named.RelatedTaskKeys);
    }

    [Fact]
    public void Report_admission_keeps_one_verdict_per_member_downgrades_uncited_blocks_and_grades()
    {
        var subject = Subject();
        var report = Report(ReviewPackageOutcomes.Completed,
            [
                new ReviewPackageMemberVerdictDto("AGT-1", "code-quality", "block", "Cited."),
                new ReviewPackageMemberVerdictDto("AGT-2", "code-quality", "block", "Uncited."),
            ],
            [new ReviewPackageFindingDto("code-quality", "src/a.cs", 12, "block", "Bug.", "AGT-1", false, [])]);

        var normalized = ReviewPackageReportPolicy.Normalize(subject, report);

        Assert.Equal("block", Verdict(normalized, "AGT-1", "code-quality").Status);
        var uncited = Verdict(normalized, "AGT-2", "code-quality");
        Assert.Equal("concerns", uncited.Status);
        Assert.Equal(ReviewVerdictCitationPolicy.BlockWithoutCitation, uncited.Classification);
        Assert.Equal("missing", Verdict(normalized, "AGT-1", "cross-card-consistency").Status);
        Assert.Equal("block", Verdict(normalized, "AGT-1", ReviewPackageAspects.Grade).Status);
        Assert.Equal("concerns", Verdict(normalized, "AGT-2", ReviewPackageAspects.Grade).Status);
        Assert.Throws<ArgumentException>(() => ReviewPackageReportPolicy.Normalize(subject, report with
        {
            Findings = [new ReviewPackageFindingDto("code-quality", "src/a.cs", 1, "note", "x", "AGT-9", true, [])],
        }));
    }

    [Fact]
    public void Validation_accepts_only_shadow_packages_of_one_to_four_members()
    {
        var subject = Subject();
        var request = new CreateReviewPackageSubjectRequest("Demo", subject.DossierPath, ReviewPackageModes.Shadow,
            subject.Members, subject.Plan, "key");
        ReviewPackageSubjectValidation.Validate(request);

        Assert.Throws<ArgumentException>(() => ReviewPackageSubjectValidation.Validate(
            request with { Mode = ReviewPackageModes.Dossier }));
        Assert.Throws<ArgumentException>(() => ReviewPackageSubjectValidation.Validate(request with
        {
            Members = Enumerable.Range(0, 5).Select(index => subject.Members[0] with
            {
                TaskKey = $"AGT-{index}", Order = index,
            }).ToArray(),
        }));
        Assert.False(ReviewPackageModes.IsImplemented(ReviewPackageModes.Dossier));
        Assert.True(ReviewPackageModes.IsImplemented(ReviewPackageModes.Shadow));
    }

    [Fact]
    public void Lease_requeues_once_then_abandons_and_fences_renewals_and_reports()
    {
        var claim = new ReviewPackageClaimRequest("runner-a", "instance-a", 120);
        var (leased, lease) = ReviewPackageLeasePolicy.Claim("rpkg_1", ReviewPackageAttemptState.Queued, claim, Now);
        Assert.Equal("rpkg_1-a1", lease.AttemptId);

        var conflict = Assert.Throws<ReviewPackageConflictException>(() => ReviewPackageLeasePolicy.Renew("rpkg_1",
            lease.AttemptId, leased, new ReviewPackageRenewRequest("runner-a", "instance-a", lease.LeaseId, lease.Fence + 1), Now));
        Assert.Equal("stale-fence", conflict.Code);

        var requeued = ReviewPackageLeasePolicy.Expire(leased, Now.AddMinutes(3));
        Assert.Equal(ReviewPackageAttemptStatuses.Queued, requeued.Status);
        var (second, secondLease) = ReviewPackageLeasePolicy.Claim("rpkg_1", requeued, claim, Now.AddMinutes(3));
        Assert.Equal(2, secondLease.Fence);
        Assert.Equal(ReviewPackageAttemptStatuses.Abandoned,
            ReviewPackageLeasePolicy.Expire(second, Now.AddMinutes(6)).Status);

        var report = Report(ReviewPackageOutcomes.OverBudget, [], []) with
        {
            LeaseId = secondLease.LeaseId, Fence = secondLease.Fence,
        };
        var hash = ReviewPackageLeasePolicy.PayloadSha256(report);
        Assert.False(ReviewPackageLeasePolicy.IsDuplicateReport(secondLease.AttemptId, second, report, hash, Now.AddMinutes(4)));
        var reported = ReviewPackageLeasePolicy.Reported(second, report, hash, "rprp_1", Now.AddMinutes(4));
        Assert.True(ReviewPackageLeasePolicy.IsDuplicateReport(secondLease.AttemptId, reported, report, hash, Now.AddMinutes(9)));
        Assert.Throws<ReviewPackageConflictException>(() => ReviewPackageLeasePolicy.IsDuplicateReport(
            secondLease.AttemptId, reported, report with { Summary = "other" }, "other-hash", Now.AddMinutes(9)));
    }

    internal static ReviewPackageSubjectDto Subject()
    {
        ReviewPackageMemberDto Member(string key, int order, char sha)
            => new(key, "run-" + key, new string(sha, 40), order, ResultRef: "refs/heads/results/" + key);
        var commands = ReviewPackageAspects.Agent.Select(aspect => new ReviewCommandDto(
            "package-aspect-" + aspect, aspect, "codex", [], ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Review " + aspect, CliType: "codex", Model: "gpt-6-sol", ThinkingLevel: "medium")).ToArray();
        return new ReviewPackageSubjectDto("rpkg_1", ReviewSubjectKinds.Package, "Demo", "docs/x/index.html", "digest",
            ReviewPackageModes.Shadow, [Member("AGT-1", 0, 'a'), Member("AGT-2", 1, 'b')],
            new ReviewPackagePlanDto("repo", "https://example.invalid/repo.git", "main", commands), Now);
    }

    internal static ReviewPackageReportRequest Report(string outcome,
        IReadOnlyList<ReviewPackageMemberVerdictDto> verdicts, IReadOnlyList<ReviewPackageFindingDto> findings)
        => new("runner-a", "instance-a", "lease", 1, "report-key", outcome, "summary", new string('1', 40),
            new string('2', 40), [], verdicts, findings, [], 3, 40);

    private static ReviewPackageMemberVerdictDto Verdict(ReviewPackageReportRequest report, string member, string aspect)
        => Assert.Single(report.Verdicts, verdict => verdict.TaskKey == member && verdict.Aspect == aspect);

    private static ReviewPackageRawFinding Raw(string file, int line, string? member = null)
        => new(file, line, "concern", "Finding.", member, []);

    private static ReviewPackageOwnership Ownership()
        => new([new ReviewPackageOwnedRange("src/a.cs", 10, 12, "AGT-1"), new ReviewPackageOwnedRange("src/a.cs", 20, 21, "AGT-2")],
            new Dictionary<string, IReadOnlyList<string>> { ["src/a.cs"] = ["AGT-1", "AGT-2"] });
}
