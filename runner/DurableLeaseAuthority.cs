using System.Text.Json;

namespace AgentRunner;

public sealed record DurableLeaseAuthoritySnapshot(
    DateTime LeaseExpiresAtUtc,
    DateTime StopBeforeUtc,
    string State,
    DateTime UpdatedAtUtc,
    string? Detail = null);

/// <summary>
/// Host-local truth for the bounded authority window granted by the Task
/// Server. A transport failure changes the replay state to uncertain but never
/// extends the stop-before deadline. Only a successful fenced renewal may
/// confirm replay authority and move the deadline.
/// </summary>
public sealed class DurableLeaseAuthority
{
    public const string FileName = "lease-authority.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeSpan _uncertaintyMargin;
    private readonly Func<DateTime> _utcNow;
    private TaskCompletionSource _confirmed =
        NewCompletionSource();
    private DurableLeaseAuthoritySnapshot _snapshot;

    private DurableLeaseAuthority(
        string path,
        TimeSpan uncertaintyMargin,
        Func<DateTime> utcNow,
        DurableLeaseAuthoritySnapshot snapshot,
        bool confirmed)
    {
        _path = path;
        _uncertaintyMargin = uncertaintyMargin;
        _utcNow = utcNow;
        _snapshot = snapshot;
        if (confirmed)
            _confirmed.TrySetResult();
    }

    public DateTime StopBeforeUtc
    {
        get
        {
            lock (_gate) return _snapshot.StopBeforeUtc;
        }
    }

    public bool ReplayAllowed
    {
        get
        {
            lock (_gate)
                return string.Equals(
                    _snapshot.State,
                    "confirmed",
                    StringComparison.Ordinal)
                    && _utcNow() < _snapshot.StopBeforeUtc;
        }
    }

    public DurableLeaseAuthoritySnapshot Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    public static DurableLeaseAuthority Open(
        string workerDirectory,
        DateTime leaseExpiresAt,
        TimeSpan uncertaintyMargin,
        bool initiallyConfirmed,
        Func<DateTime>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerDirectory);
        Directory.CreateDirectory(workerDirectory);
        var path = Path.Combine(workerDirectory, FileName);
        var now = utcNow ?? (() => DateTime.UtcNow);
        DurableLeaseAuthoritySnapshot snapshot;
        if (File.Exists(path))
        {
            var persisted = JsonSerializer.Deserialize<DurableLeaseAuthoritySnapshot>(
                           File.ReadAllText(path),
                           Json)
                       ?? throw new InvalidDataException(
                           $"Durable lease authority is empty: {path}");
            if (initiallyConfirmed)
            {
                var expires = leaseExpiresAt.ToUniversalTime();
                snapshot = new DurableLeaseAuthoritySnapshot(
                    expires,
                    ComputeStopBefore(expires, uncertaintyMargin),
                    "confirmed",
                    now(),
                    "authority restored from the live claim");
            }
            else
            {
                snapshot = persisted with
                {
                    State = "uncertain",
                    UpdatedAtUtc = now(),
                    Detail = "runner restart requires fenced reconciliation before replay",
                };
            }
        }
        else
        {
            var expires = leaseExpiresAt.ToUniversalTime();
            snapshot = new DurableLeaseAuthoritySnapshot(
                expires,
                ComputeStopBefore(expires, uncertaintyMargin),
                initiallyConfirmed ? "confirmed" : "uncertain",
                now(),
                initiallyConfirmed
                    ? "authority issued by the live claim"
                    : "persisted attempt requires fenced reconciliation before replay");
        }

        WriteAtomic(path, snapshot);
        return new DurableLeaseAuthority(
            path,
            uncertaintyMargin,
            now,
            snapshot,
            initiallyConfirmed);
    }

    public static DurableLeaseAuthoritySnapshot? Read(string workerDirectory)
    {
        var path = Path.Combine(workerDirectory, FileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<DurableLeaseAuthoritySnapshot>(
                   File.ReadAllText(path),
                   Json)
               ?? throw new InvalidDataException(
                   $"Durable lease authority is empty: {path}");
    }

    public void MarkUncertain(string detail)
    {
        lock (_gate)
        {
            if (!string.Equals(
                    _snapshot.State,
                    "uncertain",
                    StringComparison.Ordinal))
            {
                _confirmed = NewCompletionSource();
            }
            _snapshot = _snapshot with
            {
                State = "uncertain",
                UpdatedAtUtc = _utcNow(),
                Detail = detail,
            };
            WriteAtomic(_path, _snapshot);
        }
    }

    public void Confirm(DateTime leaseExpiresAt, string detail)
    {
        lock (_gate)
        {
            var expires = leaseExpiresAt.ToUniversalTime();
            _snapshot = new DurableLeaseAuthoritySnapshot(
                expires,
                ComputeStopBefore(expires, _uncertaintyMargin),
                "confirmed",
                _utcNow(),
                detail);
            WriteAtomic(_path, _snapshot);
            _confirmed.TrySetResult();
        }
    }

    public void Reject(string detail)
    {
        lock (_gate)
        {
            if (!string.Equals(
                    _snapshot.State,
                    "rejected",
                    StringComparison.Ordinal))
            {
                _confirmed = NewCompletionSource();
            }
            _snapshot = _snapshot with
            {
                State = "rejected",
                UpdatedAtUtc = _utcNow(),
                Detail = detail,
            };
            WriteAtomic(_path, _snapshot);
        }
    }

    public async Task WaitForConfirmedAsync(CancellationToken ct)
    {
        Task wait;
        lock (_gate)
        {
            if (_utcNow() >= _snapshot.StopBeforeUtc)
                throw new InvalidOperationException("Lease authority stop-before deadline has passed.");
            if (string.Equals(
                    _snapshot.State,
                    "confirmed",
                    StringComparison.Ordinal))
                return;
            wait = _confirmed.Task;
        }
        await wait.WaitAsync(ct);
    }

    public static DateTime ComputeStopBefore(
        DateTime leaseExpiresAt,
        TimeSpan uncertaintyMargin)
        => leaseExpiresAt.ToUniversalTime() - uncertaintyMargin;

    private static TaskCompletionSource NewCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void WriteAtomic(
        string path,
        DurableLeaseAuthoritySnapshot snapshot)
    {
        var temporary =
            path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonSerializer.Serialize(snapshot, Json));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
