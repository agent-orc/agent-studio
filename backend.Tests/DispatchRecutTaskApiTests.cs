using System.Net;
using System.Net.Http.Json;
using AgentStudio.Dispatch;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DispatchRecutTaskApiTests
{
    [Fact]
    public async Task Task_mutations_use_existing_scoped_endpoints_with_claim_and_DAG_guards()
    {
        var requests = new List<(string Method, string Path, string Body)>();
        using var handler = new Handler(async request =>
        {
            requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var api = new DispatchRecutTaskApi(client, "P / 1");
        await api.MoveAsync("P-1", TaskStates.Ready, TaskStates.Backlog, default);
        await api.ReferencesAsync("P-1", new() { DependsOn = [new("P-0", origin: "dispatch:packer")] }, default);
        Assert.Equal("POST", requests[0].Method);
        Assert.Contains("/api/tasks/P-1/move?project=P%20%2F%201", requests[0].Path);
        Assert.Contains("\"expectedSourceState\":\"2-ready\"", requests[0].Body);
        Assert.Contains("\"requireUnstarted\":true", requests[0].Body);
        Assert.Equal("PUT", requests[1].Method);
        Assert.Contains("/references?project=", requests[1].Path);
        Assert.Contains("dispatch:packer", requests[1].Body);
    }

    [Fact]
    public async Task A_refused_task_API_operation_does_not_report_success()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));
        using var client = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => new DispatchRecutTaskApi(client, "p")
            .MoveAsync("P-1", TaskStates.Ready, TaskStates.Backlog, default));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
