using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class CardBudgetAdmissionTests(InitializedStoreFixture fixture) : IClassFixture<InitializedStoreFixture>
{
    [Fact]
    public async Task Coding_claim_refreshes_card_spend_and_skips_only_the_exhausted_card()
    {
        using var temp = fixture.CreateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        var meter = new CardMeter();
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            clock, new ApplicationResultFinalizationSummaryGenerator(), null, null, meter);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Budget project", "BUD"), "test", default);
        var exhausted = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Exhausted", "Work", "2-ready"), "test", default);
        var sibling = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Sibling", "Work", "2-ready"), "test", default);
        await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest("runner", "host-a", "instance-a",
            "1.0.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), "test", default);
        await store.StartWorkspaceQuotaRefreshesAsync("test");
        await store.WorkspaceQuotaRefreshesIdle();
        // The first snapshot allowed both. A receipt arrives before the next admission,
        // without advancing the ordinary project quota cache's 30-second TTL.
        meter.Refusals = new Dictionary<string, string> { [exhausted.TaskId] = "card-budget-usd: 10 / 10 USD" };
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await store.WorkspaceQuotaRefreshesIdle();
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(sibling.TaskId, claim.Task!.TaskId);
        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, exhausted.TaskKey, default))!.State);
    }

    private sealed class CardMeter : IWorkspaceQuotaAdmission
    {
        public IReadOnlyDictionary<string, string> Refusals { get; set; } = new Dictionary<string, string>();
        public Task<WorkspaceQuotaVerdict> AdmitAsync(string projectId, CancellationToken ct)
            => Task.FromResult(new WorkspaceQuotaVerdict(true, null, CardRefusals: Refusals));
    }
}
