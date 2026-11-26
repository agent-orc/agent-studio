extern alias Runner;

using System.Net.Http.Json;
using AgentStudio.Runner;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using RClient = Runner::AgentRunner.TaskServerClient;
using RClaimStatus = Runner::AgentRunner.RunnerClaimStatus;

namespace AgentStudio.Tests;

public sealed partial class LeasePlacementEndpointsTests
{
    [Theory]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.HumanReview)]
    public async Task In_flight_claim_ignores_completed_and_integrated_unverified_review_deliveries(string lane)
    {
        SeedTask(lane, "AGT-3481", prompt: "Edit backend/Features/Runner/ProjectRunner.cs.");
        SeedTask(TaskStates.Ready, "AGT-4015", prompt: "Edit backend/Features/Runner/ProjectRunner.cs.");
        TaskJsonFile.UpdateFieldOrThrow(Path.Combine(_watchPath, lane, "AGT-3481"), "commits",
            new[] { new TaskCommitInfo { Sha = "delivery" } });
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http, null);
        await AssignAsync(http, ProjectName);
        await AddRepositoryUrlAsync(http, "https://github.com/example/lease-placement.git");
        (await http.PutAsJsonAsync($"/api/projects/{ProjectName}/dispatch/overlap-window", new { window = "in-flight" })).EnsureSuccessStatusCode();
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>().SetIntegrationBranch(ProjectName, "develop");
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var card = scanner.FindJob("AGT-3481", _watchPath)!;
        var projection = TaskListGitProjection.Empty with
        {
            Integration = new Dictionary<string, TaskIntegrationStatus>
            {
                [card.TaskKey] = new() { Status = IntegrationStatuses.Integrated, IntegrationBranch = "develop", Sha = "delivery" },
            },
            Signatures = new Dictionary<string, string> { [card.TaskKey] = TaskGitSignature.For(card) },
        };
        factory.Services.GetRequiredService<TaskListGitProjectionCache>().SetSnapshot(_watchPath, projection, DateTimeOffset.UnixEpoch);
        var claimed = await ClaimWithSuccessfulPreflightAsync(client, Claim("integrated-review"));
        Assert.Equal(RClaimStatus.Claimed, claimed.Status);
        Assert.Equal("AGT-4015", claimed.JobId);
    }

    [Theory]
    [InlineData("delivery-attribution-ambiguous")]
    [InlineData("merge-conflict")]
    [InlineData("delivery-verdict-stale")]
    [InlineData("delivery-merge-round")]
    public async Task Integration_recovery_round_is_claimed_past_overlapping_review(string reason)
    {
        SeedTask(TaskStates.AutoReview, "AGT-4003", prompt: "Edit backend/Features/Runner/ProjectRunner.cs.");
        SeedTask(TaskStates.Ready, "AGT-3988", prompt: "Edit backend/Features/Runner/ProjectRunner.cs.");
        var folder = Path.Combine(_watchPath, TaskStates.Ready, "AGT-3988");
        File.WriteAllText(Path.Combine(folder, "pending-intent.json"), System.Text.Json.JsonSerializer.Serialize(
            new PendingIntent { SavedReason = reason, Prompt = "Finish the integration recovery.", SavedAt = DateTimeOffset.UnixEpoch.UtcDateTime },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http, null);
        await AssignAsync(http, ProjectName);
        await AddRepositoryUrlAsync(http, "https://github.com/example/lease-placement.git");
        (await http.PutAsJsonAsync($"/api/projects/{ProjectName}/dispatch/overlap-window", new { window = "in-flight" })).EnsureSuccessStatusCode();
        var claimed = await ClaimWithSuccessfulPreflightAsync(client, Claim("recovery"));
        Assert.Equal(RClaimStatus.Claimed, claimed.Status);
        Assert.Equal("AGT-3988", claimed.JobId);
    }

    [Fact]
    public async Task Off_is_persisted_and_claims_overlapping_work_while_retaining_predictions()
    {
        SeedTask(TaskStates.AutoReview, "AGT-4003", prompt: "Edit backend/Features/Runner/ProjectRunner.cs.");
        SeedTask(TaskStates.Ready, "AGT-4015", prompt: "Edit backend/Features/Runner/ProjectRunner.cs.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http, null);
        await AssignAsync(http, ProjectName);
        await AddRepositoryUrlAsync(http, "https://github.com/example/lease-placement.git");
        (await http.PutAsJsonAsync($"/api/projects/{ProjectName}/dispatch/overlap-window", new { window = "off", starvationMinutes = 15 })).EnsureSuccessStatusCode();
        var view = await http.GetStringAsync($"/api/projects/{ProjectName}/dispatch/overlap-window");
        Assert.Contains("\"stored\":\"off\"", view);
        Assert.Contains("\"starvationMinutes\":15", view);
        var claimed = await ClaimWithSuccessfulPreflightAsync(client, Claim("advisory"));
        Assert.Equal("AGT-4015", claimed.JobId);
        Assert.NotEmpty(factory.Services.GetRequiredService<OverlapPredictionLedger>().Read(ProjectName));
    }
}
