using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgentStudio.TestSupport;
using Xunit;

namespace TaskServer.Tests;

public sealed class ModelCallEndpointTests
{
    [Fact]
    public async Task Historical_import_is_scoped_replayable_revisable_and_durable()
    {
        using var directory = new TempDirectory();
        var credential = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var engineCredential = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(start.AddDays(1));
        var at = start.ToUnixTimeMilliseconds();
        var batch = new HistoricalModelCallBatch(new string('a', 64), [
            new("AGT-1", 1, 1, at, 1, "work", "gpt-5", "high", 1000, 500, 100, null, new string('b', 64), "standard"),
            new("AGT-1", 1, 2, at, 1, "wait", null, null, 2000, 0, 200, null),
            new("AGT-1", 2, 1, at + 1, 1, "status", null, null, null, null, null, null),
            new("AGT-1", 2, 2, at + 1, 1, "work", "gpt-5", null, 100, 30, null, null, ServiceTier: "standard"),
            new("AGT-1", 2, 3, at + 1, 1, "work", "gpt-5", null, 200, null, 50, 20, ServiceTier: "standard"),
            new("AGT-1", 2, 4, at + 1, 1, "work", "gpt-5", null, null, 17, null, null, ServiceTier: "standard"),
            new("AGT-1", 2, 5, at + 1, 1, "work", "gpt-5", null, null, null, 5, null, ServiceTier: "standard"),
            new("AGT-1", 2, 6, at + 1, 1, "work", "gpt-5", null, null, null, 0, null, ServiceTier: "standard"),
            new("AGT-1", 2, 7, at + 1, 1, "work", "gpt-5", null, 11, null, null, null, ServiceTier: "standard"),
        ]);
        const string project = "ledger-project";
        const string other = "other-project";
        const string import = $"/api/v1/management/model-calls/{project}/historical-import";
        const string reads = $"/api/v1/projects/{project}/model-calls";
        var window = $"fromMs={at}&untilMs={at + 2}";
        var run = $"{reads}/query?{window}&card=AGT-1&run=1";
        var partialRun = $"{reads}/query?{window}&card=AGT-1&run=2";

        string id;
        string partialId;
        await using (var factory = new Factory(directory.Path, credential, engineCredential, clock))
        {
            using var owner = Client(factory, credential);
            using var engine = Client(factory, engineCredential);
            using var anonymous = Client(factory, null);
            using var viewer = Client(factory, credential);
            viewer.DefaultRequestHeaders.Add(CredentialViewProtocol.ActingRoleHeader, StudioUserRoles.Viewer);
            await PostAsync(owner, "/api/v1/workspaces", new CreateWorkspaceRequest("Ledger", "ledger-workspace"));
            await PostAsync(owner, "/api/v1/projects", new CreateProjectRequest("ledger-workspace", "Ledger", "AGT", project));
            await PostAsync(owner, "/api/v1/projects", new CreateProjectRequest("ledger-workspace", "Other", "OTH", other));
            await PostAsync(owner, $"/api/v1/projects/{project}/tasks", new CreateTaskRequest("Coding card", TaskKey: "AGT-1"));

            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(import, batch)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await engine.PostAsJsonAsync(import, batch)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(import, batch)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await engine.GetAsync(run)).StatusCode);

            var ack = await ImportAsync(owner, import, batch);
            Assert.Equal(new HistoricalModelCallAck(9, 0, 0, 1), ack);
            var first = (await owner.GetFromJsonAsync<ModelCallWindow>(run))!;
            id = first.Responses.Single(row => row.Ordinal == 1).Id;
            Assert.Equal(2, first.Totals.Calls);
            Assert.Equal(3300, first.Totals.Tokens);
            Assert.Equal(1, first.Totals.PricedCalls);
            Assert.Equal(2200, first.Totals.UnpricedTokens);
            Assert.NotNull(first.Totals.ListUsd);
            var aggregate = (await owner.GetFromJsonAsync<ModelCallWindow>($"{reads}/query?{window}"))!;
            Assert.Equal(9, aggregate.Totals.Calls);
            Assert.Equal(6, aggregate.Totals.MissingUsage);
            Assert.Equal(1, aggregate.Totals.StatusChecks);
            var card = Assert.Single(aggregate.Groups);
            Assert.Equal("AGT-1", card.Key);
            Assert.Equal(aggregate.Totals, card.Totals);
            var partial = (await owner.GetFromJsonAsync<ModelCallWindow>(partialRun))!;
            Assert.Equal(new ModelCallTotals(7, 383, 0, 1, 70, 47, 55, 0, 383, 6, null, 211), partial.Totals);
            Assert.Equal(first.Totals.Tokens + partial.Totals.Tokens, aggregate.Totals.Tokens);
            Assert.Equal(first.Totals.UnpricedTokens + partial.Totals.UnpricedTokens, aggregate.Totals.UnpricedTokens);
            var runs = (await owner.GetFromJsonAsync<ModelCallWindow>($"{reads}/query?{window}&card=AGT-1"))!;
            Assert.Equal(partial.Totals, runs.Groups.Single(group => group.Key == "2").Totals);
            var rows = partial.Responses.OrderBy(row => row.Ordinal).ToArray();
            Assert.Equal(new long?[] { null, 100, 250, 17, 5, 0, 11 }, rows.Select(row => row.KnownTokens));
            Assert.Equal(new long?[] { null, null, 250, null, null, null, null }, rows.Select(row => row.Tokens));
            Assert.Equal("incomplete-usage", rows[1].PriceStatus);
            Assert.Equal("incomplete-usage", rows[2].PriceStatus);
            partialId = rows[1].Id;
            var otherReads = $"/api/v1/projects/{other}/model-calls";
            Assert.Equal(0, (await owner.GetFromJsonAsync<ModelCallWindow>($"{otherReads}/query?{window}"))!.Totals.Calls);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{otherReads}/{id}")).StatusCode);
            Assert.True(File.Exists(Path.Combine(directory.Path, "model-calls.sqlite")));
        }

        await using (var restarted = new Factory(directory.Path, credential, engineCredential, clock))
        {
            using var owner = Client(restarted, credential);
            Assert.Equal(new HistoricalModelCallAck(0, 0, 9, 0), await ImportAsync(owner, import, batch));
            var beforePartial = (await owner.GetFromJsonAsync<ModelCallWindow>(partialRun))!;
            Assert.Equal(383, beforePartial.Totals.UnpricedTokens);
            var detail = (await owner.GetFromJsonAsync<ModelCallDetail>($"{reads}/{id}"))!;
            Assert.True(detail.HasResponseAlias);
            Assert.Equal("codex-response-record", detail.UsageBasis);
            Assert.Equal(500, detail.FreshInput);
            Assert.NotNull(detail.Price);
            Assert.Contains("/tasks/", detail.SourceCardUrl);

            var corrected = batch.Calls[0] with { Revision = 2, Output = 101 };
            var bad = batch.Calls[1] with { Revision = 2, Ordinal = 9 };
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(import, batch with { Calls = [corrected, bad] })).StatusCode);
            Assert.Equal(1, (await owner.GetFromJsonAsync<ModelCallDetail>($"{reads}/{id}"))!.UsageRevision);
            Assert.Equal(new HistoricalModelCallAck(0, 1, 0, 0),
                await ImportAsync(owner, import, batch with { Calls = [corrected] }));
            var completed = batch.Calls.Single(call => call.Run == 2 && call.Ordinal == 2) with { Revision = 2, Output = 40 };
            Assert.Equal(new HistoricalModelCallAck(0, 1, 0, 0),
                await ImportAsync(owner, import, batch with { Calls = [completed] }));
            Assert.Equal(new HistoricalModelCallAck(0, 0, 9, 0), await ImportAsync(owner, import, batch));
            var after = (await owner.GetFromJsonAsync<ModelCallWindow>(run))!;
            Assert.Equal(id, after.Responses.Single(row => row.Ordinal == 1).Id);
            Assert.Equal(2, after.Totals.Calls);
            Assert.Equal(3301, after.Totals.Tokens);
            var afterPartial = (await owner.GetFromJsonAsync<ModelCallWindow>(partialRun))!;
            Assert.Equal(7, afterPartial.Totals.Calls);
            Assert.Equal(423, afterPartial.Totals.Tokens);
            Assert.Equal(283, afterPartial.Totals.UnpricedTokens);
            Assert.Equal(5, afterPartial.Totals.MissingUsage);
            Assert.Equal(1, afterPartial.Totals.PricedCalls);
            Assert.NotNull(afterPartial.Totals.ListUsd);
            var revised = afterPartial.Responses.Single(row => row.Ordinal == 2);
            Assert.Equal(partialId, revised.Id);
            Assert.Equal(140, revised.Tokens);
        }
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string? credential)
    {
        var client = StudioTestClient.Create(factory);
        if (credential is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private static async Task PostAsync<T>(HttpClient client, string path, T payload)
    {
        using var response = await client.PostAsJsonAsync(path, payload);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HistoricalModelCallAck> ImportAsync(
        HttpClient client, string path, HistoricalModelCallBatch batch)
    {
        using var response = await client.PostAsJsonAsync(path, batch);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HistoricalModelCallAck>())!;
    }

    private sealed class Factory(string data, string credential, string engineCredential, ManualTimeProvider clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:DataDirectory"] = data,
                ["TaskServer:RetentionSchedulerEnabled"] = "false",
                ["AUTH"] = "bearer",
                ["STUDIO_AUTH_TOKEN"] = credential,
                ["ENGINE_AUTH_TOKEN"] = engineCredential,
            }));
        }
    }
}
