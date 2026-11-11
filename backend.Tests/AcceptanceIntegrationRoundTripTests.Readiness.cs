using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3564 - the Human Review readiness preview runs the completion's own
/// admission checks. Each scenario asks the preview first, then the real
/// acceptance, and requires both to give the same answer: the preview neither
/// moves the card nor admits a card the acceptance refuses.
/// </summary>
public sealed partial class AcceptanceIntegrationRoundTripTests
{
    [Fact]
    public async Task Readiness_verified_delivery_is_ready_and_the_batch_accept_moves_it()
    {
        var deliverySha = PublishDelivery("ready-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        WriteVerifiedTree(card.FolderPath, deliverySha, deliverySha);

        var item = Readiness(deps).Evaluate(card);

        Assert.Equal(AcceptanceReadinessClasses.Ready, item.Readiness);
        Assert.Equal(CompletionAdmissionStages.Admitted, item.Stage);
        Assert.Null(item.Round);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);

        var results = await deps.Transitions.BatchMoveAsync(
            [new BatchMoveItem { JobId = Slug, WatchPath = _watchPath, TargetState = TaskStates.Completed }],
            cause: TimelineActors.Human("owner"));

        Assert.Equal("moved", Assert.Single(results).Status);
        Assert.Equal(TaskStates.Completed, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    public async Task Readiness_unverified_merged_tree_names_the_refusal_the_acceptance_answers()
    {
        var deliverySha = PublishDelivery("unverified-delivery.txt", "delivered\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        RunGit(_repo, "checkout", "-q", "-b", "unrelated-readiness-tree", "main");
        File.WriteAllText(Path.Combine(_repo, "unrelated-readiness.txt"), "unrelated\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "unrelated verification tree");
        var unrelated = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "checkout", "-q", "main");
        WriteVerifiedTree(card.FolderPath, unrelated, deliverySha);

        var item = Readiness(deps).Evaluate(card);
        var refused = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(AcceptanceReadinessClasses.IntegratedUnverified, item.Readiness);
        Assert.Equal(CompletionAdmissionStages.IntegrationUnverified, item.Stage);
        Assert.Equal(MoveJobStatus.IntegrationFailed, refused.Status);
        Assert.Equal(refused.Message, item.Refusal);
        Assert.Contains("no gate passed on the merged tree", item.Reason);
        Assert.NotNull(item.Round);
        Assert.Equal($"refs/heads/{DeliveryRef}", item.LatestResultRef);
        Assert.Equal(item.LatestResultRef, item.Round!.BaseRef);
    }

    [Fact]
    public async Task Readiness_pending_delivery_is_not_integrated_and_the_batch_accept_refuses_it()
    {
        var deliverySha = PublishDelivery("pending-delivery.txt", "remote work\n");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;

        var item = Readiness(deps).Evaluate(card);
        var results = await deps.Transitions.BatchMoveAsync(
            [
                new BatchMoveItem { JobId = Slug, WatchPath = _watchPath, TargetState = TaskStates.Completed },
                new BatchMoveItem { JobId = "no-such-card", WatchPath = _watchPath, TargetState = TaskStates.Completed },
            ],
            cause: TimelineActors.Human("owner"));

        Assert.Equal(AcceptanceReadinessClasses.NotIntegrated, item.Readiness);
        Assert.Equal(CompletionAdmissionStages.IntegrationMissing, item.Stage);
        Assert.Collection(results,
            refused =>
            {
                Assert.Equal("integration-failed", refused.Status);
                Assert.Equal(item.Refusal, refused.Message);
            },
            missing => Assert.Equal("not-found", missing.Status));
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    public async Task Readiness_review_concerns_carry_the_aspect_and_the_settlement_concern_text()
    {
        var deliverySha = PublishDelivery("concern-delivery.txt", "delivered\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var deps = Build(deliverySha, contentStatus: "concerns",
            contentSummary: "The lane shows no reason on the card.");
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;

        var item = Readiness(deps).Evaluate(card);
        var refused = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(AcceptanceReadinessClasses.ReviewConcerns, item.Readiness);
        Assert.Equal(CompletionAdmissionStages.Content, item.Stage);
        Assert.Equal("requirement-fit", item.Aspect);
        Assert.Equal("The lane shows no reason on the card.", item.Concern);
        Assert.Contains("requirement-fit verdict was concerns", item.Refusal);
        Assert.Equal(refused.Message, item.Refusal);
        Assert.Contains("The lane shows no reason on the card.", item.Round!.Prompt);
        Assert.Contains($"refs/heads/{DeliveryRef}", item.Round.Prompt);
    }

    [Fact]
    public async Task Readiness_without_a_content_review_is_no_content_verdict()
    {
        var deliverySha = PublishDelivery("unreviewed-delivery.txt", "delivered\n");
        var deps = Build(deliverySha, seedContent: false);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;

        var item = Readiness(deps).Evaluate(card);
        var refused = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(AcceptanceReadinessClasses.NoContentVerdict, item.Readiness);
        Assert.Contains("no requirement-fit verdict", item.Reason);
        Assert.Equal(refused.Message, item.Refusal);
    }

    [Fact]
    public async Task Readiness_delivery_for_an_older_brief_is_verdict_stale()
    {
        var deliverySha = PublishDelivery("stale-brief-delivery.txt", "delivered\n");
        var deps = Build(deliverySha, briefVersion: RunDispositionPolicy.BriefVersion("An older brief"));
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;

        var item = Readiness(deps).Evaluate(card);
        var refused = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(AcceptanceReadinessClasses.VerdictStale, item.Readiness);
        Assert.Equal(refused.Message, item.Refusal);
    }

    [Fact]
    public async Task ReadinessHttp_reports_the_project_lane_and_moves_nothing()
    {
        var deliverySha = PublishDelivery("readiness-http.txt", "remote work\n");
        var deps = Build(deliverySha);
        using var factory = new TestHostFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _tempDir,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repo,
                    ["WatchPaths:0:RepositoryPath"] = _repo,
                    ["DeliveryChain:Guarded"] = "false",
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<ProjectSettingsService>();
                    services.AddSingleton(deps.Settings);
                    services.RemoveAll<AttemptAuthorityService>();
                    services.AddSingleton(deps.Authority);
                });
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using (var empty = await client.PostAsJsonAsync("/api/tasks/acceptance-readiness", new { }).WaitAsync(AsyncTestDeadline))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var response = await client.PostAsJsonAsync("/api/tasks/acceptance-readiness", new { project = Project })
            .WaitAsync(AsyncTestDeadline);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(Slug, item.GetProperty("jobId").GetString());
        Assert.Equal(AcceptanceReadinessClasses.NotIntegrated, item.GetProperty("readiness").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()));
        Assert.Equal(1, body.RootElement.GetProperty("counts").GetProperty(AcceptanceReadinessClasses.NotIntegrated).GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("counts").GetProperty(AcceptanceReadinessClasses.Ready).GetInt32());
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    private static AcceptanceReadinessService Readiness(Deps deps)
        => new(deps.Transitions, NullLogger<AcceptanceReadinessService>.Instance, deps.Authority);

    private static void WriteVerifiedTree(string folder, string treeSha, string deliverySha)
        => IntegrationVerificationStore.Write(folder, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = treeSha,
            IntegrationBranch = "develop",
            DeliveryShas = [deliverySha],
            Evidence = IntegrationVerificationEvidence.MergeGate,
            GateVerdict = nameof(BuildTestGateVerdict.Ok),
            Reason = "The pre-develop build gate passed on this delivery tree.",
        });
}
