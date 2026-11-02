using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Pure delay policy of a host's claim polls (AGT-3432).</summary>
public static class ClaimPollBackoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Maximum = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A success returns to <see cref="Initial"/>, so a sign-in started after a
    /// recovery is claimed quickly. Each failure doubles the delay up to <see cref="Maximum"/>.
    /// </summary>
    public static TimeSpan Next(TimeSpan current, bool succeeded)
        => succeeded
            ? Initial
            : TimeSpan.FromTicks(Math.Min(Math.Max(current.Ticks, Initial.Ticks) * 2, Maximum.Ticks));
}

/// <summary>
/// Collapses a repeating poll failure into one line per change. A line names the
/// HTTP status and error code, or the exception type when no reply arrived.
/// </summary>
public sealed class ClaimPollFailureLog(string poll)
{
    private string? _failure;
    private int _count;

    /// <summary>The line to log for this failure, or null while the same failure repeats.</summary>
    public string? Failed(Exception exception)
    {
        var failure = Describe(exception);
        if (failure == _failure)
        {
            _count++;
            return null;
        }
        var line = _failure is null
            ? $"{poll} poll failed: {failure}"
            : $"{poll} poll failed: {failure} (previous failure {_failure} repeated {_count} times)";
        _failure = failure;
        _count = 1;
        return line;
    }

    /// <summary>The recovery line after failures, or null when the previous poll succeeded too.</summary>
    public string? Succeeded()
    {
        if (_failure is null) return null;
        var line = $"{poll} poll recovered after {_failure} repeated {_count} times";
        _failure = null;
        _count = 0;
        return line;
    }

    internal static string Describe(Exception exception) => exception switch
    {
        TaskServerException { ErrorCode: { } code } server => $"status={server.StatusCode} code={code}",
        TaskServerException server => $"status={server.StatusCode}",
        HttpRequestException { StatusCode: { } status } => $"status={(int)status}",
        _ => $"type={exception.GetType().Name}",
    };
}

/// <summary>The claim loop shared by the device-login and Git setup polls.</summary>
public static class ClaimPollLoop
{
    /// <summary>
    /// Polls until shutdown, backing off while polls fail. A server that answers
    /// <see cref="ApiErrorCodes.RouteUnavailable"/> does not offer the route in its
    /// profile: the loop logs that once and returns. Shutdown during the delay throws.
    /// </summary>
    public static async Task RunAsync(string poll, Func<CancellationToken, Task> pollOnce,
        Action<string> log, TimeProvider clock, CancellationToken ct)
    {
        var failures = new ClaimPollFailureLog(poll);
        var delay = ClaimPollBackoff.Initial;
        while (!ct.IsCancellationRequested)
        {
            var succeeded = true;
            try
            {
                await pollOnce(ct);
                if (failures.Succeeded() is { } recovered) log(recovered);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (TaskServerException ex) when (ex.ErrorCode == ApiErrorCodes.RouteUnavailable)
            {
                log($"{poll} poll stopped: the server does not offer this route (status={ex.StatusCode})");
                return;
            }
            catch (Exception ex)
            {
                succeeded = false;
                if (failures.Failed(ex) is { } line) log(line);
            }
            delay = ClaimPollBackoff.Next(delay, succeeded);
            await Task.Delay(delay, clock, ct);
        }
    }
}
