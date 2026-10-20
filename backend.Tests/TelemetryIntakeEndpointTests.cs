using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentStudio.Clients;
using AgentStudio.Security;
using AgentStudio.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// OTLP/HTTP intake end to end (IP-15): ingest-token authentication, media
/// types and limits, partial-success responses, back-pressure under a burst,
/// masking, the export hook, and the query API over what was stored.
/// </summary>
public sealed class TelemetryIntakeEndpointTests : IAsyncDisposable
{
    private const string Logs = "/api/telemetry/otlp/v1/logs";
    private const string Metrics = "/api/telemetry/otlp/v1/metrics";
    private const string Environment = "/api/projects/shop/environments/stage/telemetry";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "telemetry-intake-" + Guid.NewGuid().ToString("N"));
    // Five minutes after the fixtures' 2026-10-16T12:00:00Z, so they are inside the admission window.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 5, 0, TimeSpan.Zero));
    private readonly List<WebApplication> _apps = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps) await app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Token_is_issued_per_declared_environment_scoped_by_signal_and_revocable()
    {
        var harness = await Start();

        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.PostAsJsonAsync(
            "/api/projects/shop/environments/production/telemetry/tokens", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.PostAsJsonAsync(
            "/api/projects/unknown/environments/stage/telemetry/tokens", new { })).StatusCode);

        var created = await CreateToken(harness, ["logs"]);
        Assert.EndsWith("/api/telemetry/otlp", created.OtlpEndpoint);
        Assert.Equal("Authorization=Bearer " + created.Secret, created.OtlpHeaders);
        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Logs, Fixture("logs.pb"), created.Secret)).StatusCode);

        var wrongSignal = await Send(harness, Metrics, Fixture("metrics.pb"), created.Secret);
        Assert.Equal(HttpStatusCode.Forbidden, wrongSignal.StatusCode);
        Assert.Equal(7, StatusCode(await wrongSignal.Content.ReadAsByteArrayAsync()));

        var listed = await harness.Client.GetStringAsync(Environment + "/tokens");
        Assert.Contains(created.Token.Id, listed);
        Assert.DoesNotContain(created.Secret, listed);
        Assert.DoesNotContain("secretHash", listed, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent, (await harness.Client.DeleteAsync($"{Environment}/tokens/{created.Token.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(harness, Logs, Fixture("logs.pb"), created.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.DeleteAsync($"{Environment}/tokens/unknown")).StatusCode);
    }

    [Fact]
    public async Task Missing_invalid_and_expired_tokens_get_401_with_a_protobuf_status()
    {
        var harness = await Start();
        var expiring = await CreateToken(harness, null, _clock.GetUtcNow().UtcDateTime.AddMinutes(30));

        var missing = await Send(harness, Logs, Fixture("logs.pb"), token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("application/x-protobuf", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal(16, StatusCode(await missing.Content.ReadAsByteArrayAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(harness, Logs, Fixture("logs.pb"), "otl.nope.aaaaaaaaaaaaaaaaaaaaaaaa")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Logs, Fixture("logs.pb"), expiring.Secret)).StatusCode);
        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(harness, Logs, Fixture("logs.pb"), expiring.Secret)).StatusCode);
    }

    [Fact]
    public async Task Media_type_encoding_size_and_format_errors_are_typed()
    {
        var harness = await Start(new TelemetryOptions { MaxRequestBytes = 2048 });
        var token = (await CreateToken(harness)).Secret;

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Send(harness, Logs, Fixture("logs.pb"), token, "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Send(harness, Logs, Fixture("logs.pb"), token, encoding: "br")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(harness, Logs, [1, 2, 3], token, encoding: "gzip")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send(harness, Logs, new byte[4096], token)).StatusCode);
        // 64 KiB of zeros compress far below the limit but expand beyond it.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send(harness, Logs, Gzip(new byte[64 * 1024]), token, encoding: "gzip")).StatusCode);

        var malformed = await Send(harness, Logs, [0x0A, 0x05, 0x01], token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(3, StatusCode(await malformed.Content.ReadAsByteArrayAsync()));
        var malformedJson = await Send(harness, Logs, "{\"resourceLogs\":7}"u8.ToArray(), token, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, malformedJson.StatusCode);
        Assert.Equal(3, JsonDocument.Parse(await malformedJson.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Oversized_body_consumes_at_most_one_byte_past_the_limit()
    {
        const int limit = 2048;
        var harness = await Start(new TelemetryOptions { MaxRequestBytes = limit });
        var token = (await CreateToken(harness)).Secret;
        using var body = new CountingStream(new byte[64 * 1024]);

        var outcome = await Receive(harness, token, body);

        Assert.Equal(413, outcome.StatusCode);
        Assert.Equal(limit + 1, body.BytesRead);
    }

    [Fact]
    public async Task Success_reports_partial_success_in_the_request_encoding()
    {
        var harness = await Start();
        var token = (await CreateToken(harness)).Secret;

        var protobuf = await Send(harness, Metrics, Fixture("metrics.pb"), token);
        Assert.Equal(HttpStatusCode.OK, protobuf.StatusCode);
        var (rejected, message) = PartialSuccess(await protobuf.Content.ReadAsByteArrayAsync());
        Assert.Equal((1L, "Number data point has no finite value."), (rejected, message));

        var json = await Send(harness, Metrics, Fixture("metrics.json"), token, "application/json; charset=utf-8");
        var partial = JsonDocument.Parse(await json.Content.ReadAsStringAsync()).RootElement.GetProperty("partialSuccess");
        Assert.Equal("1", partial.GetProperty("rejectedDataPoints").GetString());

        var clean = await Send(harness, Logs, Fixture("logs.json"), token, "application/json");
        Assert.Equal("{}", await clean.Content.ReadAsStringAsync());
        var cleanProtobuf = await Send(harness, Logs, Gzip(Fixture("logs.pb")), token, encoding: "gzip");
        Assert.Equal(HttpStatusCode.OK, cleanProtobuf.StatusCode);
        Assert.Empty(await cleanProtobuf.Content.ReadAsByteArrayAsync());
        // Nothing is written until the writer drains: 5 + 5 metric points and 3 + 3 log records wait in the queue.
        Assert.Equal(16, harness.Queue.PendingRecords);
    }

    [Fact]
    public async Task Burst_beyond_the_queue_budget_is_refused_with_retry_after_and_never_waits()
    {
        // One environment may fill half of the 20 queued records.
        var harness = await Start(new TelemetryOptions { MaxQueuedRecords = 20, MaxConcurrentRequests = 64, MaxConcurrentRequestsPerEnvironment = 64 });
        var token = (await CreateToken(harness)).Secret;
        var body = Fixture("logs.pb"); // three records per request

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Send(harness, Logs, body, token)));

        Assert.Equal(3, responses.Count(item => item.StatusCode == HttpStatusCode.OK));
        var refused = responses.Where(item => item.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        Assert.Equal(17, refused.Count);
        Assert.All(refused, item => Assert.Equal("2", item.Headers.GetValues("Retry-After").Single()));
        Assert.Equal(14, StatusCode(await refused[0].Content.ReadAsByteArrayAsync()));
        Assert.Equal(9, harness.Queue.PendingRecords);
        Assert.Equal(17, harness.Queue.RefusedBatches);
        // A batch larger than the whole budget can never fit.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send(harness, Logs,
            Encoding.UTF8.GetBytes(ManyLogs(11)), token, "application/json")).StatusCode);

        // Studio reads keep working while the intake is saturated.
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync(Environment + "/logs")).StatusCode);

        Assert.Equal(9, await harness.Writer.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(0, harness.Queue.PendingRecords);
        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Logs, body, token)).StatusCode);
        var page = await harness.Client.GetFromJsonAsync<JsonElement>(Environment + "/logs?from=2026-10-16T11:00:00Z&limit=100");
        Assert.Equal(9, page.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task Stored_telemetry_is_masked_queryable_and_handed_to_export_hooks()
    {
        var sink = new RecordingSink();
        var harness = await Start(sink: sink);
        var token = (await CreateToken(harness)).Secret;
        var leaked = "Authorization: Bearer " + new string('k', 24);
        var logs = $$$"""
            {"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"shop-api"}}]},
              "scopeLogs":[{"logRecords":[{"timeUnixNano":"1792152000000000000","severityNumber":17,
                "body":{"stringValue":"login failed, {{{leaked}}}"},
                "attributes":[{"key":"db.url","value":{"stringValue":"password=hunter2hunter2"}}]}]}]}]}
            """;

        var metrics = """
            {"resourceMetrics":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"shop-api"}}]},
              "scopeMetrics":[{"metrics":[{"name":"db.pool.size","gauge":{"dataPoints":[{"timeUnixNano":"1792152000000000000",
                "asInt":"4","attributes":[{"key":"db.url","value":{"stringValue":"password=hunter2hunter2"}}]}]}}]}]}]}
            """;

        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Logs, Encoding.UTF8.GetBytes(logs), token, "application/json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Metrics, Fixture("metrics.pb"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Metrics, Encoding.UTF8.GetBytes(metrics), token, "application/json")).StatusCode);
        await harness.Writer.DrainOnceAsync(CancellationToken.None);

        var page = await harness.Client.GetStringAsync(Environment + "/logs?from=2026-10-16T11:00:00Z&minSeverity=error&text=login&attr.service.name=shop-api");
        Assert.Contains("[REDACTED]", page);
        Assert.DoesNotContain(new string('k', 24), page);
        Assert.DoesNotContain("hunter2", page);

        var series = await harness.Client.GetFromJsonAsync<JsonElement>(
            Environment + "/metrics?name=process.memory.usage&from=2026-10-16T11:00:00Z&to=2026-10-16T12:30:00Z&step=60&attr.state=used");
        var point = series.GetProperty("series")[0].GetProperty("points")[0];
        Assert.Equal(5_242_880d, point.GetProperty("value").GetDouble());
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T12:00:00Z"), point.GetProperty("time").GetDateTimeOffset());
        var names = await harness.Client.GetStringAsync(Environment + "/metrics/names");
        Assert.Contains("http.server.duration", names);
        var pool = await harness.Client.GetStringAsync(Environment + "/metrics?name=db.pool.size&from=2026-10-16T11:00:00Z&to=2026-10-16T12:30:00Z&step=60");
        Assert.Contains("[REDACTED]", pool);
        Assert.DoesNotContain("hunter2", pool);

        var exported = Assert.Single(sink.Batches, batch => batch.Logs.Count > 0);
        Assert.Equal(new TelemetryEnvironmentKey("shop", "stage"), exported.Environment);
        Assert.DoesNotContain(new string('k', 24), exported.Logs[0].Body);
        var exportedMetrics = Assert.Single(sink.Batches, batch => batch.Metrics.Count > 0).Metrics;
        Assert.Equal(6, exportedMetrics.Count);
        Assert.DoesNotContain(exportedMetrics, sample => sample.Attributes.Json.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gzip_bomb_of_points_is_refused_with_413_and_queues_nothing()
    {
        var harness = await Start();
        var token = (await CreateToken(harness)).Secret;
        var body = Gzip(TelemetryOtlpDecodingTests.PointFlood(points: 350_000, resourceAttributes: 128));
        Assert.True(body.Length < 200 * 1024, $"the compressed flood is {body.Length} bytes");

        var response = await Send(harness, Metrics, body, token, encoding: "gzip");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(8, StatusCode(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal((0L, 0L), (harness.Queue.PendingRecords, harness.Queue.PendingBytes));
    }

    [Fact]
    public async Task One_environment_cannot_keep_the_queue_full_for_the_others()
    {
        // Each environment may fill half of the six queued records: one logs.pb request of three records.
        var directory = new ShopStageDirectory { Declared = ["stage", "qa"] };
        var harness = await Start(new TelemetryOptions { MaxQueuedRecords = 6 }, directory: directory);
        var stage = (await CreateToken(harness)).Secret;
        var qa = (await CreateToken(harness, environment: "/api/projects/shop/environments/qa/telemetry")).Secret;

        Assert.Equal(200, (await Receive(harness, stage, new MemoryStream(Fixture("logs.pb")))).StatusCode);
        // The full environment is refused before its body is read, so a full queue costs no decoding.
        var unread = new GatedStream(Fixture("logs.pb"));
        var refused = await Receive(harness, stage, unread);
        Assert.Equal((429, (int?)TelemetryIntakeService.RetryAfterSeconds), (refused.StatusCode, refused.RetryAfterSeconds));
        Assert.False(unread.Reading.IsCompleted);

        Assert.Equal(200, (await Receive(harness, qa, new MemoryStream(Fixture("logs.pb")))).StatusCode);
        Assert.Equal(6, harness.Queue.PendingRecords);
        Assert.Equal(6, await harness.Writer.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(200, (await Receive(harness, stage, new MemoryStream(Fixture("logs.pb")))).StatusCode);
    }

    [Fact]
    public async Task Shared_attribute_sets_cannot_multiply_stored_bytes_past_the_request_budget()
    {
        var harness = await Start();
        var token = (await CreateToken(harness)).Secret;
        // About 1 KB of gzip: 20,000 log records sharing 15 attributes of 1,024 '<' would store about 300 MB.
        var logs = Gzip(TelemetryOtlpDecodingTests.SharedSetLogFlood(records: 20_000));
        Assert.True(logs.Length < 8 * 1024, $"the compressed flood is {logs.Length} bytes");
        var points = Gzip(TelemetryOtlpDecodingTests.PointFlood(points: 20_000, resourceAttributes: 15, fill: '<'));

        var refusedLogs = await Send(harness, Logs, logs, token, encoding: "gzip");
        var refusedPoints = await Send(harness, Metrics, points, token, encoding: "gzip");

        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.RequestEntityTooLarge), (refusedLogs.StatusCode, refusedPoints.StatusCode));
        Assert.Equal(8, StatusCode(await refusedLogs.Content.ReadAsByteArrayAsync()));
        Assert.Equal((0L, 0L), (harness.Queue.PendingRecords, harness.Queue.PendingBytes));

        // A thousand such records fit the budget and are stored with '<' as it is.
        var accepted = await Send(harness, Logs, Gzip(TelemetryOtlpDecodingTests.SharedSetLogFlood(records: 1_000)), token, encoding: "gzip");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1_000, await harness.Writer.DrainOnceAsync(CancellationToken.None));
        var page = await harness.Client.GetFromJsonAsync<JsonElement>(Environment + "/logs?from=2026-10-16T11:00:00Z&to=2026-10-16T13:00:00Z&limit=1");
        Assert.Equal(new string('<', 1024), page.GetProperty("records")[0].GetProperty("attributes").GetProperty("r0").GetString());
    }

    [Theory]
    [InlineData(Logs, "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"body\":{\"stringValue\":\"\\uD800\"}}]}]}]}")]
    [InlineData(Metrics, "{\"resourceMetrics\":[{\"scopeMetrics\":[{\"metrics\":[{\"name\":\"m\",\"gauge\":{\"dataPoints\":[{\"asDouble\":1e400}]}}]}]}]}")]
    public async Task Json_lone_surrogates_and_out_of_range_numbers_get_400(string path, string body)
    {
        var harness = await Start();
        var token = (await CreateToken(harness)).Secret;

        var response = await Send(harness, path, Encoding.UTF8.GetBytes(body), token, "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Queue.PendingRecords);
    }

    [Fact]
    public async Task Requests_beyond_the_concurrent_slots_get_429_while_a_body_is_still_arriving()
    {
        var harness = await Start(new TelemetryOptions { MaxConcurrentRequests = 1, MaxConcurrentRequestsPerEnvironment = 1 });
        var token = (await CreateToken(harness)).Secret;
        var slow = new GatedStream(Fixture("logs.pb"));

        var first = Receive(harness, token, slow);
        Assert.True(slow.Reading.IsCompleted);
        var second = await Receive(harness, token, new MemoryStream(Fixture("logs.pb")));

        Assert.Equal((429, (int?)TelemetryIntakeService.RetryAfterSeconds), (second.StatusCode, second.RetryAfterSeconds));
        slow.Open();
        Assert.Equal(200, (await first).StatusCode);
        Assert.Equal(200, (await Receive(harness, token, new MemoryStream(Fixture("logs.pb")))).StatusCode);
    }

    [Fact]
    public async Task One_environment_cannot_take_the_slots_of_the_others()
    {
        var directory = new ShopStageDirectory { Declared = ["stage", "qa"] };
        var harness = await Start(new TelemetryOptions { MaxConcurrentRequests = 2, MaxConcurrentRequestsPerEnvironment = 1 }, directory: directory);
        var stage = (await CreateToken(harness)).Secret;
        var qa = (await CreateToken(harness, environment: "/api/projects/shop/environments/qa/telemetry")).Secret;
        var slow = new GatedStream(Fixture("logs.pb"));

        var first = Receive(harness, stage, slow);
        Assert.True(slow.Reading.IsCompleted);

        Assert.Equal(429, (await Receive(harness, stage, new MemoryStream(Fixture("logs.pb")))).StatusCode);
        Assert.Equal(200, (await Receive(harness, qa, new MemoryStream(Fixture("logs.pb")))).StatusCode);
        slow.Open();
        Assert.Equal(200, (await first).StatusCode);
    }

    [Fact]
    public async Task A_body_that_stalls_past_the_deadline_gets_408_and_frees_its_slot()
    {
        var harness = await Start(new TelemetryOptions { MaxConcurrentRequests = 1, MaxConcurrentRequestsPerEnvironment = 1 });
        var token = (await CreateToken(harness)).Secret;
        var stalled = new GatedStream(Fixture("logs.pb"));

        var first = Receive(harness, token, stalled);
        Assert.True(stalled.Reading.IsCompleted);
        _clock.Advance(new TelemetryOptions().RequestDeadline);
        var outcome = await first;

        Assert.Equal((408, 4), (outcome.StatusCode, StatusCode(outcome.Body)));
        Assert.Equal(200, (await Receive(harness, token, new MemoryStream(Fixture("logs.pb")))).StatusCode);
        Assert.Equal(3, harness.Queue.PendingRecords);
    }

    [Fact]
    public async Task Token_stops_working_once_its_environment_is_no_longer_declared()
    {
        var directory = new ShopStageDirectory();
        var harness = await Start(directory: directory);
        var token = (await CreateToken(harness)).Secret;
        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Logs, Fixture("logs.pb"), token)).StatusCode);

        directory.Declared = [];
        // The intake re-reads the declarations at most every EnvironmentRecheck.
        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Logs, Fixture("logs.pb"), token)).StatusCode);
        _clock.Advance(TelemetryIntakeService.EnvironmentRecheck);
        var removed = await Send(harness, Logs, Fixture("logs.pb"), token);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        Assert.Equal(16, StatusCode(await removed.Content.ReadAsByteArrayAsync()));

        // Unreadable declarations are a retryable outage, not a reason to drop the token. Any failure
        // of the directory counts, and it is kept for EnvironmentRecheck like a successful read.
        directory.Declared = ["stage"];
        directory.Failure = new KeyNotFoundException("the environment has no target");
        _clock.Advance(TelemetryIntakeService.EnvironmentRecheck);
        var unreadable = await Send(harness, Logs, Fixture("logs.pb"), token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unreadable.StatusCode);
        Assert.Equal("2", unreadable.Headers.GetValues("Retry-After").Single());
        var reads = directory.Reads;
        directory.Failure = null;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(harness, Logs, Fixture("logs.pb"), token)).StatusCode);
        Assert.Equal(reads, directory.Reads);
        _clock.Advance(TelemetryIntakeService.EnvironmentRecheck);
        Assert.Equal(HttpStatusCode.OK, (await Send(harness, Logs, Fixture("logs.pb"), token)).StatusCode);
    }

    [Fact]
    public async Task A_failing_export_hook_does_not_lose_stored_data()
    {
        var harness = await Start(sink: new RecordingSink { Fail = true });
        var token = (await CreateToken(harness)).Secret;

        await Send(harness, Logs, Fixture("logs.pb"), token);
        Assert.Equal(3, await harness.Writer.DrainOnceAsync(CancellationToken.None));

        var page = await harness.Client.GetFromJsonAsync<JsonElement>(Environment + "/logs?from=2026-10-16T11:00:00Z");
        Assert.Equal(3, page.GetProperty("records").GetArrayLength());
    }

    [Theory]
    [InlineData("/metrics", "invalid-query")]
    [InlineData("/metrics?name=cpu&step=abc", "invalid-query")]
    [InlineData("/metrics?name=cpu&aggregation=p99", "invalid-query")]
    [InlineData("/metrics?name=cpu&from=2026-10-16T13:00:00Z&to=2026-10-16T12:00:00Z", "invalid-query")]
    [InlineData("/logs?cursor=garbage", "invalid-query")]
    [InlineData("/logs?minSeverity=loud", "invalid-query")]
    [InlineData("/logs?limit=5000", "invalid-query")]
    [InlineData("/logs?attr.a%22b=x", "invalid-query")]
    public async Task Query_api_rejects_invalid_parameters(string query, string error)
    {
        var harness = await Start();

        var response = await harness.Client.GetAsync(Environment + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Query_api_of_an_unknown_project_is_not_found()
    {
        var harness = await Start();
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync("/api/projects/unknown/environments/stage/telemetry/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync("/api/projects/shop/environments/Bad..Id/telemetry/logs")).StatusCode);
    }

    [Fact]
    public async Task Networked_profile_lets_only_the_intake_through_without_a_session()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked"
        }).Build();
        var store = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock);

        async Task<(int Status, bool Called)> Invoke(string method, string path, string scheme = "https")
        {
            var called = false;
            var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; }, configuration, store);
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Scheme = scheme;
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);
            return (context.Response.StatusCode, called);
        }

        Assert.True((await Invoke("POST", Logs)).Called);
        Assert.True((await Invoke("POST", Metrics)).Called);
        Assert.Equal((401, false), await Invoke("GET", Logs));
        Assert.Equal((401, false), await Invoke("POST", "/api/telemetry/otlp/v1/traces"));
        Assert.Equal((401, false), await Invoke("GET", Environment + "/logs"));
        Assert.Equal((426, false), await Invoke("POST", Logs, scheme: "http"));
    }

    [Fact]
    public async Task Local_client_identity_boundary_admits_exporters_without_a_client_id()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        var identities = new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance);
        identities.EnsureLoaded();
        var called = false;
        var middleware = new ClientIdentityMiddleware(_ => { called = true; return Task.CompletedTask; }, identities, NullLogger<ClientIdentityMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = Logs;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    private async Task<Harness> Start(TelemetryOptions? options = null, ITelemetryExportSink? sink = null,
        ITelemetryEnvironmentDirectory? directory = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton((options ?? new TelemetryOptions()) with { StoreRoot = Path.Combine(_root, "telemetry") });
        builder.Services.AddSingleton(new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock));
        builder.Services.AddSingleton<ITelemetryIngestCredentials, AccessStoreTelemetryIngestCredentials>();
        builder.Services.AddSingleton(directory ?? new ShopStageDirectory());
        builder.Services.AddSingleton<ITelemetryValueMasker, CredentialTelemetryValueMasker>();
        builder.Services.AddSingleton<TelemetryStore>();
        builder.Services.AddSingleton<TelemetryIntakeQueue>();
        builder.Services.AddSingleton<TelemetryIntakeService>();
        // The writer is driven by the test (DrainOnceAsync) instead of running as a hosted service.
        builder.Services.AddSingleton<TelemetryWriter>();
        if (sink is not null) builder.Services.AddSingleton(sink);
        var app = builder.Build();
        app.MapTelemetryEndpoints();
        await app.StartAsync();
        _apps.Add(app);
        return new Harness(app.GetTestClient(), app.Services.GetRequiredService<TelemetryIntakeQueue>(),
            app.Services.GetRequiredService<TelemetryWriter>(), app.Services);
    }

    private static async Task<TelemetryIngestTokenCreatedResponse> CreateToken(Harness harness, string[]? signals = null,
        DateTime? expiresAt = null, string environment = Environment)
    {
        var response = await harness.Client.PostAsJsonAsync(environment + "/tokens", new { name = "collector", signals, expiresAt });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TelemetryIngestTokenCreatedResponse>())!;
    }

    private static Task<HttpResponseMessage> Send(Harness harness, string path, byte[] body, string? token,
        string contentType = "application/x-protobuf", string? encoding = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (encoding is not null) request.Content.Headers.ContentEncoding.Add(encoding);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return harness.Client.SendAsync(request);
    }

    /// <summary>Calls the intake directly, so a test controls when the body arrives.</summary>
    private static Task<OtlpIntakeOutcome> Receive(Harness harness, string token, Stream body)
        => harness.Services.GetRequiredService<TelemetryIntakeService>().ReceiveAsync(TelemetrySignals.Logs,
            "application/x-protobuf", null, "Bearer " + token, null, body, CancellationToken.None);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "otlp", name));

    private static byte[] Gzip(byte[] body)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(body);
        return output.ToArray();
    }

    private static string ManyLogs(int count)
        => "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":["
           + string.Join(",", Enumerable.Repeat("{\"body\":{\"stringValue\":\"x\"}}", count)) + "]}]}]}";

    /// <summary>google.rpc.Status.code (field 1).</summary>
    private static int StatusCode(byte[] body)
    {
        var reader = new ProtoWireReader(body);
        while (reader.TryReadTag(out var field, out var type))
        {
            if (field == 1 && type == ProtoWireType.Varint) return (int)reader.ReadVarint();
            reader.Skip(type);
        }
        return -1;
    }

    /// <summary>Export*ServiceResponse.partial_success (field 1): rejected count (1) and error message (2).</summary>
    private static (long Rejected, string Message) PartialSuccess(byte[] body)
    {
        long rejected = 0;
        var message = "";
        var outer = new ProtoWireReader(body);
        while (outer.TryReadTag(out var field, out var type))
        {
            if (field != 1 || type != ProtoWireType.LengthDelimited) { outer.Skip(type); continue; }
            var inner = new ProtoWireReader(outer.ReadBytes());
            while (inner.TryReadTag(out var innerField, out var innerType))
            {
                if (innerField == 1 && innerType == ProtoWireType.Varint) rejected = (long)inner.ReadVarint();
                else if (innerField == 2 && innerType == ProtoWireType.LengthDelimited) message = inner.ReadString();
                else inner.Skip(innerType);
            }
        }
        return (rejected, message);
    }

    private sealed record Harness(HttpClient Client, TelemetryIntakeQueue Queue, TelemetryWriter Writer, IServiceProvider Services);

    private sealed class ShopStageDirectory : ITelemetryEnvironmentDirectory
    {
        public IReadOnlyCollection<string> Declared { get; set; } = ["stage"];
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }

        public IReadOnlyCollection<string>? Environments(string project)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            return string.Equals(project, "shop", StringComparison.OrdinalIgnoreCase) ? Declared : null;
        }
    }

    /// <summary>A request body whose bytes arrive only after <see cref="Open"/>.</summary>
    private sealed class GatedStream(byte[] content) : Stream
    {
        private readonly MemoryStream _content = new(content);
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reading => _reading.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Open() => _gate.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reading.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            return await _content.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingStream(byte[] content) : Stream
    {
        private readonly MemoryStream _content = new(content);
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _content.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingSink : ITelemetryExportSink
    {
        public bool Fail { get; init; }
        public List<TelemetryExportBatch> Batches { get; } = [];
        public string Name => "recording";

        public ValueTask ExportAsync(TelemetryExportBatch batch, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("export target down");
            Batches.Add(batch);
            return ValueTask.CompletedTask;
        }
    }
}
