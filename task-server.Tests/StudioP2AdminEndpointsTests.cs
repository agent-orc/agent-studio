using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioP2AdminEndpointsTests
{
    [Fact]
    public async Task Orchestrator_config_upsert_round_trips_and_refreshes_updated_at()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2AdminApiFactory(temp.Path);
        using var client = Client(factory);

        var first = await client.PutAsJsonAsync(
            "/api/v1/studio/admin/config/orchestrator",
            new UpdateOrchestratorConfigRequest("""{"model":"default","thinkingLevel":"medium"}"""));
        first.EnsureSuccessStatusCode();
        var firstConfig = await first.Content.ReadFromJsonAsync<OrchestratorConfigDto>();
        Assert.Equal("""{"model":"default","thinkingLevel":"medium"}""", firstConfig!.ConfigJson);

        var second = await client.PutAsJsonAsync(
            "/api/v1/studio/admin/config/orchestrator",
            new UpdateOrchestratorConfigRequest("""{"model":"opus","thinkingLevel":"high"}"""));
        second.EnsureSuccessStatusCode();
        var secondConfig = await second.Content.ReadFromJsonAsync<OrchestratorConfigDto>();
        Assert.Equal("""{"model":"opus","thinkingLevel":"high"}""", secondConfig!.ConfigJson);
        Assert.True(secondConfig.UpdatedAt >= firstConfig.UpdatedAt);
    }

    [Fact]
    public async Task Prompt_override_lifecycle_covers_upsert_preview_rebaseline_review_review_all_and_delete()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2AdminApiFactory(temp.Path);
        using var client = Client(factory);
        const string name = "system-prompt";

        var upsert = await client.PutAsJsonAsync(
            $"/api/v1/studio/admin/prompts/{name}", new UpdatePromptOverrideRequest("Hello world"));
        upsert.EnsureSuccessStatusCode();
        var stored = await upsert.Content.ReadFromJsonAsync<PromptOverrideDto>();
        Assert.Equal("Hello world", stored!.OverrideContent);
        Assert.Null(stored.BaselineHash);

        var preview = await client.PostAsJsonAsync(
            $"/api/v1/studio/admin/prompts/{name}/preview", new PreviewPromptOverrideRequest());
        preview.EnsureSuccessStatusCode();
        var previewResponse = await preview.Content.ReadFromJsonAsync<PreviewPromptOverrideResponse>();
        Assert.Equal("Hello world", previewResponse!.EffectiveContent);

        var proposedPreview = await client.PostAsJsonAsync(
            $"/api/v1/studio/admin/prompts/{name}/preview", new PreviewPromptOverrideRequest("Proposed content"));
        proposedPreview.EnsureSuccessStatusCode();
        var proposedResponse = await proposedPreview.Content.ReadFromJsonAsync<PreviewPromptOverrideResponse>();
        Assert.Equal("Proposed content", proposedResponse!.EffectiveContent);

        var rebaseline = await client.PostAsync($"/api/v1/studio/admin/prompts/{name}/rebaseline", null);
        rebaseline.EnsureSuccessStatusCode();
        var rebaselineResponse = await rebaseline.Content.ReadFromJsonAsync<RebaselinePromptOverrideResponse>();
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Hello world")));
        Assert.Equal(expectedHash, rebaselineResponse!.BaselineHash);

        var review = await client.PostAsync($"/api/v1/studio/admin/prompts/{name}/review", null);
        review.EnsureSuccessStatusCode();
        var reviewResponse = await review.Content.ReadFromJsonAsync<PromptReviewAnnotationDto>();
        Assert.Equal("Hello world".Length, reviewResponse!.ContentLength);
        Assert.Equal(1, reviewResponse.LineCount);

        var secondUpsert = await client.PutAsJsonAsync(
            $"/api/v1/studio/admin/prompts/{name}", new UpdatePromptOverrideRequest("Line1\nLine2\nLine3"));
        secondUpsert.EnsureSuccessStatusCode();

        await client.PutAsJsonAsync(
            "/api/v1/studio/admin/prompts/other-prompt", new UpdatePromptOverrideRequest("Just one line"));

        var reviewAll = await client.PostAsync("/api/v1/studio/admin/prompts/review-all", null);
        reviewAll.EnsureSuccessStatusCode();
        var reviewAllResponse = await reviewAll.Content.ReadFromJsonAsync<ReviewAllPromptsResponse>();
        Assert.Equal(2, reviewAllResponse!.Reviewed.Count);
        var systemPromptAnnotation = Assert.Single(reviewAllResponse.Reviewed, item => item.Name == name);
        Assert.Equal(3, systemPromptAnnotation.LineCount);
        var otherPromptAnnotation = Assert.Single(reviewAllResponse.Reviewed, item => item.Name == "other-prompt");
        Assert.Equal(1, otherPromptAnnotation.LineCount);

        var delete = await client.DeleteAsync($"/api/v1/studio/admin/prompts/{name}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var deleteAgain = await client.DeleteAsync($"/api/v1/studio/admin/prompts/{name}");
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);

        var previewAfterDelete = await client.PostAsJsonAsync(
            $"/api/v1/studio/admin/prompts/{name}/preview", new PreviewPromptOverrideRequest());
        Assert.Equal(HttpStatusCode.NotFound, previewAfterDelete.StatusCode);

        var reviewAfterDelete = await client.PostAsync($"/api/v1/studio/admin/prompts/{name}/review", null);
        Assert.Equal(HttpStatusCode.NotFound, reviewAfterDelete.StatusCode);

        var rebaselineAfterDelete = await client.PostAsync($"/api/v1/studio/admin/prompts/{name}/rebaseline", null);
        Assert.Equal(HttpStatusCode.NotFound, rebaselineAfterDelete.StatusCode);
    }

    [Fact]
    public async Task Preview_review_and_rebaseline_on_an_unknown_prompt_return_404()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2AdminApiFactory(temp.Path);
        using var client = Client(factory);

        var preview = await client.PostAsJsonAsync(
            "/api/v1/studio/admin/prompts/never-created/preview", new PreviewPromptOverrideRequest());
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);

        var review = await client.PostAsync("/api/v1/studio/admin/prompts/never-created/review", null);
        Assert.Equal(HttpStatusCode.NotFound, review.StatusCode);

        var rebaseline = await client.PostAsync("/api/v1/studio/admin/prompts/never-created/rebaseline", null);
        Assert.Equal(HttpStatusCode.NotFound, rebaseline.StatusCode);
    }

    [Fact]
    public async Task Component_routing_resolves_known_prefixes_and_falls_back_to_unclassified()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2AdminApiFactory(temp.Path);
        using var client = Client(factory);

        var bus = await client.PostAsJsonAsync(
            "/api/v1/studio/component-routing/resolve", new ResolveComponentRoutingRequest("/api/bus/queues"));
        bus.EnsureSuccessStatusCode();
        var busResponse = await bus.Content.ReadFromJsonAsync<ResolveComponentRoutingResponse>();
        Assert.Equal("Bus", busResponse!.OwningComponent);

        var drift = await client.PostAsJsonAsync(
            "/api/v1/studio/component-routing/resolve", new ResolveComponentRoutingRequest("/api/drift/reports"));
        drift.EnsureSuccessStatusCode();
        var driftResponse = await drift.Content.ReadFromJsonAsync<ResolveComponentRoutingResponse>();
        Assert.Equal("Drift", driftResponse!.OwningComponent);

        var unknown = await client.PostAsJsonAsync(
            "/api/v1/studio/component-routing/resolve", new ResolveComponentRoutingRequest("/api/never-heard-of-it"));
        unknown.EnsureSuccessStatusCode();
        var unknownResponse = await unknown.Content.ReadFromJsonAsync<ResolveComponentRoutingResponse>();
        Assert.Equal("Unclassified", unknownResponse!.OwningComponent);
        Assert.Equal("Unclassified", unknownResponse.FrontendOwner);
    }

    [Fact]
    public async Task Prompt_enhance_and_title_generate_dispatch_fenced_studio_operations()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2AdminApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();

        var enhance = await client.PostAsJsonAsync(
            "/api/v1/studio/prompt/enhance", new EnhancePromptRequest("Make this task clearer"));
        Assert.Equal(HttpStatusCode.Accepted, enhance.StatusCode);
        var enhanceResponse = await enhance.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>();
        Assert.Equal(StudioOperationKinds.PromptEnhance, enhanceResponse!.Kind);
        Assert.Equal(StudioOperationStatuses.Pending, enhanceResponse.Status);
        var enhanceOperation = await store.GetStudioOperationAsync(enhanceResponse.OperationId, default);
        Assert.Contains("Make this task clearer", enhanceOperation!.RequestJson);

        var title = await client.PostAsJsonAsync(
            "/api/v1/studio/title/generate", new GenerateTitleRequest("Fix the login redirect bug"));
        Assert.Equal(HttpStatusCode.Accepted, title.StatusCode);
        var titleResponse = await title.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>();
        Assert.Equal(StudioOperationKinds.TitleGenerate, titleResponse!.Kind);
        var titleOperation = await store.GetStudioOperationAsync(titleResponse.OperationId, default);
        Assert.Contains("Fix the login redirect bug", titleOperation!.RequestJson);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p2-admin-test");
        return client;
    }

    private sealed class P2AdminApiFactory(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                }));
        }
    }
}
