using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Runner;

/// <summary>What a stop request did for one card.</summary>
public enum RunStopDispatch
{
    /// <summary>No such card.</summary>
    NotFound,

    /// <summary>A local CLI process was signalled, which is the pre-AGT-2870 behaviour.</summary>
    StoppedLocally,

    /// <summary>
    /// A held attempt has no local CLI process to signal. The request is
    /// recorded for its owner and observed before the next pipeline step.
    /// </summary>
    RemoteStopRequested,

    /// <summary>Nothing is running here and no remote attempt holds the card.</summary>
    NoRunningAttempt,
}

/// <summary>
/// Plain facts the stop decision needs, read once by the endpoint: whether a
/// local process was actually signalled, and whether a runner currently
/// holds this card's fenced lease.
/// </summary>
public sealed record RunStopFacts(bool LocalProcessStopped, bool RemoteLeaseHeld);

/// <summary>
/// Pure dispatch decision for <c>POST /api/tasks/{key}/stop</c> (AGT-2870).
///
/// <para>
/// Before this, the endpoint asked the local CLI backend only, so a card
/// executing on a remote host answered 404 and an operator had no way to stop
/// the run other than killing the process on the host by hand. A held lease
/// is positive evidence that an owner can end the run at its next boundary.
/// </para>
/// </summary>
public static class RemoteRunStopPolicy
{
    public static RunStopDispatch Decide(RunStopFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.LocalProcessStopped) return RunStopDispatch.StoppedLocally;
        return facts.RemoteLeaseHeld
            ? RunStopDispatch.RemoteStopRequested
            : RunStopDispatch.NoRunningAttempt;
    }
}

/// <summary>Wire values of <see cref="RemoteRunStopRequest.Reason"/>.</summary>
public static class RemoteRunStopReasons
{
    /// <summary>The operator pressed Pause.</summary>
    public const string User = "user";

    /// <summary>
    /// Pause and Send: a follow-up is queued and must start as the next round as
    /// soon as the stopped attempt hands back.
    /// </summary>
    public const string Followup = "followup";

    /// <summary>A supervisor watchdog asked for the run to end.</summary>
    public const string Watchdog = "watchdog";

    /// <summary>An explicit receipt action stops the run and holds the card.</summary>
    public const string DecisionStop = "decision-stop";

    public static string From(RunStopReason reason) => reason switch
    {
        RunStopReason.FollowupPause => Followup,
        RunStopReason.Watchdog => Watchdog,
        _ => User,
    };

    public static bool IsFollowup(string? reason)
        => string.Equals((reason ?? string.Empty).Trim(), Followup, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A durable stop command and its delivery receipt for one fenced attempt.</summary>
public sealed record RemoteRunStopRequest(
    string TaskKey,
    string Reason,
    DateTime RequestedAtUtc,
    string? AttemptId = null,
    string? RequestedBy = null)
{
    public string CommandId { get; init; } = string.Empty;
    public string? RunnerId { get; init; }
    public long FencingToken { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime? ObservedAtUtc { get; init; }
    public DateTime? TerminalAtUtc { get; init; }
    public string? TerminalReason { get; init; }
    public string State => TerminalAtUtc is not null ? "terminal" : ObservedAtUtc is not null ? "observed" : "requested";
}

/// <summary>
/// Stop receipts are written before the HTTP acknowledgement. A request can only
/// be observed by the exact attempt and fence it names. Terminal receipts remain
/// queryable for idempotent command replay, but cannot be delivered again.
/// The configured root is resolved on first use, so a host without
/// <c>TaskRepository</c> still starts; it reads no receipts and refuses to record one.
/// </summary>
public sealed class RemoteRunStopRequestStore
{
    public const string RelativePath = ".metadata/remote-run-stop-requests.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(1);
    private readonly object _gate = new();
    private readonly Func<string?>? _resolveRoot;
    private readonly IAtomicJsonFileWriter _writer;
    private Dictionary<string, RemoteRunStopRequest> _requests = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;
    private string? _path;
    private bool _loaded;

    public RemoteRunStopRequestStore(IConfiguration configuration, IAtomicJsonFileWriter? writer = null)
        : this(configuration, () => DateTime.UtcNow, writer)
    {
    }

    internal RemoteRunStopRequestStore(IConfiguration configuration, Func<DateTime> utcNow,
        IAtomicJsonFileWriter? writer = null)
        : this(() => configuration["TaskRepository"], utcNow, writer)
    {
    }

    internal RemoteRunStopRequestStore(Func<DateTime> utcNow)
        : this((Func<string?>?)null, utcNow, null) { }

    internal RemoteRunStopRequestStore(string? root, Func<DateTime> utcNow, IAtomicJsonFileWriter? writer = null)
        : this(string.IsNullOrWhiteSpace(root) ? null : () => root, utcNow, writer)
    {
        lock (_gate) EnsureLoaded();
    }

    private RemoteRunStopRequestStore(Func<string?>? resolveRoot, Func<DateTime> utcNow, IAtomicJsonFileWriter? writer)
    {
        _resolveRoot = resolveRoot;
        _utcNow = utcNow;
        _writer = (writer ?? new AtomicJsonFileWriter()).WithLastGoodBackup();
    }

    /// <summary>True when receipts are meant to survive a restart, even if the root is still unresolved.</summary>
    private bool Durable => _resolveRoot is not null;

    public RemoteRunStopRequest Record(
        string taskKey,
        string reason,
        string? attemptId = null,
        string? requestedBy = null,
        long fencingToken = 0,
        string? commandId = null,
        string? runnerId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);
        var normalizedTask = taskKey.Trim();
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? RemoteRunStopReasons.User : reason.Trim();
        var normalizedAttempt = string.IsNullOrWhiteSpace(attemptId) ? null : attemptId.Trim();
        var normalizedBy = string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim();
        if (Durable && (normalizedAttempt is null || fencingToken <= 0))
            throw new ArgumentException("A current attempt and fence are required for a durable stop.");
        lock (_gate)
        {
            EnsureLoaded();
            if (Durable && _path is null)
                throw new InvalidOperationException("TaskRepository is required for durable remote stop receipts.");
            var id = string.IsNullOrWhiteSpace(commandId) ? null : commandId.Trim();
            if (id?.Length > 128) throw new ArgumentException("Stop command id is too long.", nameof(commandId));
            if (id is not null && _requests.TryGetValue(id, out var replay))
            {
                if (!SameCommand(replay, normalizedTask, normalizedReason, normalizedAttempt, fencingToken, normalizedBy))
                    throw new InvalidOperationException("Stop command id is bound to different input.");
                return replay;
            }
            var existing = ActiveFor(normalizedTask);
            // A delayed request for an older lease must not replace a command
            // already recorded for the successor generation.
            if (existing is not null && existing.FencingToken > fencingToken)
                throw new InvalidOperationException("Stop target was superseded by a newer lease.");
            if (existing is not null && existing.FencingToken == fencingToken
                && !string.Equals(existing.AttemptId, normalizedAttempt, StringComparison.Ordinal))
                throw new InvalidOperationException("Stop target does not match the current attempt.");
            if (id is null && existing is not null && SameCommand(existing, normalizedTask, normalizedReason, normalizedAttempt, fencingToken, normalizedBy))
                return existing;
            var now = _utcNow();
            var request = new RemoteRunStopRequest(normalizedTask, normalizedReason, now, normalizedAttempt, normalizedBy)
            {
                CommandId = id ?? Guid.NewGuid().ToString("N"),
                RunnerId = runnerId,
                FencingToken = fencingToken,
                ExpiresAtUtc = now.Add(RequestLifetime),
            };
            var next = new Dictionary<string, RemoteRunStopRequest>(_requests, StringComparer.Ordinal);
            if (existing is not null)
                next[existing.CommandId] = existing with { TerminalAtUtc = now, TerminalReason = "superseded" };
            next.Add(request.CommandId, request);
            Persist(next);
            _requests = next;
            return request;
        }
    }

    /// <summary>
    /// The pending request for this card, if any. Peeking does not consume it:
    /// the runner may miss a heartbeat, and the request stays valid until the
    /// attempt it belongs to actually hands back.
    /// </summary>
    public RemoteRunStopRequest? Peek(string? taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        lock (_gate)
        {
            EnsureLoaded();
            return ActiveFor(taskKey.Trim());
        }
    }

    public RemoteRunStopRequest? GetReceipt(string commandId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var receipt = _requests.GetValueOrDefault(commandId);
            if (receipt is not null) _ = ActiveFor(receipt.TaskKey);
            return _requests.GetValueOrDefault(commandId);
        }
    }

    /// <summary>Read every durable receipt for a card, including terminal history.</summary>
    public IReadOnlyList<RemoteRunStopRequest> ListForTask(string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return [];
        lock (_gate)
        {
            EnsureLoaded();
            return _requests.Values
                .Where(request => string.Equals(request.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(request => request.RequestedAtUtc)
                .ThenBy(request => request.CommandId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public RemoteRunStopRequest? Observe(string taskKey, string? attemptId, long fencingToken)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var request = ActiveFor(taskKey);
            if (request is null) return null;
            if (!string.Equals(request.AttemptId, attemptId, StringComparison.Ordinal)
                || request.FencingToken != fencingToken) return null;
            if (request.ObservedAtUtc is not null) return request;
            var observed = request with { ObservedAtUtc = _utcNow() };
            Replace(observed);
            return observed;
        }
    }

    public void RetireSuperseded(string taskKey, string? currentAttemptId, long currentFence)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var request = ActiveFor(taskKey);
            // An older acquire callback may run after a successor has already
            // recorded its stop. Only a strictly newer fence can retire it.
            if (request is null || request.FencingToken >= currentFence) return;
            Replace(request with { TerminalAtUtc = _utcNow(), TerminalReason = "superseded" });
        }
    }

    /// <summary>
    /// Close the gap between reading a held lease and persisting its stop.
    /// Acquisition retires commands already present, while this read-after-write
    /// retires a command recorded after a successor's acquisition callback.
    /// </summary>
    public RemoteRunStopRequest ReconcileWithLease(RemoteRunStopRequest request, RunLeaseResponse current)
    {
        if (!string.Equals(current.Outcome, "Held", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Lease?.AttemptId, request.AttemptId, StringComparison.Ordinal)
            || current.Lease?.FencingToken != request.FencingToken)
            Clear(request.TaskKey, request.AttemptId, request.FencingToken, "superseded");
        return GetReceipt(request.CommandId) ?? throw new InvalidOperationException("Stop receipt disappeared.");
    }

    /// <summary>Drop the request once its attempt released or completed.</summary>
    public RemoteRunStopRequest? Clear(string? taskKey, string? attemptId = null, long? fencingToken = null,
        string terminalReason = "settled")
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        lock (_gate)
        {
            EnsureLoaded();
            var request = ActiveFor(taskKey.Trim());
            if (request is null) return null;
            if (attemptId is not null && !string.Equals(request.AttemptId, attemptId, StringComparison.Ordinal)) return null;
            if (fencingToken is not null && request.FencingToken != fencingToken) return null;
            Replace(request with { TerminalAtUtc = _utcNow(), TerminalReason = terminalReason });
            return request;
        }
    }

    private RemoteRunStopRequest? ActiveFor(string taskKey)
    {
        var request = _requests.Values.LastOrDefault(request =>
            string.Equals(request.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            && request.TerminalAtUtc is null);
        if (request is null) return null;
        if (request.ExpiresAtUtc > _utcNow()) return request;
        Replace(request with { TerminalAtUtc = _utcNow(), TerminalReason = "expired" });
        return null;
    }

    private static bool SameCommand(RemoteRunStopRequest request, string taskKey, string reason,
        string? attemptId, long fence, string? by)
        => string.Equals(request.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
           && string.Equals(request.Reason, reason, StringComparison.Ordinal)
           && string.Equals(request.AttemptId, attemptId, StringComparison.Ordinal)
           && request.FencingToken == fence
           && string.Equals(request.RequestedBy, by, StringComparison.Ordinal);

    private void Replace(RemoteRunStopRequest request)
    {
        var next = new Dictionary<string, RemoteRunStopRequest>(_requests, StringComparer.Ordinal)
        {
            [request.CommandId] = request,
        };
        Persist(next);
        _requests = next;
    }

    private void Persist(Dictionary<string, RemoteRunStopRequest> requests)
    {
        if (_path is not null) _writer.Write(_path, JsonSerializer.Serialize(requests.Values.ToList(), Json));
    }

    // Caller holds _gate. A missing root leaves the store empty: nothing can
    // have been recorded without one, so every read is correctly a miss.
    private void EnsureLoaded()
    {
        if (_loaded) return;
        var root = _resolveRoot?.Invoke();
        var path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, RelativePath);
        if (path is not null && (File.Exists(path) || File.Exists(path + ".bak")))
        {
            var loaded = JsonSerializer.Deserialize<List<RemoteRunStopRequest>>(
                AtomicJsonFileWriter.ReadAllTextWithBackup(path), Json)
                ?? throw new InvalidDataException("Remote stop receipt file is empty.");
            _requests = loaded.ToDictionary(item => item.CommandId, StringComparer.Ordinal);
        }
        _path = path;
        _loaded = true;
    }
}
