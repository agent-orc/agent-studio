using AgentStudio.Clients;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3792: probes the primary gate host (<c>RemoteGate:SshHost</c>) with one
/// bounded <c>ssh host true</c>, the same connection the gate transport opens.
/// It sends no bundle and starts no worker, so an outage costs one short SSH
/// connection per backoff step instead of a batch formed and abandoned.
/// </summary>
public sealed class SshGateHostProbe : IGateHostProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly IConfiguration _configuration;
    private readonly Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> _runProcess;

    private readonly HostTelemetryStore? _telemetry;
    private bool _diskCritical;
    public GateHostDiskReading? LatestDisk { get; private set; }

    public SshGateHostProbe(IConfiguration configuration, HostTelemetryStore? telemetry = null)
        : this(configuration, RemoteGateProcess.RunAsync)
    {
        _telemetry = telemetry;
    }

    internal SshGateHostProbe(IConfiguration configuration,
        Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> runProcess)
    {
        _configuration = configuration;
        _runProcess = runProcess;
    }

    public string? Host
    {
        get
        {
            try { return RemoteGateOptions.Read(_configuration).Host; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public async Task<GateHostDiskReading?> ReadDiskAsync(CancellationToken ct)
    {
        if (Host is not { } host) return null;
        var root = RemoteGateOptions.Read(_configuration).Root;
        var output = new List<string>();
        await _runProcess("ssh", RemoteGateTransport.SshArguments(host,
            "p=" + RemoteGateTransport.Quote(root)
                + "; while [ ! -d \"$p\" ]; do p=$(dirname -- \"$p\"); done; stat -f -c '%a %b %S' -- \"$p\""), Path.GetTempPath(),
            ProbeTimeout, ct, line => { lock (output) output.Add(line); }).ConfigureAwait(false);
        var filesystem = ParseDisk(root, output.LastOrDefault() ?? "");
        var pressure = _telemetry?.DiskPressure("gate-ssh:" + host, [filesystem])
            ?? HostDiskPressurePolicy.Decide([filesystem], new(), _diskCritical);
        _diskCritical = pressure.Level == HostDiskPressureLevel.Critical;
        var now = DateTime.UtcNow;
        LatestDisk = new("gate-ssh:" + host, host, now, [filesystem],
            pressure.Level.ToString().ToLowerInvariant(), pressure.Reason);
        _telemetry?.Append("gate-ssh:" + host, new HostTelemetrySample(now,
            null, null, null, null, null, null, null, null, null, null, 0, 0, Filesystems: [filesystem]));
        return LatestDisk;
    }

    internal static HostFilesystemUsageDto ParseDisk(string root, string output)
    {
        var values = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 3 || !long.TryParse(values[0], out var available)
            || !long.TryParse(values[1], out var total) || !long.TryParse(values[2], out var blockSize)
            || available < 0 || total <= 0 || blockSize <= 0)
            throw new IOException("Gate host returned an invalid disk-space reading.");
        return new("gate work", root, checked(available * blockSize), checked(total * blockSize));
    }

    public async Task WaitForDiskRecoveryAsync(Action<string> log, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        while (await ReadDiskAsync(ct).ConfigureAwait(false) is { Level: "critical" } disk)
        {
            log($"Gate admission paused: {disk.Reason}");
            await delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }

    internal async Task<int> CountBridgeWorkersAsync(CancellationToken ct)
    {
        var options = RemoteGateOptions.Read(_configuration);
        var count = new List<string>();
        // A worker keeps its exclusive lock through its wait and execution.
        // Use read-only handles so observation never creates a gate resource.
        var command = "set -eu; n=0; for lock in " + RemoteGateTransport.Quote(options.Root)
            + "/*/worker.lock; do [ -f \"$lock\" ] || continue; "
            + "if flock -n -s -E 81 9 9<\"$lock\"; then :; "
            + "else status=$?; [ \"$status\" -eq 81 ] || exit \"$status\"; n=$((n+1)); fi; done; printf '%s\\n' \"$n\"";
        await _runProcess("ssh", RemoteGateTransport.SshArguments(options.Host, command),
            Path.GetTempPath(), ProbeTimeout, ct, line => { lock (count) count.Add(line); });
        return count.Count == 1 && int.TryParse(count[0], out var value) && value >= 0
            ? value : throw new IOException("Gate host returned invalid bridge worker demand.");
    }

    public async Task<GateHostProbeResult> ProbeAsync(CancellationToken ct)
    {
        if (Host is not { } host) return new GateHostProbeResult(false, "No gate host is configured.");
        try
        {
            await _runProcess("ssh", RemoteGateTransport.SshArguments(host, "true"), Path.GetTempPath(),
                ProbeTimeout, ct, null).ConfigureAwait(false);
            return new GateHostProbeResult(true);
        }
        catch (Exception error) when (error is IOException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            return new GateHostProbeResult(false, error.Message);
        }
    }
}

public sealed record GateHostDiskReading(string Id, string Host, DateTime ObservedAt,
    IReadOnlyList<HostFilesystemUsageDto> Filesystems, string Level, string Reason);
