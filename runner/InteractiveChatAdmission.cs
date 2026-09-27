using System.Collections.Concurrent;

namespace AgentRunner;

/// <summary>
/// Polls interactive work independently of coding admission. A chat claim is
/// started immediately, even when every coding slot is occupied or the coding
/// load gate is closed. Long-running turns borrow coding capacity only for the
/// purpose of admitting future coding work.
/// </summary>
internal sealed class InteractiveChatAdmission
{
    internal static readonly TimeSpan HeavyAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task<RemoteChatWorkClaimResponse>> _claim;
    private readonly Func<RemoteChatWorkItem, CancellationToken, ChatExecution> _execute;
    private readonly ConcurrentDictionary<string, ActiveChat> _active = new();

    public InteractiveChatAdmission(RunnerOptions options, TaskServerClient client, Action<string> log)
    {
        _log = log;
        _claim = ct => client.ClaimProjectChatWorkAsync(
            new RemoteChatWorkClaimRequest(
                options.RunnerId, options.RunnerName, options.Hostname), ct);
        _execute = (work, ct) =>
        {
            var runner = new RemoteProjectChatRunner(options, client, log);
            return new ChatExecution(runner.RunAsync(work, ct), () => runner.IsCpuHeavy);
        };
    }

    internal InteractiveChatAdmission(
        Func<CancellationToken, Task<RemoteChatWorkClaimResponse>> claim,
        Func<RemoteChatWorkItem, CancellationToken, Task<int>> execute,
        Action<string> log)
    {
        _log = log;
        _claim = claim;
        _execute = (work, ct) => new ChatExecution(execute(work, ct), () => false);
    }

    public int ActiveCount => _active.Count;
    public int HeavyCount => _active.Values.Count(chat =>
        !chat.Execution.IsCompleted && IsHeavy(chat.StartedAt, DateTime.UtcNow, chat.IsCpuHeavy()));

    public Task DrainAsync() => Task.WhenAll(_active.Values.Select(chat => chat.Execution));

    internal static bool IsHeavy(DateTime startedAt, DateTime now, bool sustainedCpu = false)
        => sustainedCpu || now - startedAt >= HeavyAfter;

    internal static int FreeCodingSlots(int ceiling, int codingCount, int heavyChatCount)
        => Math.Max(0, ceiling - codingCount - heavyChatCount);

    public async Task PollAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(shutdown);
                await Task.Delay(PollInterval, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) when (RemoteRunnerDaemon.IsTransientServerFault(ex))
            {
                _log($"interactive-chat-poll-retry error={ex.GetType().Name}: {ex.Message}");
                await DelayAsync(shutdown);
            }
            catch (Exception ex)
            {
                _log($"interactive-chat-poll-failed error={ex}");
                await DelayAsync(shutdown);
            }
        }
    }

    internal async Task PollOnceAsync(CancellationToken shutdown)
    {
        // The batch bound gives the coding loop CPU time. There is no chat
        // admission ceiling and no dependency on coding occupancy.
        for (var i = 0; i < 32 && !shutdown.IsCancellationRequested; i++)
        {
            var claim = await _claim(shutdown);
            if (claim.Status != RemoteChatWorkClaimStatuses.Claimed || claim.Work is null)
                break;
            var work = claim.Work;
            _log($"claimed interactive chat {work.ProjectName}/{work.Kind} independently of coding slots");
            var execution = _execute(work, shutdown);
            _active[work.WorkId] = new ActiveChat(DateTime.UtcNow, execution.Task, execution.IsCpuHeavy);
            _ = ObserveCompletionAsync(work.WorkId, execution.Task);
        }
    }

    private async Task ObserveCompletionAsync(string workId, Task<int> execution)
    {
        try { _log($"interactive chat {workId} completed with exit code {await execution}"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"interactive chat {workId} failed: {ex}"); }
        finally { _active.TryRemove(workId, out _); }
    }

    private static async Task DelayAsync(CancellationToken shutdown)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(1), shutdown); }
        catch (OperationCanceledException) { }
    }

    private sealed record ChatExecution(Task<int> Task, Func<bool> IsCpuHeavy);
    private sealed record ActiveChat(DateTime StartedAt, Task<int> Execution, Func<bool> IsCpuHeavy);
}
