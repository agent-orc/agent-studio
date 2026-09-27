using System.Diagnostics;
using System.Globalization;
using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>
/// Adds lightweight timing around task API operations. The 30 ms budget is
/// enforced by perf benchmarks; this filter makes production regressions
/// visible through Server-Timing and structured logs without changing the
/// endpoint contracts.
/// </summary>
internal sealed class TaskOperationTimingFilter : IEndpointFilter
{
    public const double BudgetMs = 30.0;

    private readonly ILogger<TaskOperationTimingFilter> _logger;

    public TaskOperationTimingFilter(ILogger<TaskOperationTimingFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var sw = Stopwatch.StartNew();
        var http = context.HttpContext;
        var traceEnabled = http.Request.Headers["X-Task-Switch-Trace"] == "1"
            && http.Request.Method == "GET"
            && (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)
                ?.RoutePattern.RawText?.EndsWith("/{jobId}", StringComparison.Ordinal) == true;
        var trace = traceEnabled ? TaskSwitchTrace.Begin(
            http.Request.Headers["X-Task-Request-Id"],
            http.Request.Headers["X-Task-Switch-Id"]) : null;
        IDisposable? gitScope = null;
        if (trace != null)
        {
            http.Response.Headers["X-Task-Request-Id"] = trace.RequestId;
            http.Response.Headers["X-Task-Switch-Id"] = trace.SwitchId;
            gitScope = GitProcessTelemetry.BeginRequest("tasks/detail", _logger, includeNested: true);
        }
        try
        {
            var result = await next(context);
            return trace != null && result is IResult inner
                ? new TaskSwitchTracedResult(inner, trace, GitProcessTelemetry.CurrentTally(),
                    GitProcessTelemetry.CurrentTimeouts(), _logger)
                : result;
        }
        catch (Exception ex) when (trace != null)
        {
            _logger.LogInformation("task-switch-trace {Trace}",
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
                    http.Response.StatusCode);
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
                    http.Response.StatusCode);
            }
        }
    }

    private static string OperationName(HttpContext http)
    {
        var pattern = (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)
            ?.RoutePattern.RawText;
        return string.IsNullOrWhiteSpace(pattern)
            ? $"{http.Request.Method} {http.Request.Path.Value}"
            : $"{http.Request.Method} {pattern}";
    }
}
