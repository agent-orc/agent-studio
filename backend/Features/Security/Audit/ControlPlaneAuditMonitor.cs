namespace AgentStudio.Security;

/// <summary>Where an acute audit alert goes besides the log: the operator feed in the host.</summary>
public interface IControlPlaneAuditAlertChannel
{
    Task RaiseAsync(ControlPlaneAuditAlert alert, CancellationToken ct);
}

public sealed class OperatorFeedAuditAlertChannel(AgentMessageBusBridge bus) : IControlPlaneAuditAlertChannel
{
    public Task RaiseAsync(ControlPlaneAuditAlert alert, CancellationToken ct)
        => bus.EmitControlPlaneAuditAlertAsync(alert.Code, alert.Message, alert.Sequence, ct);
}

public static class ControlPlaneAuditAlertCodes
{
    public const string ChainBroken = "audit-chain-broken";
    public const string SinkGap = "audit-sink-gap";
    public const string SinkMismatch = "audit-sink-mismatch";
    public const string SinkUnreachable = "audit-sink-unreachable";
}

/// <summary>
/// The live state of the audit trail for the activity view: the last chain
/// verification, forwarding progress, and the acute alerts raised. An alert is
/// raised once per condition and logged as an error; it clears when the
/// condition does (a later verification passes, the sink catches up).
/// </summary>
public sealed class ControlPlaneAuditMonitor(
    ControlPlaneAuditLog log,
    ControlPlaneAuditSinkOptions sink,
    TimeProvider? clock = null,
    IControlPlaneAuditAlertChannel? channel = null,
    ILogger<ControlPlaneAuditMonitor>? logger = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<ControlPlaneAuditAlert> _raised = [];
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private ControlPlaneAuditIntegrity _integrity = new(ControlPlaneAuditIntegrityStates.NotRun, null, 0, null, false);
    private DateTime? _oldestPendingAt;
    private string? _lastError;

    public ControlPlaneAuditSinkOptions Sink => sink;

    /// <summary>Every alert raised in this process, oldest first.</summary>
    public IReadOnlyList<ControlPlaneAuditAlert> Raised { get { lock (_gate) return _raised.ToList(); } }

    public ControlPlaneAuditIntegrity Integrity { get { lock (_gate) return _integrity; } }

    public ControlPlaneAuditForwarding Forwarding
    {
        get
        {
            var position = log.ReadForwardPosition();
            var head = log.HeadSequence;
            lock (_gate)
                return new ControlPlaneAuditForwarding(
                    sink.Kind, sink.Configured, head, position.Sequence,
                    sink.Configured ? Math.Max(0, head - position.Sequence) : 0,
                    position.ForwardedAt, _oldestPendingAt, _lastError, _open.Contains(ControlPlaneAuditAlertCodes.SinkUnreachable));
        }
    }

    public void RecordIntegrity(ControlPlaneAuditIntegrity integrity)
    {
        lock (_gate) _integrity = integrity;
    }

    public void RecordForwardFailure(string error, DateTime? oldestPendingAt)
    {
        lock (_gate)
        {
            _lastError = error;
            _oldestPendingAt = oldestPendingAt;
        }
    }

    public void RecordForwardProgress(DateTime? oldestPendingAt)
    {
        lock (_gate)
        {
            _lastError = null;
            _oldestPendingAt = oldestPendingAt;
        }
    }

    /// <summary>Raises <paramref name="code"/> unless it is already open; returns true when it was raised now.</summary>
    public async Task<bool> RaiseAsync(string code, string message, long? sequence, CancellationToken ct)
    {
        ControlPlaneAuditAlert alert;
        lock (_gate)
        {
            if (!_open.Add(code)) return false;
            alert = new ControlPlaneAuditAlert(code, message, _clock.GetUtcNow().UtcDateTime, sequence);
            _raised.Add(alert);
        }
        logger?.LogError("Control plane audit alert {Code}: {Message}", code, message);
        if (channel is not null)
        {
            try { await channel.RaiseAsync(alert, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogError(exception, "Control plane audit alert {Code} could not reach the operator feed", code);
            }
        }
        return true;
    }

    public void Clear(string code)
    {
        lock (_gate) _open.Remove(code);
    }

    public bool IsOpen(string code)
    {
        lock (_gate) return _open.Contains(code);
    }
}
