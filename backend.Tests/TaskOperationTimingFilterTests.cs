using AgentStudio.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3088: the timing filter logs the status the returned result will
/// write (a 304 used to be logged as 200, because the response still held its
/// default when the filter logged), and it traces board reads with one
/// <c>board-read-trace</c> summary line per read.
/// </summary>
public sealed class TaskOperationTimingFilterTests
{
    [Fact]
    public async Task ConditionalBoardRead_IsLoggedWithTheNotModifiedStatus()
    {
        var logger = new CapturingLogger();
        var filter = new TaskOperationTimingFilter(logger, Configuration(sampleEvery: 0));
        var http = BoardRequest("/api/tasks/grouped");

        var result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ => ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status304NotModified)));

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var timing = Assert.Single(logger.Lines, line => line.Contains("task-operation-timing", StringComparison.Ordinal));
        Assert.Contains("statusCode=304", timing, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("board-read-trace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampledBoardRead_LogsOneTraceLineWithItsStages()
    {
        var logger = new CapturingLogger();
        var filter = new TaskOperationTimingFilter(logger, Configuration(sampleEvery: 1));
        var http = BoardRequest("/api/tasks/grouped");
        http.Response.Body = new MemoryStream();

        var result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ =>
            {
                using (TaskSwitchTrace.Span("snapshot"))
                using (TaskSwitchTrace.Span("index.lookup")) { }
                using (TaskSwitchTrace.Span("compose"))
                {
                    using (TaskSwitchTrace.Span("compose.cards"))
                    {
                        TaskSwitchTrace.RecordMeasured("compose.lease", System.Diagnostics.Stopwatch.Frequency / 1000);
                        TaskSwitchTrace.RecordMeasured("compose.identity", System.Diagnostics.Stopwatch.Frequency / 2000);
                    }
                }
                return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status304NotModified));
            });
        await Assert.IsAssignableFrom<IResult>(result).ExecuteAsync(http);

        var trace = Assert.Single(logger.Lines, line => line.StartsWith("board-read-trace", StringComparison.Ordinal));
        Assert.Contains("\"snapshot\"", trace, StringComparison.Ordinal);
        Assert.Contains("\"index.lookup\"", trace, StringComparison.Ordinal);
        Assert.Contains("\"compose\"", trace, StringComparison.Ordinal);
        Assert.Contains("\"compose.cards\"", trace, StringComparison.Ordinal);
        Assert.Contains("\"compose.lease\"", trace, StringComparison.Ordinal);
        Assert.Contains("\"compose.identity\"", trace, StringComparison.Ordinal);
        Assert.Contains("\"status\":304", trace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsampledDetailRead_IsNotTraced()
    {
        var logger = new CapturingLogger();
        var filter = new TaskOperationTimingFilter(logger, Configuration(sampleEvery: 1));
        var http = BoardRequest("/api/tasks/{jobId}");

        await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.DoesNotContain(logger.Lines, line => line.Contains("-trace", StringComparison.Ordinal));
    }

    private static IConfiguration Configuration(int sampleEvery)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BoardReadTrace:SampleEvery"] = sampleEvery.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

    private static DefaultHttpContext BoardRequest(string route)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        http.Request.Method = "GET";
        http.Request.Path = route;
        http.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: route));
        return http;
    }

    private sealed class CapturingLogger : ILogger<TaskOperationTimingFilter>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }
}
