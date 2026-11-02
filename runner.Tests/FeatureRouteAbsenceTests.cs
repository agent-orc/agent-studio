using System.Net;
using System.Text;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// A host pauses a poll loop whose feature route the server does not map
/// (AGT-3366), logs that once, and picks the route up again when it answers.
/// </summary>
public sealed class FeatureRouteAbsenceTests
{
    public static TheoryData<Exception, bool> Failures => new()
    {
        { new TaskServerException(404, "absent", RouteAbsenceContract.ErrorCode), true },
        { new TaskServerException(404, "unknown host"), false },
        { new TaskServerException(404, "missing object", "source-object-missing"), false },
        { new TaskServerException(401, "refused", RouteAbsenceContract.ErrorCode), false },
        { new HttpRequestException("connection refused"), false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Only_the_route_absence_answer_pauses_a_loop(Exception failure, bool absent)
    {
        var logs = new List<string>();
        var absence = new FeatureRouteAbsence("git setup", logs.Add);

        Assert.Equal(absent, FeatureRouteAbsence.IsRouteAbsent(failure));
        Assert.Equal(absent, absence.TryRecordAbsent(failure));
        Assert.Equal(absent ? FeatureRouteAbsence.RecheckInterval : TimeSpan.FromSeconds(2),
            absence.NextDelay(TimeSpan.FromSeconds(2)));
        Assert.Equal(absent ? 1 : 0, logs.Count);
    }

    [Fact]
    public void Absence_is_logged_once_and_an_answer_resumes_the_poll_interval()
    {
        var logs = new List<string>();
        var absence = new FeatureRouteAbsence("device-login", logs.Add);
        var failure = new TaskServerException(404, "absent", RouteAbsenceContract.ErrorCode);

        for (var poll = 0; poll < 5; poll++) Assert.True(absence.TryRecordAbsent(failure));
        Assert.Equal(FeatureRouteAbsence.RecheckInterval, absence.NextDelay(TimeSpan.FromSeconds(2)));
        Assert.Single(logs);
        Assert.Contains("device-login paused", logs[0]);

        absence.RecordAnswered();
        absence.RecordAnswered();
        Assert.Equal(TimeSpan.FromSeconds(2), absence.NextDelay(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, logs.Count);
        Assert.Contains("device-login resumed", logs[1]);

        Assert.True(absence.TryRecordAbsent(failure));
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public async Task Claim_routes_tell_an_absent_route_from_an_empty_queue()
    {
        var status = HttpStatusCode.NotFound;
        string? body = null;
        using var http = new HttpClient(new Handler(_ => Respond(status, body)))
            { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: false);

        body = Unmapped;
        var gitSetup = await Assert.ThrowsAsync<TaskServerException>(() => client.ClaimGitSetupAsync("runner-a", default));
        Assert.True(FeatureRouteAbsence.IsRouteAbsent(gitSetup));
        Assert.True(FeatureRouteAbsence.IsRouteAbsent(
            await Assert.ThrowsAsync<TaskServerException>(() => client.ClaimDeviceLoginAsync(default))));
        Assert.True(FeatureRouteAbsence.IsRouteAbsent(
            await Assert.ThrowsAsync<TaskServerException>(() => client.GetProjectRepositoryAsync("PROJ-001", default))));
        Assert.True(FeatureRouteAbsence.IsRouteAbsent(
            await Assert.ThrowsAsync<TaskServerException>(() => client.GetHostAllocationVersionAsync("host-a", default))));
        Assert.True(FeatureRouteAbsence.IsRouteAbsent(
            await Assert.ThrowsAsync<TaskServerException>(() => client.ClaimPostWorkAsync(
                new PostWorkClaimRequest("host-a", "instance", 60, 0), default))));

        // The Task Server's own 404 keeps its meaning: no work, unknown host, no status yet.
        body = null;
        Assert.Null(await client.ClaimGitSetupAsync("runner-a", default));
        Assert.Null(await client.ClaimDeviceLoginAsync(default));
        Assert.Null(await client.GetProjectRepositoryAsync("PROJ-001", default));
        Assert.Equal(0, await client.GetHostAllocationVersionAsync("host-a", default));
        status = HttpStatusCode.NoContent;
        Assert.Null(await client.ClaimGitSetupAsync("runner-a", default));
    }

    [Fact]
    public async Task The_monolith_studio_project_summary_is_not_read_as_the_task_server_project_list()
    {
        // The monolith aliases /api/v1/projects to its Studio project summary,
        // which has no projectId. Reading it as ProjectDto gave the repository
        // probe a null id and an ArgumentNullException every minute.
        var body = """[{"id":"PROJ-001","displayName":"Alpha","workspaceId":"main","shortCode":"AL"}]""";
        using var http = new HttpClient(new Handler(_ => Respond(HttpStatusCode.OK, body)))
            { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: false);

        var summary = await Assert.ThrowsAsync<TaskServerException>(() => client.ListProjectsAsync(default));
        Assert.True(FeatureRouteAbsence.IsRouteAbsent(summary));

        body = """[{"projectId":"PROJ-001","workspaceId":"main","name":"Alpha","taskKeyPrefix":"AL","version":1}]""";
        var projects = await client.ListProjectsAsync(default);
        Assert.Equal("PROJ-001", Assert.Single(projects).ProjectId);
    }

    [Fact]
    public async Task Repository_probe_pauses_on_the_monolith_project_summary()
    {
        var logs = new List<string>();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Respond(HttpStatusCode.OK, """[{"id":"PROJ-001","displayName":"Alpha"}]""");
        })) { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: false);
        var options = new RunnerOptions
        {
            ServerUrl = "https://studio.test", RunnerId = "runner-a", RunnerName = "runner-a",
            Hostname = "host-a", BackendName = "test", WorkDir = Path.GetTempPath(),
            StateDir = Path.GetTempPath(), BaseBranch = "main",
        };
        using var stop = new CancellationTokenSource();

        var poll = new RunnerProjectRepositoryProbe(options, client, new NoGitProof(), line => { lock (logs) logs.Add(line); })
            .PollAsync(stop.Token);
        for (var attempt = 0; attempt < 100 && Volatile.Read(ref calls) == 0; attempt++) await Task.Delay(20);
        await Task.Delay(100);
        stop.Cancel();
        await poll;

        Assert.Equal(1, calls);
        lock (logs)
        {
            Assert.Single(logs);
            Assert.Contains("repository origin probe paused", logs[0]);
            Assert.DoesNotContain(logs, line => line.Contains("ArgumentNullException", StringComparison.Ordinal));
        }
    }

    private static string Unmapped
        => $$"""{"error":"{{RouteAbsenceContract.ErrorCode}}","code":"{{RouteAbsenceContract.ErrorCode}}","message":"This server does not map the route."}""";

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string? body)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = body is null ? new ByteArrayContent([]) : new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private sealed class NoGitProof : IRepositoryGitProof
    {
        public Task<RepositoryAccessProof> ProveAsync(
            RepositoryAccessBinding binding, string? generation, CancellationToken ct)
            => throw new InvalidOperationException("The probe must not reach Git without a project id.");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
