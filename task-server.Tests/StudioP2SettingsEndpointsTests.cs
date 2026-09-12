using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioP2SettingsEndpointsTests
{
    [Fact]
    public async Task Cli_settings_upsert_touches_only_their_own_column_and_return_the_full_row()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SettingsApiFactory(temp.Path);
        using var client = Client(factory);

        var economy = await client.PutAsJsonAsync(
            "/api/v1/studio/cli/model-routing/economy-mode", new SetEconomyModeRequest(true));
        economy.EnsureSuccessStatusCode();
        var economySettings = await economy.Content.ReadFromJsonAsync<CliSettingsDto>();
        Assert.True(economySettings!.EconomyMode);
        Assert.Null(economySettings.QuotaCapsJson);
        Assert.Null(economySettings.QuotaModelRoutesJson);
        Assert.Null(economySettings.QuotaWaitPolicyJson);

        var caps = await client.PutAsJsonAsync(
            "/api/v1/studio/cli/quota/caps", new SetQuotaCapsRequest("""{"claude":100}"""));
        caps.EnsureSuccessStatusCode();
        var capsSettings = await caps.Content.ReadFromJsonAsync<CliSettingsDto>();
        Assert.True(capsSettings!.EconomyMode);
        Assert.Equal("""{"claude":100}""", capsSettings.QuotaCapsJson);
        Assert.Null(capsSettings.QuotaModelRoutesJson);

        var routes = await client.PutAsJsonAsync(
            "/api/v1/studio/cli/quota/model-routes", new SetQuotaModelRoutesRequest("""{"claude":"opus"}"""));
        routes.EnsureSuccessStatusCode();
        var routesSettings = await routes.Content.ReadFromJsonAsync<CliSettingsDto>();
        Assert.True(routesSettings!.EconomyMode);
        Assert.Equal("""{"claude":100}""", routesSettings.QuotaCapsJson);
        Assert.Equal("""{"claude":"opus"}""", routesSettings.QuotaModelRoutesJson);
        Assert.Null(routesSettings.QuotaWaitPolicyJson);

        var waitPolicy = await client.PutAsJsonAsync(
            "/api/v1/studio/cli/quota/wait-policy", new SetCliQuotaWaitPolicyRequest("""{"maxWaitSeconds":60}"""));
        waitPolicy.EnsureSuccessStatusCode();
        var waitPolicySettings = await waitPolicy.Content.ReadFromJsonAsync<CliSettingsDto>();
        Assert.True(waitPolicySettings!.EconomyMode);
        Assert.Equal("""{"claude":100}""", waitPolicySettings.QuotaCapsJson);
        Assert.Equal("""{"claude":"opus"}""", waitPolicySettings.QuotaModelRoutesJson);
        Assert.Equal("""{"maxWaitSeconds":60}""", waitPolicySettings.QuotaWaitPolicyJson);

        var disableEconomy = await client.PutAsJsonAsync(
            "/api/v1/studio/cli/model-routing/economy-mode", new SetEconomyModeRequest(false));
        disableEconomy.EnsureSuccessStatusCode();
        var disabledSettings = await disableEconomy.Content.ReadFromJsonAsync<CliSettingsDto>();
        Assert.False(disabledSettings!.EconomyMode);
        Assert.Equal("""{"claude":100}""", disabledSettings.QuotaCapsJson);
        Assert.Equal("""{"claude":"opus"}""", disabledSettings.QuotaModelRoutesJson);
        Assert.Equal("""{"maxWaitSeconds":60}""", disabledSettings.QuotaWaitPolicyJson);
    }

    [Fact]
    public async Task Crash_recovery_pending_items_list_commit_and_dismiss_with_conflict_on_double_resolution()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SettingsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();

        var toCommit = await store.EnqueueCrashRecoveryPendingAsync(
            "prj_1", "tsk_1", """{"files":["a.txt"]}""", default);
        var toDismiss = await store.EnqueueCrashRecoveryPendingAsync(
            null, null, """{"files":["b.txt"]}""", default);

        var pending = await client.GetFromJsonAsync<List<CrashRecoveryPendingItemDto>>(
            "/api/v1/studio/crash-recovery/pending");
        Assert.Equal(2, pending!.Count);
        Assert.All(pending, item => Assert.Equal(CrashRecoveryPendingStatuses.Pending, item.Status));

        var commit = await client.PostAsync($"/api/v1/studio/crash-recovery/pending/{toCommit.Id}/commit", null);
        commit.EnsureSuccessStatusCode();
        var committed = await commit.Content.ReadFromJsonAsync<CrashRecoveryPendingItemDto>();
        Assert.Equal(CrashRecoveryPendingStatuses.Committed, committed!.Status);

        var commitAgain = await client.PostAsync($"/api/v1/studio/crash-recovery/pending/{toCommit.Id}/commit", null);
        Assert.Equal(HttpStatusCode.Conflict, commitAgain.StatusCode);

        var dismiss = await client.PostAsync($"/api/v1/studio/crash-recovery/pending/{toDismiss.Id}/dismiss", null);
        dismiss.EnsureSuccessStatusCode();
        var dismissed = await dismiss.Content.ReadFromJsonAsync<CrashRecoveryPendingItemDto>();
        Assert.Equal(CrashRecoveryPendingStatuses.Dismissed, dismissed!.Status);

        var pendingAfter = await client.GetFromJsonAsync<List<CrashRecoveryPendingItemDto>>(
            "/api/v1/studio/crash-recovery/pending");
        Assert.Empty(pendingAfter!);

        var commitUnknown = await client.PostAsync("/api/v1/studio/crash-recovery/pending/does-not-exist/commit", null);
        Assert.Equal(HttpStatusCode.NotFound, commitUnknown.StatusCode);
    }

    [Fact]
    public async Task Watch_paths_create_conflict_on_duplicate_name_and_delete_returns_404_when_missing()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SettingsApiFactory(temp.Path);
        using var client = Client(factory);

        var create = await client.PostAsJsonAsync(
            "/api/v1/studio/watch-paths", new CreateWatchPathRequest("repo-root", "prj_1", "**/*.cs"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<WatchPathDto>();
        Assert.Equal("repo-root", created!.Name);
        Assert.Equal("prj_1", created.ProjectId);
        Assert.Equal("**/*.cs", created.Pattern);

        var duplicate = await client.PostAsJsonAsync(
            "/api/v1/studio/watch-paths", new CreateWatchPathRequest("repo-root", null, "**/*.ts"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("watch-path-exists", (await duplicate.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var delete = await client.DeleteAsync("/api/v1/studio/watch-paths/repo-root");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var deleteAgain = await client.DeleteAsync("/api/v1/studio/watch-paths/repo-root");
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);

        var recreate = await client.PostAsJsonAsync(
            "/api/v1/studio/watch-paths", new CreateWatchPathRequest("repo-root", null, "**/*.ts"));
        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p2-settings-test");
        return client;
    }

    private sealed class P2SettingsApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
