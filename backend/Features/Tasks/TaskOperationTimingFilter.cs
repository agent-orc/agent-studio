using System.Diagnostics;
using System.Globalization;
using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>
/// Adds lightweight timing around task API operations. The 30 ms budget is
/// enforced by perf benchmarks; this filter makes production regressions
/// visible through Server-Timing and structured logs without changing the
/// endpoint contracts.
///
/// <para>Board reads (<c>GET /api/tasks/grouped</c> and <c>GET /api/tasks/</c>)
/// also get a stage trace (AGT-3088): on request with
/// <c>X-Task-Switch-Trace: 1</c>, and otherwise for one read in
/// <c>BoardReadTrace:SampleEvery</c> (default every read; 0 turns sampling
/// off). Each traced read logs one <c>board-read-trace</c> line with the
/// snapshot, filter, git-freshness, compose and body stages and the index
/// spans nested in them.</para>
/// </summary>
internal sealed class TaskOperationTimingFilter : IEndpointFilter
{
    public const double BudgetMs = 30.0;

    private readonly ILogger<TaskOperationTimingFilter> _logger;
    private readonly TaskDetailLatencyMetrics? _detailLatency;
    private readonly int _boardTraceSampleEvery;
    private long _boardReads;

    public TaskOperationTimingFilter(ILogger<TaskOperationTimingFilter> logger, IConfiguration? configuration = null,
        TaskDetailLatencyMetrics? detailLatency = null)
    {
        _logger = logger;
        _detailLatency = detailLatency;
        _boardTraceSampleEvery = int.TryParse(configuration?["BoardReadTrace:SampleEvery"], out var every)
            ? Math.Max(0, every)
            : 1;
    }

    internal static bool IsBoardRead(string? route, string method)
        => method == "GET" && route is "/api/tasks/grouped" or "/api/tasks/";

    private bool SampleBoardRead()
        => _boardTraceSampleEvery > 0
           && Interlocked.Increment(ref _boardReads) % _boardTraceSampleEvery == 0;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var sw = Stopwatch.StartNew();
        var http = context.HttpContext;
        var route = (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)
            ?.RoutePattern.RawText;
        var coreRequest = route?.EndsWith("/{jobId}/core", StringComparison.Ordinal) == true;
        if (http.Request.Method == "GET" && route?.EndsWith("/{jobId}", StringComparison.Ordinal) == true)
        {
            var started = Stopwatch.GetTimestamp();
            http.Response.OnCompleted(() =>
            {
                _detailLatency?.Observe(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return Task.CompletedTask;
            });
        }
        var traceRequested = http.Request.Headers["X-Task-Switch-Trace"] == "1";
        var boardRead = IsBoardRead(route, http.Request.Method);
        var traceEnabled = boardRead
            ? traceRequested || SampleBoardRead()
            : traceRequested
              && http.Request.Method == "GET"
              && (coreRequest || route?.EndsWith("/{jobId}", StringComparison.Ordinal) == true);
        var traceLabel = boardRead ? "board-read-trace" : "task-switch-trace";
        var trace = traceEnabled ? TaskSwitchTrace.Begin(
            http.Request.Headers["X-Task-Request-Id"],
            http.Request.Headers["X-Task-Switch-Id"]) : null;
        IDisposable? gitScope = null;
        if (trace != null)
        {
            http.Response.Headers["X-Task-Request-Id"] = trace.RequestId;
            http.Response.Headers["X-Task-Switch-Id"] = trace.SwitchId;
            // Board reads open their own Git telemetry scope in the endpoint.
            if (!boardRead)
                gitScope = GitProcessTelemetry.BeginRequest(coreRequest ? "tasks/core" : "tasks/detail",
                    _logger, includeNested: true);
        }
        object? result = null;
        try
        {
            result = await next(context);
            if (trace != null && coreRequest)
            {
                var tally = GitProcessTelemetry.CurrentTally();
                if (tally.HasValue)
                    http.Response.Headers["X-Task-Core-Git-Spawns"] = tally.Value.Spawns.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["X-Task-Core-Workspace-Scans"] = trace.WorkspaceScans.ToString(CultureInfo.InvariantCulture);
            }
            return trace != null && result is IResult inner
                ? new TaskSwitchTracedResult(inner, trace, GitProcessTelemetry.CurrentTally(),
                    GitProcessTelemetry.CurrentTimeouts(), _logger, traceLabel)
                : result;
        }
        catch (Exception ex) when (trace != null)
        {
            _logger.LogInformation(traceLabel + " {Trace}",
                trace.Summary(ex is OperationCanceledException
                        ? (http.RequestAborted.IsCancellationRequested ? "aborted" : "timeout")
                        : "error",
                    http.Response.StatusCode, 0, GitProcessTelemetry.CurrentTally(),
                    GitProcessTelemetry.CurrentTimeouts()));
            throw;
        }
        finally
        {
            gitScope?.Dispose();
            trace?.Restore();
            sw.Stop();
            var operation = OperationName(http);
            var elapsedMs = sw.Elapsed.TotalMilliseconds;
            // The returned IResult has not executed yet, so the response still
            // carries its default 200; a 304 or an error result names its own.
            var statusCode = StatusCodeOf(result) ?? http.Response.StatusCode;

            if (!http.Response.HasStarted)
            {
                http.Response.Headers.Append(
                    "Server-Timing",
                    "task-op;dur=" + elapsedMs.ToString("0.###", CultureInfo.InvariantCulture));
            }

            var exceeded = elapsedMs > BudgetMs;
            if (exceeded)
            {
                _logger.LogWarning(
                    "task-operation-timing operation={Operation} method={Method} path={Path} elapsedMs={ElapsedMs:0.###} budgetMs={BudgetMs:0.###} exceeded=true statusCode={StatusCode}",
                    operation,
                    http.Request.Method,
                    http.Request.Path.Value,
                    elapsedMs,
                    BudgetMs,
                    statusCode);
            }
            else
            {
                _logger.LogDebug(
                    "task-operation-timing operation={Operation} method={Method} path={Path} elapsedMs={ElapsedMs:0.###} budgetMs={BudgetMs:0.###} exceeded=false statusCode={StatusCode}",
                    operation,
                    http.Request.Method,
                    http.Request.Path.Value,
                    elapsedMs,
                    BudgetMs,
                    statusCode);
            }
        }
    }

    internal static int? StatusCodeOf(object? result)
        => result is IStatusCodeHttpResult { StatusCode: { } statusCode } ? statusCode : null;

    private static string OperationName(HttpContext http)
    {
        var pattern = (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)
            ?.RoutePattern.RawText;
        return string.IsNullOrWhiteSpace(pattern)
            ? $"{http.Request.Method} {http.Request.Path.Value}"
            : $"{http.Request.Method} {pattern}";
    }
}
