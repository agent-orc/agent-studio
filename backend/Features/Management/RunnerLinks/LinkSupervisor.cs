using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Management;

public static class RunnerLinkStates
{
    public const string Down = "down";
    public const string Connecting = "connecting";
    public const string Up = "up";
    public const string Degraded = "degraded";
    public const string Reconnecting = "reconnecting";
    public const string Paused = "paused";
}

public sealed record RunnerLinkOptions(
    string RunnerId,
    string Kind,
    string SshTarget,
    int RemotePort,
    int LocalPort,
    IReadOnlyList<string> ExtraForwards,
    int HeartbeatTimeoutSeconds,
    IReadOnlyList<int> BackoffSeconds,
    string SshExecutable,
    string LogDirectory)
{
    public static IReadOnlyList<RunnerLinkOptions> Read(IConfiguration configuration)
    {
        var links = new List<RunnerLinkOptions>();
        foreach (var section in configuration.GetSection("RunnerLinks").GetChildren())
        {
            if (section.GetValue("Enabled", true) is false) continue;
            var runnerId = section["RunnerId"]?.Trim() ?? "";
            var kind = section["Kind"]?.Trim().ToLowerInvariant() ?? "";
            var target = section["SshTarget"]?.Trim() ?? "";
            var remotePort = section.GetValue<int>("RemotePort");
            var localPort = section.GetValue<int>("LocalPort");
            var timeout = Math.Clamp(section.GetValue("HeartbeatTimeoutSeconds", 90), 30, 900);
            var forwards = section.GetSection("ExtraForwards").Get<string[]>() ?? [];
            var backoff = section.GetSection("BackoffSeconds").Get<int[]>() ?? [5, 10, 30, 60, 120];
            if (runnerId.Length == 0 || kind != "ssh-reverse" || target.Length == 0
                || remotePort is < 1 or > 65535 || localPort is < 1 or > 65535
                || backoff.Length == 0 || backoff.Any(value => value is < 1 or > 3600)
                || forwards.Any(value => !RunnerLinkPolicy.IsForward(value)))
                throw new InvalidOperationException($"RunnerLinks:{section.Key} is invalid.");
            links.Add(new RunnerLinkOptions(
                runnerId, kind, target, remotePort, localPort, forwards, timeout, backoff,
                section["SshExecutable"]?.Trim() is { Length: > 0 } executable ? executable : "ssh",
                Path.GetFullPath(section["LogDirectory"] ?? Path.Combine(
                    configuration["TaskRepository"] ?? AppContext.BaseDirectory, ".logs", "runner-links"))));
        }
        if (links.Select(link => link.RunnerId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != links.Count)
            throw new InvalidOperationException("RunnerLinks runnerId values must be unique.");
        return links;
    }
}

public sealed record RunnerLinkProbe(
    DateTime At,
    string Kind,
    bool Succeeded,
    int? ExitCode,
    string Detail);

public sealed record RunnerLinkResource(
    string RunnerId,
    string Kind,
    string State,
    DateTime Since,
    DateTime? LastHeartbeatAt,
    RunnerLinkProbe? LastProbe,
    string? LastError,
    int Attempt,
    DateTime? NextRetryAt,
    int? ChildPid,
    DateTime? NotificationRaisedAt,
    DateTime? UnreachableSince,
    string? BlockedBy,
    int? RemoteListenerPid,
    int? RemoteListenerAgeSeconds,
    string? Transport);

public sealed record RunnerLinkTransition(
    string State,
    bool Probe,
    bool Recover,
    bool Start,
    bool ResetBackoff);

/// <summary>Pure lifecycle policy. The hosted service owns only observations and bounded effects.</summary>
public static class RunnerLinkPolicy
{
    public static string? Transport(int exitCode, string stderr)
        => stderr.Contains("No route to host", StringComparison.OrdinalIgnoreCase) ? "no-route" : null;

    public static string ProbeDetail(int exitCode, string kind, string command, string stdout, string stderr)
    {
        var transport = Transport(exitCode, stderr);
        if (transport is not null) return $"transport: {transport}; {stderr.Trim()}";
        if (kind is "listener-cleanup" or "adopted-listener-release")
            return exitCode switch
            {
                0 => "Remote listener cleanup succeeded.",
                1 => $"Remote listener remains held. {stdout.Trim()} {stderr.Trim()}".Trim(),
                124 => $"Cleanup command timed out: {command}",
                255 => $"Cleanup SSH transport failed (exit 255): {stderr.Trim()}",
                _ => $"Cleanup command exited with code {exitCode}: {stderr.Trim()}",
            };
        return exitCode == 0 ? "Route probe succeeded."
            : $"ssh route probe exited with code {exitCode}: {stderr.Trim()}";
    }

    public static RunnerLinkTransition Decide(
        string state,
        bool paused,
        bool childRunning,
        bool heartbeatFresh,
        bool heartbeatLate,
        bool retryDue)
    {
        if (paused) return new(RunnerLinkStates.Paused, false, false, false, false);
        if (!childRunning && state is RunnerLinkStates.Connecting or RunnerLinkStates.Up)
            return new(RunnerLinkStates.Reconnecting, false, true, false, false);
        if (heartbeatFresh) return new(RunnerLinkStates.Up, false, false, false, true);
        if (state is RunnerLinkStates.Up or RunnerLinkStates.Connecting && heartbeatLate)
            return new(RunnerLinkStates.Degraded, true, false, false, false);
        if (state == RunnerLinkStates.Degraded)
            return new(RunnerLinkStates.Degraded, false, false, false, false);
        if (state == RunnerLinkStates.Reconnecting && retryDue)
            return new(RunnerLinkStates.Connecting, false, false, true, false);
        if (state == RunnerLinkStates.Down && retryDue)
            return new(RunnerLinkStates.Connecting, false, false, true, false);
        return new(state, false, false, false, false);
    }

    public static bool IsForward(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 3
               && int.TryParse(parts[0], out var remote) && remote is >= 1 and <= 65535
               && parts[1].Length > 0 && parts[1].All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-')
               && int.TryParse(parts[2], out var local) && local is >= 1 and <= 65535;
    }
}

public sealed record SshCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string LogPath,
    TimeSpan? Timeout = null);

public interface IRunnerLinkProcess : IAsyncDisposable
{
    int Pid { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    string StandardOutput { get; }
    string StandardError { get; }
    Task<int> WaitAsync(CancellationToken cancellationToken);
    Task TerminateAsync(CancellationToken cancellationToken);
}

public interface IRunnerLinkProcessLauncher : IDisposable
{
    IRunnerLinkProcess Start(SshCommand command);
}

/// <summary>Starts silent, redirected children and binds Windows children to a kill-on-close job.</summary>
public sealed class RunnerLinkProcessLauncher : IRunnerLinkProcessLauncher
{
    private readonly WindowsKillJob? _job = OperatingSystem.IsWindows() ? new WindowsKillJob() : null;

    public IRunnerLinkProcess Start(SshCommand command)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(command.LogPath)!);
        var info = BuildStartInfo(command);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ssh.");
        try { _job?.Add(process); }
        catch { process.Kill(true); process.Dispose(); throw; }
        return new RealRunnerLinkProcess(process, command.LogPath, command.Timeout);
    }

    internal static ProcessStartInfo BuildStartInfo(SshCommand command)
    {
        var info = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public void Dispose() => _job?.Dispose();

    private sealed class WindowsKillJob : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public WindowsKillJob()
        {
            _handle = Native.CreateJobObject(IntPtr.Zero, null);
            if (_handle.IsInvalid) throw new InvalidOperationException("Could not create the runner-link job object.");
            var limits = new Native.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new Native.JobObjectBasicLimitInformation
                {
                    LimitFlags = Native.JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<Native.JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, pointer, false);
                if (!Native.SetInformationJobObject(_handle, 9, pointer, (uint)size))
                    throw new InvalidOperationException("Could not configure the runner-link job object.");
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        public void Add(Process process)
        {
            if (!Native.AssignProcessToJobObject(_handle, process.Handle))
                throw new InvalidOperationException("Could not bind ssh to the Task Server job object.");
        }

        public void Dispose() => _handle.Dispose();
    }

    private static class Native
    {
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public long Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }
}

internal sealed class RealRunnerLinkProcess : IRunnerLinkProcess
{
    private const long MaximumLogBytes = 1024 * 1024;
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly TimeSpan? _timeout;
    private readonly System.Text.StringBuilder _standardOutput = new();
    private readonly System.Text.StringBuilder _standardError = new();

    public RealRunnerLinkProcess(Process process, string logPath, TimeSpan? timeout)
    {
        _process = process;
        _timeout = timeout;
        _stdout = PumpAsync(process.StandardOutput, logPath, "stdout", _standardOutput, _logGate, _lifetime.Token);
        _stderr = PumpAsync(process.StandardError, logPath, "stderr", _standardError, _logGate, _lifetime.Token);
    }

    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public string StandardOutput => _standardOutput.ToString();
    public string StandardError => _standardError.ToString();

    public async Task<int> WaitAsync(CancellationToken cancellationToken)
    {
        using var timeout = _timeout is { } duration ? new CancellationTokenSource(duration) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout?.Token ?? CancellationToken.None);
        try { await _process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
        {
            await TerminateAsync(CancellationToken.None);
            await Task.WhenAll(_stdout, _stderr);
            return 124;
        }
        await Task.WhenAll(_stdout, _stderr);
        return _process.ExitCode;
    }

    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        if (!_process.HasExited)
        {
            _process.Kill(true);
            await _process.WaitForExitAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await TerminateAsync(CancellationToken.None);
        _lifetime.Cancel();
        try { await Task.WhenAll(_stdout, _stderr); }
        catch (OperationCanceledException ex) { SilentCatch.Note(ex, "Runner-link output pumps cancelled during disposal"); }
        _process.Dispose();
        _lifetime.Dispose();
        _logGate.Dispose();
    }

    private static async Task PumpAsync(
        StreamReader reader,
        string path,
        string stream,
        System.Text.StringBuilder capture,
        SemaphoreSlim logGate,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await logGate.WaitAsync(cancellationToken);
            try
            {
                if (capture.Length < 8192) capture.AppendLine(line);
                Rotate(path);
                await File.AppendAllTextAsync(
                    path,
                    $"{DateTime.UtcNow:O} {stream} {line}{Environment.NewLine}",
                    cancellationToken);
            }
            finally
            {
                logGate.Release();
            }
        }
    }

    private static void Rotate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumLogBytes) return;
        var prior = path + ".1";
        if (File.Exists(prior)) File.Delete(prior);
        File.Move(path, prior);
    }
}

public interface IRunnerLinkAudit
{
    Task ActionAsync(string runnerId, string action, string actor, CancellationToken cancellationToken);
}

public sealed class RunnerLinkAudit(IConfiguration configuration) : IRunnerLinkAudit
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task ActionAsync(string runnerId, string action, string actor, CancellationToken cancellationToken)
    {
        var root = configuration["TaskRepository"] ?? Path.Combine(AppContext.BaseDirectory, "workspace");
        var path = Path.Combine(Path.GetFullPath(root), ".audit", "runner-links.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp = DateTime.UtcNow, runnerId, action, actor,
        }) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken);
        try { await File.AppendAllTextAsync(path, line, cancellationToken); }
        finally { _gate.Release(); }
    }
}

public sealed class LinkSupervisor : BackgroundService
{
    private readonly IReadOnlyDictionary<string, LinkState> _links;
    private readonly IRunnerLinkProcessLauncher _processes;
    private readonly V1ReviewExecutorRegistry _heartbeats;
    private readonly AgentMessageBusBridge _feed;
    private readonly TaskScannerService _tasks;
    private readonly ProjectSettingsService _projectSettings;
    private readonly TimeProvider _time;

    public LinkSupervisor(
        IConfiguration configuration,
        IRunnerLinkProcessLauncher processes,
        V1ReviewExecutorRegistry heartbeats,
        AgentMessageBusBridge feed,
        TaskScannerService tasks,
        ProjectSettingsService projectSettings,
        TimeProvider? time = null)
    {
        _processes = processes;
        _heartbeats = heartbeats;
        _feed = feed;
        _tasks = tasks;
        _projectSettings = projectSettings;
        _time = time ?? TimeProvider.System;
        _links = RunnerLinkOptions.Read(configuration).ToDictionary(
            item => item.RunnerId, item => new LinkState(item, UtcNow()), StringComparer.OrdinalIgnoreCase);
        _heartbeats.CapabilitySnapshotAdvertised += OnHeartbeat;
    }

    public IReadOnlyList<RunnerLinkResource> Snapshot() => _links.Values
        .Select(link => link.Resource()).OrderBy(link => link.RunnerId, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<RunnerLinkResource?> ReconnectAsync(string runnerId, string actor, CancellationToken cancellationToken)
    {
        if (!_links.TryGetValue(runnerId, out var link)) return null;
        await link.Gate.WaitAsync(cancellationToken);
        try
        {
            link.Paused = false;
            await StopChildAsync(link, cancellationToken);
            await TransitionAsync(link, RunnerLinkStates.Reconnecting, "Operator requested reconnect.", cancellationToken);
            link.NextRetryAt = UtcNow();
            link.NeedsCleanup = true;
            await RecoverAsync(link, cancellationToken);
        }
        finally { link.Gate.Release(); }
        return link.Resource();
    }

    public async Task<RunnerLinkResource?> PauseAsync(string runnerId, CancellationToken cancellationToken)
    {
        if (!_links.TryGetValue(runnerId, out var link)) return null;
        await link.Gate.WaitAsync(cancellationToken);
        try
        {
            link.Paused = true;
            await StopChildAsync(link, cancellationToken);
            await ReleaseAdoptedForwardAsync(link, cancellationToken);
            await TransitionAsync(link, RunnerLinkStates.Paused, null, cancellationToken);
            link.NextRetryAt = null;
        }
        finally { link.Gate.Release(); }
        return link.Resource();
    }

    public Task<RunnerLinkResource?> ResumeAsync(string runnerId, CancellationToken cancellationToken)
        => ReconnectAsync(runnerId, "resume", cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var link in _links.Values)
        {
            await link.Gate.WaitAsync(stoppingToken);
            try
            {
                var adopted = await RouteProbeAsync(link, "adoption", stoppingToken);
                if (adopted)
                {
                    link.Adopted = true;
                    await TransitionAsync(link, RunnerLinkStates.Connecting, null, stoppingToken);
                }
                else
                {
                    link.NextRetryAt = UtcNow();
                    await TransitionAsync(link, RunnerLinkStates.Down, link.LastProbe?.Detail, stoppingToken);
                }
            }
            finally { link.Gate.Release(); }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            foreach (var link in _links.Values) await TickAsync(link, stoppingToken);
        }
    }

    internal async Task TickAsync(string runnerId, CancellationToken cancellationToken = default)
    {
        if (_links.TryGetValue(runnerId, out var link)) await TickAsync(link, cancellationToken);
    }

    private async Task TickAsync(LinkState link, CancellationToken cancellationToken)
    {
        if (!await link.Gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var now = UtcNow();
            var heartbeatFresh = link.LastHeartbeatAt is { } heartbeat
                                 && heartbeat >= link.HeartbeatRequiredAfter
                                 && now - heartbeat <= TimeSpan.FromSeconds(link.Options.HeartbeatTimeoutSeconds);
            var heartbeatLate = link.LastHeartbeatAt is null
                ? now - link.Since > TimeSpan.FromSeconds(link.Options.HeartbeatTimeoutSeconds)
                : now - link.LastHeartbeatAt > TimeSpan.FromSeconds(link.Options.HeartbeatTimeoutSeconds);
            var childRunning = link.Child is { HasExited: false } || link.Adopted;
            if (link.State == RunnerLinkStates.Reconnecting && link.NeedsCleanup
                && (link.NextRetryAt is null || link.NextRetryAt <= now))
            {
                await RecoverAsync(link, cancellationToken);
                await MaybeNotifyAsync(link, now, cancellationToken);
                return;
            }
            var transition = RunnerLinkPolicy.Decide(
                link.State, link.Paused, childRunning, heartbeatFresh, heartbeatLate,
                link.NextRetryAt is null || link.NextRetryAt <= now);
            if (transition.ResetBackoff)
            {
                link.Attempt = 0;
                link.NextRetryAt = null;
                await TransitionAsync(link, RunnerLinkStates.Up, null, cancellationToken);
                return;
            }
            if (transition.State != link.State)
                await TransitionAsync(link, transition.State, null, cancellationToken);
            if (transition.Probe)
            {
                var routeAlive = await RouteProbeAsync(link, "late-heartbeat", cancellationToken);
                if (!routeAlive)
                {
                    await TransitionAsync(link, RunnerLinkStates.Down, link.LastProbe?.Detail, cancellationToken);
                    await RecoverAsync(link, cancellationToken);
                }
            }
            if (transition.Recover) await RecoverAsync(link, cancellationToken);
            if (transition.Start) await StartForwardAsync(link, cancellationToken);
            await MaybeNotifyAsync(link, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleFailureAsync(link, ex.Message, cancellationToken);
        }
        finally { link.Gate.Release(); }
    }

    private async Task RecoverAsync(LinkState link, CancellationToken cancellationToken)
    {
        if (link.Child is { HasExited: true } exited)
        {
            await exited.WaitAsync(cancellationToken);
            link.Transport = RunnerLinkPolicy.Transport(exited.ExitCode ?? 255, exited.StandardError);
            if (link.Transport is not null)
                link.LastError = $"transport: {link.Transport}; {exited.StandardError.Trim()}";
        }
        await TransitionAsync(link, RunnerLinkStates.Reconnecting, link.LastError, cancellationToken);
        link.HeartbeatRequiredAfter = UtcNow();
        link.Adopted = false;
        await StopChildAsync(link, cancellationToken);
        var cleanup = CleanupCommand(link.Options);
        var result = await RunBoundedAsync(link, cleanup, "listener-cleanup", TimeSpan.FromSeconds(10), cancellationToken);
        if (!result.Succeeded)
        {
            if (result.ExitCode == 1 && result.Detail.Contains("LISTENER", StringComparison.Ordinal))
            {
                link.BlockedBy = "remote-listener-held";
                if (!link.ListenerAlarmRaised)
                {
                    link.ListenerAlarmRaised = true;
                    await _feed.EmitRunnerLinkTransitionAsync(
                        "link_remote_listener_held", link.Options.RunnerId, result.Detail, link.Attempt, cancellationToken);
                }
            }
            await ScheduleFailureAsync(link, $"Remote listener cleanup failed: {result.Detail}", cancellationToken);
            return;
        }
        link.BlockedBy = null;
        link.RemoteListenerPid = null;
        link.RemoteListenerAgeSeconds = null;
        link.ListenerAlarmRaised = false;
        link.NeedsCleanup = false;
        link.NextRetryAt = UtcNow();
    }

    private async Task StartForwardAsync(LinkState link, CancellationToken cancellationToken)
    {
        link.Attempt++;
        var arguments = new List<string>
        {
            "-N", "-T", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3", "-o", "LogLevel=VERBOSE",
            "-R", $"{link.Options.RemotePort}:127.0.0.1:{link.Options.LocalPort}",
        };
        foreach (var forward in link.Options.ExtraForwards) { arguments.Add("-R"); arguments.Add(forward); }
        arguments.Add(link.Options.SshTarget);
        var log = Path.Combine(link.Options.LogDirectory, SafeName(link.Options.RunnerId) + ".log");
        link.Child = _processes.Start(new SshCommand(link.Options.SshExecutable, arguments, log));
        link.NextRetryAt = null;
        await TransitionAsync(link, RunnerLinkStates.Connecting, null, cancellationToken);
        await Task.Yield();
        if (link.Child.HasExited)
        {
            await link.Child.WaitAsync(cancellationToken);
            link.Transport = RunnerLinkPolicy.Transport(link.Child.ExitCode ?? 255, link.Child.StandardError);
            await ScheduleFailureAsync(link, $"ssh exited before a heartbeat (exit {link.Child.ExitCode}). {link.Child.StandardError.Trim()}".Trim(), cancellationToken);
        }
    }

    private async Task<bool> RouteProbeAsync(LinkState link, string kind, CancellationToken cancellationToken)
    {
        var remote = $"curl --fail --silent --show-error --max-time 5 http://127.0.0.1:{link.Options.RemotePort}/healthz >/dev/null";
        var result = await RunBoundedAsync(link,
            ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", link.Options.SshTarget, remote],
            kind, TimeSpan.FromSeconds(8), cancellationToken);
        return result.Succeeded;
    }

    public static IReadOnlyList<string> CleanupCommand(RunnerLinkOptions options)
    {
        var script = $$"""
            port={{options.RemotePort}}
            endpoint=127.0.0.1:$port
            listener() { ss -H -ltnp "sport = :$port" | awk -v endpoint="$endpoint" '$4 == endpoint { print }'; }
            rows=$(listener) || exit 2
            [ -n "$rows" ] || exit 0
            pids=$(printf '%s\n' "$rows" | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u)
            [ -n "$pids" ] || { echo 'LISTENER pid=unknown ageSeconds=unknown'; exit 1; }
            self_uid=$(id -u)
            for pid in $pids; do
              owner_uid=$(ps -o uid= -p "$pid" | tr -d ' ')
              command=$(ps -o comm= -p "$pid" | tr -d ' ')
              age=$(ps -o etimes= -p "$pid" | tr -d ' ')
              echo "LISTENER pid=$pid ageSeconds=${age:-unknown} owner=$command uid=$owner_uid"
              if [ "$owner_uid" = "$self_uid" ] && [ "$command" = sshd ]; then
                kill "$pid" 2>&1 || true
                sleep 1
                kill -0 "$pid" 2>/dev/null && kill -KILL "$pid" 2>&1 || true
              fi
            done
            [ -z "$(listener)" ] || exit 1
            """;
        var command = "timeout 4s sh -c " + ShellQuote(script);
        return ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", options.SshTarget, command];
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private async Task<RunnerLinkProbe> RunBoundedAsync(
        LinkState link, IReadOnlyList<string> arguments, string kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var log = Path.Combine(link.Options.LogDirectory, SafeName(link.Options.RunnerId) + ".log");
        await using var process = _processes.Start(new SshCommand(link.Options.SshExecutable, arguments, log, timeout));
        var exit = await process.WaitAsync(cancellationToken);
        var command = arguments[^1];
        var probe = new RunnerLinkProbe(UtcNow(), kind, exit == 0, exit,
            RunnerLinkPolicy.ProbeDetail(exit, kind, command, process.StandardOutput, process.StandardError));
        link.Transport = RunnerLinkPolicy.Transport(exit, process.StandardError) ?? link.Transport;
        if (kind is "listener-cleanup" or "adopted-listener-release")
        {
            var match = System.Text.RegularExpressions.Regex.Match(process.StandardOutput,
                @"LISTENER pid=(\d+) ageSeconds=(\d+)");
            link.RemoteListenerPid = match.Success ? int.Parse(match.Groups[1].Value) : null;
            link.RemoteListenerAgeSeconds = match.Success ? int.Parse(match.Groups[2].Value) : null;
        }
        link.LastProbe = probe;
        if (!probe.Succeeded) link.LastError = probe.Detail;
        return probe;
    }

    private async Task ScheduleFailureAsync(LinkState link, string error, CancellationToken cancellationToken)
    {
        link.LastError = error;
        await TransitionAsync(link, RunnerLinkStates.Reconnecting, error, cancellationToken);
        var index = link.BlockedBy is not null ? link.Options.BackoffSeconds.Count - 1
            : Math.Clamp(link.Attempt, 0, link.Options.BackoffSeconds.Count - 1);
        link.NextRetryAt = UtcNow().AddSeconds(link.Options.BackoffSeconds[index]);
        if (link.BlockedBy is null)
            await _feed.EmitRunnerLinkTransitionAsync("link_reconnect_failed", link.Options.RunnerId, error, link.Attempt, cancellationToken);
    }

    private async Task StopChildAsync(LinkState link, CancellationToken cancellationToken)
    {
        if (link.Child is null) return;
        await link.Child.TerminateAsync(cancellationToken);
        await link.Child.DisposeAsync();
        link.Child = null;
    }

    private async Task ReleaseAdoptedForwardAsync(LinkState link, CancellationToken cancellationToken)
    {
        if (!link.Adopted) return;
        var result = await RunBoundedAsync(
            link, CleanupCommand(link.Options), "adopted-listener-release", TimeSpan.FromSeconds(10), cancellationToken);
        link.Adopted = false;
        if (!result.Succeeded) link.LastError = $"Adopted listener release failed: {result.Detail}";
    }

    private void OnHeartbeat(string runnerId, DateTime at)
    {
        if (!_links.TryGetValue(runnerId, out var link)) return;
        link.LastHeartbeatAt = at.ToUniversalTime();
    }

    private async Task TransitionAsync(
        LinkState link,
        string next,
        string? error,
        CancellationToken cancellationToken)
    {
        if (link.State == next)
        {
            if (!string.IsNullOrWhiteSpace(error)) link.LastError = error;
            return;
        }
        link.State = next;
        link.Since = UtcNow();
        if (!string.IsNullOrWhiteSpace(error)) link.LastError = error;
        if (next == RunnerLinkStates.Up)
        {
            link.LastError = null;
            link.BlockedBy = null;
            link.RemoteListenerPid = null;
            link.RemoteListenerAgeSeconds = null;
            link.ListenerAlarmRaised = false;
            link.Transport = null;
            link.UnreachableSince = null;
            link.NotificationRaisedAt = null;
            await _feed.EmitRunnerLinkTransitionAsync(
                "link_up", link.Options.RunnerId, null, link.Attempt, cancellationToken);
        }
        else
        {
            if (next != RunnerLinkStates.Paused) link.UnreachableSince ??= link.Since;
            await _feed.EmitRunnerLinkTransitionAsync(
                "link_down", link.Options.RunnerId, link.LastError, link.Attempt, cancellationToken);
        }
    }

    private async Task MaybeNotifyAsync(LinkState link, DateTime now, CancellationToken cancellationToken)
    {
        if (link.NotificationRaisedAt is not null || link.State == RunnerLinkStates.Paused
            || link.BlockedBy is not null
            || link.UnreachableSince is not { } unreachableSince
            || now - unreachableSince < TimeSpan.FromMinutes(5)
            || !ReadyTargets(link.Options.RunnerId)) return;
        link.NotificationRaisedAt = now;
        await _feed.EmitRunnerLinkTransitionAsync(
            "link_down_notification", link.Options.RunnerId, link.LastError, link.Attempt, cancellationToken);
    }

    private bool ReadyTargets(string runnerId) => _tasks.ScanAllAutomationJobs()
        .Where(task => task.State == TaskStates.Ready && !task.Fixture)
        .Select(task => task.ExecutionLocation?.ConfiguredRunnerId
                        ?? task.ExecutionLocation?.RunnerId
                        ?? ProjectExecutionPolicy.ResolveExecutionLocation(_projectSettings.Get(task.ProjectName)))
        .Any(target => string.Equals(target, runnerId, StringComparison.OrdinalIgnoreCase));

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;
    private static string SafeName(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _heartbeats.CapabilitySnapshotAdvertised -= OnHeartbeat;
        await base.StopAsync(cancellationToken);
        foreach (var link in _links.Values)
        {
            await link.Gate.WaitAsync(cancellationToken);
            try
            {
                await StopChildAsync(link, cancellationToken);
                await ReleaseAdoptedForwardAsync(link, cancellationToken);
            }
            finally { link.Gate.Release(); }
        }
    }

    private sealed class LinkState(RunnerLinkOptions options, DateTime now)
    {
        public RunnerLinkOptions Options { get; } = options;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string State = RunnerLinkStates.Down;
        public DateTime Since = now;
        public DateTime? LastHeartbeatAt;
        public DateTime HeartbeatRequiredAfter = now;
        public RunnerLinkProbe? LastProbe;
        public string? LastError;
        public int Attempt;
        public DateTime? NextRetryAt = now;
        public IRunnerLinkProcess? Child;
        public bool Paused;
        public bool Adopted;
        public DateTime? NotificationRaisedAt;
        public DateTime? UnreachableSince = now;
        public bool NeedsCleanup;
        public string? BlockedBy;
        public int? RemoteListenerPid;
        public int? RemoteListenerAgeSeconds;
        public string? Transport;
        public bool ListenerAlarmRaised;

        public RunnerLinkResource Resource() => new(
            Options.RunnerId, Options.Kind, State, Since, LastHeartbeatAt, LastProbe, LastError,
            Attempt, NextRetryAt, Child is { HasExited: false } ? Child.Pid : null, NotificationRaisedAt,
            UnreachableSince, BlockedBy, RemoteListenerPid, RemoteListenerAgeSeconds, Transport);
    }
}
