using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class TaskServerModelRouteAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authority_routes_are_used_and_an_unreachable_authority_waits(bool unavailable)
    {
        var selected = new SelectionResult { Outcome = SelectionOutcome.Fallback,
            Primary = new("claude", "fixture-claude", "high"), Route = new("codex", "fixture-codex", "medium"),
            Rule = SelectionRules.QuotaCap, Reason = "fixture quota exhausted",
            EquivalentRoutes = [new("codex", "fixture-codex", "medium")] };
        var handler = new Handler(unavailable, selected);
        var client = new HttpModelRouteAdmission(new Clients(handler), Options.Create(new TaskServerOptions
        {
            QuotaAdmissionBaseUrl = "https://routing.invalid",
            ModelSelectionBearerToken = "routing-" + Guid.NewGuid().ToString("N"),
        }));
        var request = new ModelRouteAdmissionRequest(new SelectionRequest { Pins = new("claude", "fixture-claude", "high") }, [], []);
        var pending = await client.SelectAsync(request, default);
        Assert.Equal(SelectionOutcome.Wait, pending.Outcome);
        await client.RefreshesIdle();
        var result = await client.SelectAsync(request, default);
        Assert.Equal("https://routing.invalid/api/model-selection/plan", handler.Path);
        if (unavailable)
        {
            Assert.Equal(SelectionOutcome.Wait, result.Outcome);
            Assert.False(result.ShouldLaunch);
            Assert.Contains("authority unavailable", result.Reason);
        }
        else
        {
            Assert.Equal(selected.Route, result.Route);
            Assert.Contains(new ConfiguredModelRouteDto("codex", "fixture-codex"), client.ConfiguredRoutes);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(bool unavailable, SelectionResult result) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = JsonContent.Create(result, options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }) });
        }
    }
}
