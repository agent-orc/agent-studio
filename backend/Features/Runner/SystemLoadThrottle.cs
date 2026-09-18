using System.Runtime.InteropServices;

namespace AgentStudio.Runner;

public interface ILoadThrottleGate
{
    LoadThrottleDecision Current { get; }
    bool WasRecentlyActive { get; }
    Task WaitUntilReadyAsync(string reason, CancellationToken ct);
}

/// <summary>
/// Samples total host CPU every 15 seconds. It deliberately observes the whole
/// machine, including node/esbuild children, rather than only the backend process.
/// </summary>
public sealed class SystemLoadThrottle : BackgroundService, ILoadThrottleGate
{
    private readonly ILogger<SystemLoadThrottle> _logger;
    private readonly IConfiguration _configuration;
    private readonly object _sync = new();
    private readonly List<CpuLoadSample> _samples = new();
    private LoadThrottleDecision _current = new(false, 0, TimeSpan.Zero);
    private DateTime? _lastActiveUtc;
    private CpuTicks? _previous;

    public SystemLoadThrottle(ILogger<SystemLoadThrottle> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public LoadThrottleDecision Current { get { lock (_sync) return _current; } }
    public bool WasRecentlyActive { get { lock (_sync) return _lastActiveUtc is { } at && DateTime.UtcNow - at < TimeSpan.FromMinutes(5); } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _configuration.GetValue<int?>("Runner:LoadThrottle:SampleSeconds") ?? 15));
        while (!stoppingToken.IsCancellationRequested)
        {
            Sample();
            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task WaitUntilReadyAsync(string reason, CancellationToken ct)
    {
        var announced = false;
        while (Current.Throttle)
        {
            if (!announced)
            {
                announced = true;
                _logger.LogWarning("load_throttled_operation_queued reason=environmental-load operation={Operation} cpuPercent={CpuPercent:0.#}", reason, Current.CurrentPercent);
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        if (announced)
            _logger.LogInformation("load_throttled_operation_released reason=environmental-load operation={Operation}", reason);
    }

    private void Sample()
    {
        if (!SystemCpuReader.TryRead(out var ticks)) return;
        var prior = _previous;
        _previous = ticks;
        if (prior is null) return;
        var total = ticks.Total - prior.Value.Total;
        var idle = ticks.Idle - prior.Value.Idle;
        if (total <= 0) return;
        var percent = Math.Clamp(100d * (total - idle) / total, 0, 100);
        var now = DateTime.UtcNow;
        var threshold = _configuration.GetValue<double?>("Runner:LoadThrottle:CpuThresholdPercent") ?? 90;
        var sustained = TimeSpan.FromSeconds(_configuration.GetValue<int?>("Runner:LoadThrottle:SustainedSeconds") ?? 60);
        LoadThrottleDecision previous;
        lock (_sync)
        {
            previous = _current;
            _samples.Add(new(now, percent));
            _samples.RemoveAll(s => now - s.AtUtc > TimeSpan.FromMinutes(3));
            _current = LoadThrottlePolicy.Decide(_samples, now, threshold, sustained);
            if (_current.Throttle) _lastActiveUtc = now;
        }
        if (previous.Throttle != Current.Throttle)
            _logger.LogWarning("load_throttle_state_changed active={Active} cpuPercent={CpuPercent:0.#} sustainedSeconds={SustainedSeconds:0}", Current.Throttle, percent, Current.SustainedFor.TotalSeconds);
    }

    internal readonly record struct CpuTicks(ulong Idle, ulong Total);

    internal static class SystemCpuReader
    {
        public static bool TryRead(out CpuTicks ticks)
        {
            ticks = default;
            if (OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var idleValue = ToUInt64(idle);
                ticks = new(idleValue, ToUInt64(kernel) + ToUInt64(user));
                return true;
            }
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    var values = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
                    ticks = new(values[3] + (values.Length > 4 ? values[4] : 0), values.Aggregate(0UL, (sum, value) => sum + value));
                    return true;
                }
                catch { return false; }
            }
            return false;
        }

        private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low; public uint High; }
    }
}
