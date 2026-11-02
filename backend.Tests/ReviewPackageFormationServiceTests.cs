using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Prompts;
using AgentStudio.Review;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The shadow formation tick and its report projection (AGT-3400 S4): it forms
/// packages only under <c>review.package: shadow</c>, logs each decision once,
/// freezes a per-member plan, and never changes a member's lane, card files or
/// per-card review observations.
/// </summary>
public sealed class ReviewPackageFormationServiceTests : IDisposable
{
    private const string Dossier = "docs/review-gate-packages/index.html";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-package-formation-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeDeliveries _deliveries = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Review_package_off_forms_nothing()
    {
        var (service, store, _, _) = Build(ReviewPackageModes.Off);
        Seed("card-a", TaskStates.HumanReview, Dossier);
        _deliveries.Passed("card-a", Now.AddMinutes(-5));

        Assert.Empty(await service.TickAsync(default));
        Assert.Empty(store.List("Demo"));
        Assert.Empty(store.ReadFormationDecisions("Demo"));
    }

    [Fact]
    public async Task Shadow_forms_one_package_per_wave_and_leaves_every_card_unchanged()
    {
        var (service, store, scanner, _) = Build(ReviewPackageModes.Shadow);
        var a = Seed("card-a", TaskStates.HumanReview, Dossier, implementationIndex: 2, criteria: ["Cap 4", "Log decisions"]);
        var b = Seed("card-b", TaskStates.AutoReview, Dossier, implementationIndex: 1, dependsOn: "card-a");
        var coding = Seed("card-c", TaskStates.Progress, Dossier);
        var other = Seed("card-x", TaskStates.HumanReview, null);
        _deliveries.Passed("card-a", Now.AddMinutes(-20));
        _deliveries.Passed("card-b", Now.AddMinutes(-10));
        _deliveries.Passed("card-x", Now.AddMinutes(-10));
        // The scanner normalizes task.json on its first read; compare from there.
        Assert.Equal(4, scanner.ScanAllAutomationJobs().Count);
        var before = new[] { a, b, coding, other }.ToDictionary(folder => folder, Snapshot);

        var decisions = await service.TickAsync(default);

        var formed = Assert.Single(decisions);
        Assert.Equal(ReviewPackageFormationDecisions.Form, formed.Decision);
        Assert.Equal(["card-a", "card-b"], formed.Members);
        var record = Assert.Single(store.List("Demo"));
        Assert.Equal(ReviewSubjectKinds.Package, record.Subject.Kind);
        Assert.Equal(ReviewPackageAttemptStatuses.Queued, record.Status);
        Assert.Equal(ReviewPackageMembershipDigest.Compute(Dossier, record.Subject.Members), record.Subject.MembershipDigest);
        Assert.Equal(["run-card-a", "run-card-b"], record.Subject.Members.Select(member => member.RunAttemptId));
        Assert.Equal("Cap 4\nLog decisions", record.Subject.Members[0].AcceptanceCriteria);
        Assert.Equal(ReviewPackageAspects.Agent, record.Subject.Plan.Commands.Select(command => command.Aspect));
        Assert.All(record.Subject.Plan.Commands, command => Assert.Equal("gpt-6-sol", command.Model));
        var crossCard = Assert.Single(record.Subject.Plan.Commands,
            command => command.Aspect == ReviewPackageAspects.CrossCardConsistency);
        Assert.Contains("`card-a`, `card-b`", crossCard.Prompt);
        Assert.Contains("Do not judge whether a member meets its own acceptance criteria", crossCard.Prompt);
        Assert.Contains("## Things you should *not* do", crossCard.Prompt);
        Assert.Contains("```package-review", crossCard.Prompt);
        Assert.DoesNotContain("[[ASPECT_VERDICT", crossCard.Prompt);
        Assert.DoesNotContain("{{", crossCard.Prompt);
        Assert.All(before, entry => Assert.Equal(entry.Value, Snapshot(entry.Key)));
        Assert.Single(store.ReadFormationDecisions("Demo"));

        Assert.Empty(await service.TickAsync(default));
        Assert.Single(store.List("Demo"));
        Assert.Single(store.ReadFormationDecisions("Demo"));
    }

    [Fact]
    public async Task A_wait_for_a_card_in_review_is_logged_once()
    {
        var (service, store, _, _) = Build(ReviewPackageModes.Shadow);
        Seed("card-a", TaskStates.HumanReview, Dossier);
        Seed("card-b", TaskStates.AutoReview, Dossier);
        _deliveries.Passed("card-a", Now.AddMinutes(-5));

        Assert.Equal(ReviewPackageFormationDecisions.Wait, Assert.Single(await service.TickAsync(default)).Decision);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await service.TickAsync(default);
        Assert.Single(store.ReadFormationDecisions("Demo"));
        Assert.Empty(store.List("Demo"));

        _clock.Advance(TimeSpan.FromMinutes(56));
        var formed = Assert.Single(await service.TickAsync(default));
        Assert.Equal(ReviewPackageFormationPolicy.WindowReason, formed.Reason);
        Assert.Equal(2, store.ReadFormationDecisions("Demo").Count);
    }

    [Fact]
    public async Task A_report_writes_package_observations_and_the_shadow_report_compares_them_with_the_card()
    {
        var (service, store, scanner, pipeline) = Build(ReviewPackageModes.Shadow);
        var a = Seed("card-a", TaskStates.HumanReview, Dossier);
        var b = Seed("card-b", TaskStates.HumanReview, Dossier);
        _deliveries.Passed("card-a", Now.AddMinutes(-20));
        _deliveries.Passed("card-b", Now.AddMinutes(-10));
        WritePipeline(a, "round-card-a", "pass", 1_000);
        WritePipeline(b, "round-card-b", "block", 2_000);
        await service.TickAsync(default);
        var claim = store.Claim(new ReviewPackageClaimRequest("runner-a", "instance-a"));
        var lease = claim.Lease!;
        var report = new ReviewPackageReportRequest("runner-a", "instance-a", lease.LeaseId, lease.Fence, "report-1",
            ReviewPackageOutcomes.Completed, "done", new string('1', 40), new string('2', 40), [],
            [
                new ReviewPackageMemberVerdictDto("card-a", "code-quality", "block", "Cap differs."),
                new ReviewPackageMemberVerdictDto("card-b", "code-quality", "pass", "Clean."),
            ],
            [
                new ReviewPackageFindingDto("code-quality", "src/a.cs", 4, "block", "Cap is 5.", "card-a", false, []),
                new ReviewPackageFindingDto("cross-card-consistency", "src/b.cs", 9, "concern", "Name drift.", "card-b", true, ["card-a"]),
            ],
            [Command("code-quality", 600, 100), Command("cross-card-consistency", 400, 50)], 2, 30);

        var (receipt, subject, normalized) = store.Report(lease.AttemptId, report);
        var rows = ReviewPackageObservations.Rows(subject, normalized, receipt.AttemptId, receipt.ReceivedAt);
        Assert.Equal(rows.Count(row => row.Review.JobId == "card-a"),
            ReviewPackageObservations.Append(a, rows.Where(row => row.Review.JobId == "card-a")));
        Assert.Equal(0, ReviewPackageObservations.Append(a, rows.Where(row => row.Review.JobId == "card-a")));

        var observed = ReviewPackageObservations.Read(a);
        var quality = Assert.Single(observed, row => row.Review.Aspect == "code-quality");
        Assert.Equal(ReviewPackageObservations.Level, quality.Review.Level);
        Assert.Equal("block", quality.Status);
        Assert.Equal(1, quality.Review.Findings.Blocker);
        Assert.Equal(300, quality.Review.InputTokens);
        Assert.False(File.Exists(TaskPaths.ReviewObservationsLog(a)));

        var reports = new ReviewPackageShadowReportService(new MonolithReviewPackageAuthority(store), scanner, pipeline,
            TokenTestServices.Pricing(), _clock);
        var shadow = await reports.BuildAsync("Demo", ReviewPackageShadowReportService.DefaultDays, default);
        Assert.Equal(1, shadow.Packages);
        Assert.Equal(2, shadow.Members);
        var rowA = Assert.Single(shadow.Rows, row => row.TaskKey == "card-a" && row.Aspect == "code-quality");
        Assert.Equal(("block", "pass", "package-stricter"), (rowA.PackageStatus, rowA.CardStatus, rowA.Comparison));
        var rowB = Assert.Single(shadow.Rows, row => row.TaskKey == "card-b" && row.Aspect == "code-quality");
        Assert.Equal("card-stricter", rowB.Comparison);
        Assert.Equal(1, shadow.Agreement.CardBlocksMissed);
        Assert.Equal(1, Assert.Single(shadow.Rows, row => row.TaskKey == "card-b"
            && row.Aspect == "cross-card-consistency").CrossCardFindings);
        Assert.Equal(700, shadow.Tokens.PackagePrompt + shadow.Tokens.PackageOutput);
        Assert.Equal(450, shadow.Tokens.CrossCardPrompt + shadow.Tokens.CrossCardOutput);
        Assert.Equal(3_000 + 2 * 50, shadow.Tokens.CardPrompt + shadow.Tokens.CardOutput);
    }

    private (ReviewPackageFormationService Service, ReviewPackageStore Store, TaskScannerService Scanner,
        PipelineExecutionLog Pipeline) Build(string mode)
    {
        var watchPath = Path.Combine(_root, "Demo");
        Directory.CreateDirectory(watchPath);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
            ["PromptTemplates:RuntimePath"] = Path.Combine(RepositoryRoot.Find(), "prompts", "runtime"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetReviewPackage("Demo", mode);
        var store = new ReviewPackageStore(Path.Combine(_root, "review-packages"), _clock);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var service = new ReviewPackageFormationService(scanner, settings, _deliveries,
            new MonolithReviewPackageAuthority(store), store, new ReviewPackagePlanBuilder(prompts), _clock,
            NullLogger<ReviewPackageFormationService>.Instance);
        return (service, store, scanner,
            new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance));
    }

    private string Seed(string slug, string lane, string? dossier, int? implementationIndex = null,
        string? dependsOn = null, IReadOnlyList<string>? criteria = null)
    {
        var dir = Path.Combine(_root, "Demo", lane, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var task = new Dictionary<string, object?>
        {
            ["id"] = slug, ["title"] = slug, ["state"] = lane, ["order"] = 1, ["agent"] = "codex",
            ["createdAt"] = Now.AddDays(-1).ToString("o"),
        };
        if (dossier is not null) task["dossier"] = new { path = dossier, implementationIndex };
        if (dependsOn is not null) task["references"] = new { dependsOn = new[] { dependsOn } };
        if (criteria is not null) task["acceptanceScope"] = new { deliveryMode = "bounded-slice", slice = "S4", criteria };
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(task));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n");
        return dir;
    }

    private static void WritePipeline(string folder, string round, string verdict, long input)
        => File.WriteAllText(Path.Combine(folder, PipelineExecutionLog.FileName), JsonSerializer.Serialize(
            new PipelineExecutionRecord
            {
                JobId = Path.GetFileName(folder),
                Steps =
                [
                    new PipelineStepExecution
                    {
                        StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                        Status = verdict == "block" ? PipelineStepStatus.Failed : PipelineStepStatus.Passed,
                        Model = "gpt-6-sol", StartedAt = Now.UtcDateTime.AddMinutes(-40),
                        CompletedAt = Now.UtcDateTime.AddMinutes(-39), DurationMs = 60_000,
                        InputTokens = input, OutputTokens = 50, ExecutionAttemptId = round, Verdict = verdict,
                    },
                ],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private ReviewPackageCommandEvidenceDto Command(string aspect, long input, long output)
        => new("package-aspect-" + aspect, aspect, "codex", "gpt-6-sol", "medium", Now.UtcDateTime.AddMinutes(-2),
            Now.UtcDateTime.AddMinutes(-1), 0, "ok", input, output, 0, 0, "sha");

    private static string Snapshot(string folder)
        => string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order()
            .Select(path => path + ":" + File.ReadAllText(path)));

    private sealed class FakeDeliveries : IReviewPackageDeliveryFacts
    {
        private readonly Dictionary<string, DateTimeOffset> _passed = new(StringComparer.Ordinal);

        public void Passed(string task, DateTimeOffset at) => _passed[task] = at;

        public ReviewPackageDelivery? Read(TaskInfo task, DateTimeOffset since)
        {
            var plan = new ReviewPlanDto(
                ReviewPackageAspects.ComparedWithCard.Select(aspect => new ReviewCommandDto("aspect-" + aspect, aspect,
                    "codex", [], ExecutionKind: ReviewCommandKinds.AgentAspect, Prompt: "p", CliType: "codex",
                    Model: "gpt-6-sol", ThinkingLevel: "medium")).ToArray(),
                ReviewPackageAspects.ComparedWithCard, IntegrationRef: "main");
            return new ReviewPackageDelivery("run-" + task.Id, new string(task.Id[^1], 40), "refs/heads/results/" + task.Id,
                _passed.ContainsKey(task.Id) ? "round-" + task.Id : null,
                _passed.TryGetValue(task.Id, out var at) ? at : null, plan, "repo",
                "https://example.invalid/demo.git", [$"src/{task.Id}.cs"], 20, true);
        }
    }
}
