using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class WorkspaceQuotaAdmissionTests
{
    [Fact]
    public async Task Remote_refusal_is_returned_to_claim_authority()
    {
        var serviceToken = "quota-service-" + Guid.NewGuid().ToString("N");
        using var client = new HttpClient(new ReplyHandler(request =>
        {
            Assert.Equal("http://localhost/api/quota/admission/project-1", request.RequestUri!.ToString());
            Assert.Equal(serviceToken, request.Headers.Authorization?.Parameter);
            return
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new WorkspaceQuotaVerdict(false, "Workspace quota reached.")),
            };
        }));
        var admission = new HttpWorkspaceQuotaAdmission(new ClientFactory(client),
            Options.Create(new TaskServerOptions { QuotaAdmissionBaseUrl = "http://localhost", QuotaAdmissionBearerToken = serviceToken }));
        var verdict = await admission.AdmitAsync("project-1", default);
        Assert.False(verdict.Allowed);
        Assert.Contains("quota", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unavailable_meter_reports_warning_without_stopping_claims()
    {
        using var client = new HttpClient(new ReplyHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var admission = new HttpWorkspaceQuotaAdmission(new ClientFactory(client),
            Options.Create(new TaskServerOptions { QuotaAdmissionBaseUrl = "http://localhost" }));
        var verdict = await admission.AdmitAsync("project-1", default);
        Assert.True(verdict.Allowed);
        Assert.True(verdict.MeterUnavailable);
        Assert.Contains("unavailable", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_configuration_never_assumes_the_dev_backend_port()
        => Assert.Null(new TaskServerOptions().QuotaAdmissionBaseUrl);

    [Fact]
    public async Task Unresolved_project_answer_counts_as_meter_unavailable_once()
    {
        using var client = new HttpClient(new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"allowed":true,"message":"Project 'Project' is unknown to the quota meter.","meterUnavailable":true}""",
                System.Text.Encoding.UTF8, "application/json"),
        }));
        var admission = new HttpWorkspaceQuotaAdmission(new ClientFactory(client),
            Options.Create(new TaskServerOptions { QuotaAdmissionBaseUrl = "http://localhost" }));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var cache = new WorkspaceQuotaVerdictCache(admission, clock);
        var transition = Assert.Single(await cache.RefreshAsync(["Project"], default));
        Assert.True(transition.MeterUnavailable);
        Assert.True(cache.Read("Project")!.Allowed);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty(await cache.RefreshAsync(["Project"], default));
        Assert.Equal(WorkspaceQuotaAdmissionStates.Unreachable, cache.State);
    }

    [Fact]
    public async Task Cache_reports_outage_and_recovery_once_with_one_card_notice()
    {
        var meter = new SwitchableAdmission(new WorkspaceQuotaVerdict(true, "Meter down.", true));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var cache = new WorkspaceQuotaVerdictCache(meter, clock);
        // No verdict yet: the claim path admits without an event.
        Assert.Null(cache.Read("p"));
        Assert.Null(cache.TryConsumeCardNotice("p"));

        Assert.True(Assert.Single(await cache.RefreshAsync(["p", "p"], default)).MeterUnavailable);
        Assert.Equal("Meter down.", cache.TryConsumeCardNotice("p"));
        Assert.Null(cache.TryConsumeCardNotice("p"));
        Assert.Empty(await cache.RefreshAsync(["p"], default));
        Assert.Equal(1, meter.Calls);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty(await cache.RefreshAsync(["p"], default));
        Assert.Null(cache.TryConsumeCardNotice("p"));

        meter.Verdict = new WorkspaceQuotaVerdict(true, null);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(Assert.Single(await cache.RefreshAsync(["p"], default)).MeterUnavailable);
        Assert.Equal(WorkspaceQuotaAdmissionStates.On, cache.State);
        Assert.Equal(3, meter.Calls);
    }

    [Fact]
    public async Task Concurrent_refreshes_share_one_request_and_report_the_transition_once()
    {
        var meter = new BlockingAdmission();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var cache = new WorkspaceQuotaVerdictCache(meter, clock);
        var first = cache.RefreshAsync(["p"], default);
        var second = cache.RefreshAsync(["p"], default);
        Assert.False(first.IsCompleted || second.IsCompleted);
        meter.Release.SetResult(new WorkspaceQuotaVerdict(true, "Meter down.", true));
        var transitions = (await first).Concat(await second).ToList();
        Assert.Equal(1, meter.Calls);
        Assert.Single(transitions);
    }

    private sealed class SwitchableAdmission(WorkspaceQuotaVerdict verdict) : IWorkspaceQuotaAdmission
    {
        public WorkspaceQuotaVerdict Verdict { get; set; } = verdict;
        public int Calls { get; private set; }
        public Task<WorkspaceQuotaVerdict> AdmitAsync(string projectId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Verdict);
        }
    }

    private sealed class BlockingAdmission : IWorkspaceQuotaAdmission
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource<WorkspaceQuotaVerdict> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorkspaceQuotaVerdict> AdmitAsync(string projectId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Release.Task;
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
}
