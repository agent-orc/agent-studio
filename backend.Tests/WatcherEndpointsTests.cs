using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using AgentStudio.Tasks;
using AgentStudio.Watcher;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Review-mode API (§10.4): approve moves the proposal card to Ready with
/// the recommended model; reject suppresses the fingerprint. Proposals never
/// enter Ready by themselves - only an explicit decision call does that.
/// </summary>
public sealed class WatcherEndpointsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "watcher-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectPath;

    public WatcherEndpointsTests()
    {
        _projectPath = Path.Combine(_workspace, "alpha");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_projectPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Approve_MovesCardToReady_AndResolvesCase()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);

        var (jobId, proposalId) = SeedProposal(factory, "case-approve", "watcher-approve-job");

        using var response = await client.PostAsJsonAsync(
            $"/api/watcher/proposals/{proposalId}/decision",
            new WatcherProposalDecisionRequest("approved", null, null));

        response.EnsureSuccessStatusCode();

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var job = scanner.FindJob(jobId, _projectPath);
        Assert.NotNull(job);
        Assert.Equal(TaskStates.Ready, job!.State);

        var caseStore = factory.Services.GetRequiredService<WatcherCaseStore>();
        var savedCase = caseStore.FindById(_workspace, "case-approve");
        Assert.Equal(WatcherCaseStates.Resolved, savedCase!.State);
    }

    [Fact]
    public async Task Reject_RequiresReason_ArchivesCard_AndSuppressesFingerprint()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var (jobId, proposalId) = SeedProposal(factory, "case-reject", "watcher-reject-job");

        using var missingReason = await client.PostAsJsonAsync(
            $"/api/watcher/proposals/{proposalId}/decision",
            new WatcherProposalDecisionRequest("rejected", null, null));
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);

        using var response = await client.PostAsJsonAsync(
            $"/api/watcher/proposals/{proposalId}/decision",
            new WatcherProposalDecisionRequest("rejected", "Known noise: planned maintenance.", null));
        response.EnsureSuccessStatusCode();

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var job = scanner.FindJob(jobId, _projectPath);
        Assert.Equal(TaskStates.Archive, job!.State);

        var suppressions = factory.Services.GetRequiredService<WatcherSuppressionStore>();
        Assert.True(suppressions.IsSuppressed(_workspace, "rep-reject-fp"));
    }

    [Fact]
    public async Task DecidingTwice_IsRejectedWithConflict()
    {
        await using var factory = BuildFactory();
        using var client = CreateClient(factory);
        var (_, proposalId) = SeedProposal(factory, "case-twice", "watcher-twice-job");

        using var first = await client.PostAsJsonAsync(
            $"/api/watcher/proposals/{proposalId}/decision",
            new WatcherProposalDecisionRequest("approved", null, null));
        first.EnsureSuccessStatusCode();

        using var second = await client.PostAsJsonAsync(
            $"/api/watcher/proposals/{proposalId}/decision",
            new WatcherProposalDecisionRequest("approved", null, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    private (string JobId, string ProposalId) SeedProposal(WebApplicationFactory<Program> factory, string caseId, string jobId)
    {
        var mutations = factory.Services.GetRequiredService<TaskMutationService>();
        var created = mutations.CreateJob(new CreateTaskRequest
        {
            Id = jobId,
            Title = "Watcher draft",
            PromptMarkdown = "# Watcher finding\ntest",
            WatchPath = _projectPath,
            TargetState = TaskStates.Preparation,
            Tags = ["watcher-proposal", "watcher-repetition"],
        });
        Assert.False(string.IsNullOrWhiteSpace(created));

        var fingerprint = $"rep-{caseId["case-".Length..]}-fp";
        var caseStore = factory.Services.GetRequiredService<WatcherCaseStore>();
        var watcherCase = caseStore.Save(_workspace, new WatcherCase
        {
            Id = caseId,
            Fingerprint = fingerprint,
            DetectorClass = WatcherDetectorClasses.Repetition,
            Project = "Alpha Project",
            AffectedCards = [jobId],
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            OccurrenceCount = 2,
            SweepCount = 2,
            State = WatcherCaseStates.DecisionRequired,
            ProposalId = $"WPR-{caseId}",
            ProposalJobId = jobId,
        });

        var proposalStore = factory.Services.GetRequiredService<WatcherProposalStore>();
        var proposal = proposalStore.Save(_workspace, new WatcherProposal
        {
            Id = watcherCase.ProposalId!,
            CaseId = caseId,
            DetectorClass = WatcherDetectorClasses.Repetition,
            Fingerprint = fingerprint,
            Project = "Alpha Project",
            JobId = jobId,
            Title = "Watcher draft",
            RecommendedModel = "gpt-5.6-terra",
            RecommendedThinkingLevel = "medium",
            Tags = ["watcher-proposal", "watcher-repetition"],
        });

        return (jobId, proposal.Id);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = "Alpha Project",
                    ["WatchPaths:0:Path"] = _projectPath,
                    ["WatchPaths:0:RootPath"] = _projectPath,
                    ["Watcher:Enabled"] = "false",
                    ["Watcher:SuppressionDays"] = "14",
                }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }
}
