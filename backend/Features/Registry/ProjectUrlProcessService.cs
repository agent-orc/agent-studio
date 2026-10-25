using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Security;

namespace AgentStudio.Registry;

/// <summary>AGT-2180 — stable classification vocabulary for URL Preview diagnostics.</summary>
public static class ProjectUrlDiagnosisClasses
{
    public const string NotStarted = "not-started";
    public const string Starting = "starting";
    public const string CommandUnavailable = "command-unavailable";
    public const string InvalidCwd = "invalid-cwd";
    public const string ProcessExited = "process-exited";
    public const string PortInUse = "port-in-use";
    public const string PortNeverOpened = "port-never-opened";
    public const string Timeout = "timeout";
    public const string HttpError = "http-error-response";
    public const string ContentNotRenderable = "content-not-renderable";
    public const string InvalidConfiguration = "invalid-configuration";
    public const string Running = "running";
}

public static class ProjectUrlStartupFailureReasons
{
    public const string ProcessExit = "process-exit";
    public const string PortInUse = "port-in-use";
    public const string SilenceTimeout = "silence-timeout";
    public const string StartupLimit = "startup-limit";
}

/// <summary>
/// AGT-2180 — bounded, redacted evidence snapshot explaining why a preview is
/// (not) ready. This is the actionable-diagnostics contract consumed by the
/// Preview offline card and the Settings quick setup.
/// </summary>
public sealed record ProjectUrlDiagnostic
{
    public string Classification { get; init; } = ProjectUrlDiagnosisClasses.NotStarted;
    public string Summary { get; init; } = "The preview service is not reachable.";
    public string RecommendedAction { get; init; } = "Start the service or review URL Preview setup.";
    public string? Command { get; init; }
    public string? Cwd { get; init; }
    public string? Url { get; init; }
    public int? ConfiguredPort { get; init; }
    public bool ProcessCreated { get; init; }
    public int? ExitCode { get; init; }
    public string StdoutTail { get; init; } = "";
    public string StderrTail { get; init; } = "";
    public bool TimedOut { get; init; }
    public bool PortReachable { get; init; }
    public int? HttpStatus { get; init; }
    public bool ContentReady { get; init; }
    /// <summary>PID of the process listening on the configured preview port.</summary>
    public int? OccupyingProcessId { get; init; }
    /// <summary>Executable name of the process listening on the configured preview port.</summary>
    public string? OccupyingProcessName { get; init; }
    /// <summary>Terminal startup cause when readiness ended before the preview became usable.</summary>
    public string? StartupFailureReason { get; init; }
    /// <summary>Browser embedding evidence when response headers or the iframe can decide it.</summary>
    public bool? IframeReady { get; init; }
    /// <summary>Bounded blocking X-Frame-Options/CSP evidence, when present.</summary>
    public string? FramePolicy { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IProjectUrlProcessStopper
{
    ProjectUrlProcessSnapshot? Stop(string projectId, string urlId);
}

public interface IProjectUrlProcessStarter
{
    ProjectUrlProcessSnapshot StartWithReadiness(ProjectRecord project, ProjectUrlRecord url);
    ProjectUrlDiagnostic? Latest(ProjectRecord project, ProjectUrlRecord url);
}

/// <summary>
/// Owns the dev-server processes launched for project URLs. Sessions remain
/// observable after the initiating request finishes, can be stopped explicitly,
/// and are terminated with the backend host so a preview cannot become an
/// orphaned child process.
///
/// <para>AGT-2180 layers actionable diagnostics on top of the owned sessions:
/// <see cref="ProbeAsync"/> validates TCP, HTTP, and bounded content readiness
/// so an open port or an error page is never mislabeled as Running, and
/// <see cref="StartAsync"/>/<see cref="TestAsync"/> run a bounded start
/// validation whose evidence (exit code, output tails) is redacted before it
/// leaves the host.</para>
/// </summary>
public sealed class ProjectUrlProcessService : IProjectUrlProcessStarter, IProjectUrlProcessStopper, IDisposable
{
    private const int MaxOutputLines = 1000;
    internal const int OutputTailLimit = 8_192;
    internal const int DefaultSilenceTimeoutSeconds = 30;
    internal const int DefaultStartupTimeoutSeconds = 600;
    private static readonly Regex SecretRegex = new(
        @"(?im)(?<userinfo>https?://)[^/\s:@]+(?::[^/\s@]*)?@|(?<bearer>bearer\s+)[a-z0-9._~+/=-]+|(?<key>(?:api[_-]?key|token|password|secret|authorization)\s*[:=]\s*)(?<value>(?:bearer\s+)?[^\s\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<ProjectUrlProcessService> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IProjectUrlPortInspector _portInspector;
    private readonly bool _isHostIsolated;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectUrlDiagnostic> _latest = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private int _disposed;
    internal bool IsHostIsolated => _isHostIsolated;

    public ProjectUrlProcessService(
        ILogger<ProjectUrlProcessService> logger,
        IHttpClientFactory httpFactory,
        IProjectUrlPortInspector? portInspector = null,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _httpFactory = httpFactory;
        _portInspector = portInspector ?? new ProjectUrlPortInspector();
        _isHostIsolated = configuration is null || !SecurityProfiles.IsLocal(configuration);
    }

    /// <summary>
    /// Start or restart the owned server for <paramref name="url"/>. The
    /// command runs through the platform shell in the URL working directory,
    /// falling back to the project's repository path and then root path.
    /// </summary>
    public ProjectUrlProcessSnapshot Start(ProjectRecord project, ProjectUrlRecord url) =>
        StartProcess(project, url, readinessPending: false);

    private ProjectUrlProcessSnapshot StartProcess(
        ProjectRecord project,
        ProjectUrlRecord url,
        bool readinessPending)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(url);
        var rule = url.StartRule;
        if (rule == null || string.IsNullOrWhiteSpace(rule.Command))
            throw new ArgumentException("URL has no start command to run.", nameof(url));

        var cwd = ResolveWorkingDirectory(project, rule);
        var key = Key(project.Id, url.Id);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            RetirePrevious(key);

            var targetPort = ResolveLocalTargetPort(url);
            var occupant = targetPort == null ? null : _portInspector.FindListener(targetPort.Value);
            if (occupant != null)
            {
                var diagnostic = Save(project.Id, url.Id, Base(rule, url.Url, cwd) with
                {
                    Classification = ProjectUrlDiagnosisClasses.PortInUse,
                    Summary = $"Port {targetPort} is already in use by {occupant.ProcessName} (PID {occupant.ProcessId}).",
                    RecommendedAction = "Stop the occupying process or configure a different preview port, then Retry.",
                    ConfiguredPort = targetPort,
                    PortReachable = true,
                    StartupFailureReason = ProjectUrlStartupFailureReasons.PortInUse,
                    OccupyingProcessId = occupant.ProcessId,
                    OccupyingProcessName = Redact(occupant.ProcessName),
                });
                _logger.LogWarning(
                    "project-url-port-occupied project={Id} url={UrlId} port={Port} process={ProcessName} pid={Pid}",
                    project.Id, url.Id, targetPort, occupant.ProcessName, occupant.ProcessId);
                throw new ProjectUrlPortOccupiedException(diagnostic);
            }

            var process = new Process
            {
                StartInfo = BuildStartInfo(rule.Command, cwd, _isHostIsolated),
                EnableRaisingEvents = true,
            };
            var session = new Session(project.Id, url.Id, rule.Command, cwd, process);
            process.OutputDataReceived += (_, eventArgs) => AppendOutput(session, eventArgs.Data, isError: false);
            process.ErrorDataReceived += (_, eventArgs) => AppendOutput(session, eventArgs.Data, isError: true);
            process.Exited += (_, _) => MarkExited(session);

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Process.Start returned false.");

                session.ProcessId = process.Id;
                _sessions[key] = session;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.StandardInput.Close();

                lock (session.Gate)
                {
                    if (!readinessPending && session.State == ProjectUrlProcessStates.Starting && !process.HasExited)
                        session.State = ProjectUrlProcessStates.Running;
                    AppendOutputLocked(session, $"[studio] Started process {session.ProcessId}.");
                }

                if (process.HasExited)
                    MarkExited(session);

                _logger.LogInformation(
                    "project-url-started project={Id} url={UrlId} pid={Pid} command={Command} cwd={Cwd}",
                    project.Id, url.Id, session.ProcessId, rule.Command, cwd);
                return Snapshot(session);
            }
            catch (Exception ex)
            {
                _sessions.TryRemove(key, out _);
                TryKillAndDispose(process);
                _logger.LogError(ex,
                    "project-url-start-failed project={Id} url={UrlId} command={Command} cwd={Cwd}",
                    project.Id, url.Id, rule.Command, cwd);
                throw new InvalidOperationException($"Failed to start dev server: {ex.Message}", ex);
            }
        }
    }

    public ProjectUrlProcessSnapshot? Get(string projectId, string urlId)
        => _sessions.TryGetValue(Key(projectId, urlId), out var session)
            ? Snapshot(session)
            : null;

    public ProjectUrlProcessSnapshot? Stop(string projectId, string urlId)
    {
        lock (_lifecycleGate)
        {
            if (!_sessions.TryGetValue(Key(projectId, urlId), out var session)) return null;
            StopSession(session, "stopped by operator");
            return Snapshot(session);
        }
    }

    /// <summary>Stop every preview process owned by a project before it is deleted.</summary>
    public IReadOnlyList<ProjectUrlProcessSnapshot> StopProject(string projectId)
    {
        lock (_lifecycleGate)
        {
            var sessions = _sessions.Values
                .Where(session => string.Equals(session.ProjectId, projectId, StringComparison.Ordinal))
                .ToArray();
            foreach (var session in sessions)
                StopSession(session, "project removed");
            return sessions.Select(Snapshot).ToArray();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        lock (_lifecycleGate)
        {
            foreach (var session in _sessions.Values)
            {
                StopSession(session, "backend shutdown");
                session.Process.Dispose();
                session.StartupCancellation.Dispose();
            }
        }
    }

    // ── AGT-2180: actionable diagnostics on top of the owned sessions ──────

    /// <summary>Last published diagnostic for a URL, if any (no probe).</summary>
    public ProjectUrlDiagnostic? Latest(ProjectRecord project, ProjectUrlRecord url) =>
        _latest.TryGetValue(Key(project.Id, url.Id), out var value) ? value : null;

    /// <summary>
    /// Probe the URL's TCP, HTTP, and bounded content readiness without
    /// touching any process. Session evidence (exit code, output tails) is
    /// folded in when Studio owns a process for the URL.
    /// </summary>
    public async Task<ProjectUrlDiagnostic> ProbeAsync(ProjectRecord project, ProjectUrlRecord url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(url);
        var seed = Latest(project, url);
        if (seed?.Classification == ProjectUrlDiagnosisClasses.PortInUse)
        {
            var port = ResolveLocalTargetPort(url);
            var occupant = port == null ? null : _portInspector.FindListener(port.Value);
            if (occupant != null)
                return Save(project.Id, url.Id, seed with
                {
                    Summary = $"Port {port} is already in use by {occupant.ProcessName} (PID {occupant.ProcessId}).",
                    OccupyingProcessId = occupant.ProcessId,
                    OccupyingProcessName = Redact(occupant.ProcessName),
                    CheckedAt = DateTimeOffset.UtcNow,
                });
            seed = null;
        }
        var snapshot = Get(project.Id, url.Id);
        if (snapshot != null)
        {
            var (stdoutTail, stderrTail) = OutputTails(project.Id, url.Id);
            seed = (seed ?? Base(url.StartRule, url.Url, url.StartRule?.Cwd)) with
            {
                ProcessCreated = true,
                ExitCode = snapshot.ExitCode,
                StdoutTail = Redact(stdoutTail),
                StderrTail = Redact(stderrTail),
            };
        }
        if (seed?.StartupFailureReason != null
            || snapshot?.State == ProjectUrlProcessStates.Starting
                && seed?.Classification == ProjectUrlDiagnosisClasses.Starting)
            return Save(project.Id, url.Id, seed with { CheckedAt = DateTimeOffset.UtcNow });
        var evidence = await ProbeTargetAsync(url.StartRule, url.Url, seed, cancellationToken);
        return Save(project.Id, url.Id, evidence);
    }

    /// <summary>
    /// Start the owned session and validate process, TCP, HTTP, and bounded
    /// content readiness before reporting Running. Returns the same redacted
    /// diagnostic contract as <see cref="ProbeAsync"/>.
    /// </summary>
    public async Task<ProjectUrlDiagnostic> StartAsync(ProjectRecord project, ProjectUrlRecord url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(url);
        var invalid = ValidateStartConfiguration(project, url, out var rule, out var target, out var cwd);
        if (invalid != null)
            return Save(project.Id, url.Id, invalid);

        try
        {
            StartProcess(project, url with { StartRule = rule! with { Cwd = cwd } }, readinessPending: true);
        }
        catch (ProjectUrlPortOccupiedException)
        {
            return Latest(project, url)!;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Save(project.Id, url.Id, Base(rule, url.Url, cwd) with
            {
                Classification = ProjectUrlDiagnosisClasses.CommandUnavailable,
                Summary = "The start command could not be launched.",
                RecommendedAction = "Check the command and local tool installation in Settings.",
                StderrTail = Redact(ex.Message),
            });
        }

        return await WaitForReadinessAsync(project, url, rule!, target!, cwd!, cancellationToken);
    }

    /// <summary>
    /// Start Preview readiness under service ownership and return immediately.
    /// The session remains <c>starting</c> while the activity-aware validation
    /// runs, so callers can poll live output without holding an HTTP request
    /// open for a multi-minute cold build.
    /// </summary>
    public ProjectUrlProcessSnapshot StartWithReadiness(ProjectRecord project, ProjectUrlRecord url)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(url);
        var invalid = ValidateStartConfiguration(project, url, out var rule, out var target, out var cwd);
        if (invalid != null)
        {
            Save(project.Id, url.Id, invalid);
            throw new ArgumentException(invalid.Summary, nameof(url));
        }

        var snapshot = StartProcess(
            project,
            url with { StartRule = rule! with { Cwd = cwd } },
            readinessPending: true);
        if (!_sessions.TryGetValue(Key(project.Id, url.Id), out var session))
            throw new InvalidOperationException("The preview process session could not be observed after start.");

        _ = ObserveReadinessAsync(
            session,
            WaitForReadinessAsync(project, url, rule!, target!, cwd!, session.StartupCancellation.Token));
        return snapshot;
    }

    private async Task ObserveReadinessAsync(Session session, Task<ProjectUrlDiagnostic> readiness)
    {
        try
        {
            await readiness;
        }
        catch (OperationCanceledException) when (session.StartupCancellation.IsCancellationRequested)
        {
            // Stop/restart/shutdown owns this cancellation and already settled
            // the observable process state.
            _logger.LogDebug(
                "project-url-readiness-cancelled project={ProjectId} url={UrlId} pid={Pid}",
                session.ProjectId, session.UrlId, session.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "project-url-readiness-failed project={ProjectId} url={UrlId} pid={Pid}",
                session.ProjectId, session.UrlId, session.ProcessId);
            MarkReadinessFailed(session, "Startup validation failed unexpectedly.");
        }
    }

    private async Task<ProjectUrlDiagnostic> WaitForReadinessAsync(
        ProjectRecord project,
        ProjectUrlRecord url,
        ProjectUrlStartRule rule,
        Uri target,
        string cwd,
        CancellationToken cancellationToken)
    {
        Save(project.Id, url.Id, Base(rule, url.Url, cwd) with
        {
            Classification = ProjectUrlDiagnosisClasses.Starting,
            Summary = "The process is working and console activity is extending the readiness wait.",
            RecommendedAction = "Wait while the command keeps producing output.",
            ProcessCreated = true,
        });

        // The command may spend minutes installing dependencies and building
        // before it ever binds a port (e.g. `npm install && ng serve`). Rather
        // than failing on a fixed wall-clock deadline, keep waiting as long as
        // the process is still emitting console output; only silence counts
        // against the readiness window. A hard cap bounds the wait regardless.
        var idleWindow = TimeSpan.FromSeconds(Math.Clamp(
            rule.ReadinessTimeoutSeconds <= 0 ? DefaultSilenceTimeoutSeconds : rule.ReadinessTimeoutSeconds,
            2,
            300));
        var startupLimitSeconds = Math.Clamp(
            rule.StartupTimeoutSeconds <= 0 ? DefaultStartupTimeoutSeconds : rule.StartupTimeoutSeconds,
            2,
            3600);
        var hardCap = TimeSpan.FromSeconds(startupLimitSeconds);
        var startedAt = DateTime.UtcNow;
        var everPortReachable = false;
        var hardCapReached = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = Get(project.Id, url.Id);
                if (current == null || !ProjectUrlProcessStates.IsActive(current.State))
                {
                    var (stdoutTail, stderrTail) = OutputTails(project.Id, url.Id);
                    var unavailable = current?.ExitCode is 126 or 127
                        || stderrTail.Contains("not found", StringComparison.OrdinalIgnoreCase)
                        || stderrTail.Contains("not recognized", StringComparison.OrdinalIgnoreCase);
                    var exited = Save(project.Id, url.Id, Base(rule, url.Url, cwd) with
                    {
                        Classification = unavailable ? ProjectUrlDiagnosisClasses.CommandUnavailable : ProjectUrlDiagnosisClasses.ProcessExited,
                        Summary = unavailable ? "The start command is not available in this environment." : "The service process exited before the preview became ready.",
                        RecommendedAction = unavailable ? "Install the command or correct the start command in Settings." : "Review the process output, correct the setup, then Retry.",
                        ProcessCreated = true, ExitCode = current?.ExitCode,
                        StartupFailureReason = ProjectUrlStartupFailureReasons.ProcessExit,
                        StdoutTail = Redact(stdoutTail), StderrTail = Redact(stderrTail),
                    });
                    return exited;
                }

                var port = rule.Port ?? target.Port;
                everPortReachable |= await IsPortReachableAsync(target.Host, port, cancellationToken);
                if (everPortReachable)
                {
                    var (stdoutTail, stderrTail) = OutputTails(project.Id, url.Id);
                    var ready = await ProbeTargetAsync(rule, url.Url, Base(rule, url.Url, cwd) with
                    {
                        ProcessCreated = true, PortReachable = true,
                        StdoutTail = Redact(stdoutTail), StderrTail = Redact(stderrTail),
                    }, cancellationToken);
                    if (ready.Classification is ProjectUrlDiagnosisClasses.Running
                        or ProjectUrlDiagnosisClasses.HttpError
                        or ProjectUrlDiagnosisClasses.ContentNotRenderable)
                    {
                        var settled = Save(project.Id, url.Id, ready);
                        MarkReadinessReady(project.Id, url.Id);
                        return settled;
                    }
                }

                var now = DateTime.UtcNow;
                if (now - startedAt >= hardCap)
                {
                    hardCapReached = true;
                    break;
                }
                // Fail only on sustained silence: the URL is still unreachable
                // and the process has produced no output for the idle window.
                if (now - LastOutputAt(project.Id, url.Id) >= idleWindow)
                    break;

                await Task.Delay(250, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var (stdoutTail, stderrTail) = OutputTails(project.Id, url.Id);
            return Save(project.Id, url.Id, Base(rule, url.Url, cwd) with
            {
                Classification = ProjectUrlDiagnosisClasses.Timeout,
                Summary = "Startup validation was cancelled before readiness was confirmed.",
                RecommendedAction = "Retry the bounded validation.",
                ProcessCreated = true, TimedOut = true,
                StdoutTail = Redact(stdoutTail), StderrTail = Redact(stderrTail),
            });
        }

        var idleSeconds = (int)Math.Round(Math.Max(0, (DateTime.UtcNow - LastOutputAt(project.Id, url.Id)).TotalSeconds));
        var (finalStdout, finalStderr) = OutputTails(project.Id, url.Id);
        var failure = StartupFailure(
            rule, url.Url, cwd, hardCapReached, everPortReachable, idleSeconds, startupLimitSeconds,
            Redact(finalStdout), Redact(finalStderr));
        Save(project.Id, url.Id, failure);
        if (_sessions.TryGetValue(Key(project.Id, url.Id), out var session))
            MarkReadinessFailed(session, failure.Summary);
        return failure;
    }

    private ProjectUrlDiagnostic? ValidateStartConfiguration(
        ProjectRecord project,
        ProjectUrlRecord url,
        out ProjectUrlStartRule? rule,
        out Uri? target,
        out string? cwd)
    {
        rule = url.StartRule;
        target = null;
        cwd = null;
        if (rule == null || string.IsNullOrWhiteSpace(rule.Command))
            return InvalidDiagnostic(rule, url.Url, "A start command is required.");
        if (!Uri.TryCreate(rule.HealthUrl ?? url.Url, UriKind.Absolute, out target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) ||
            rule.Port is <= 0 or > 65535)
            return InvalidDiagnostic(rule, url.Url, "The URL, health target, or port is invalid.");

        cwd = ResolveDiagnosticCwd(project, rule.Cwd);
        if (cwd == null || !Directory.Exists(cwd))
            return new ProjectUrlDiagnostic
            {
                Classification = ProjectUrlDiagnosisClasses.InvalidCwd,
                Summary = "The configured working directory does not exist.",
                RecommendedAction = "Open Settings and choose an existing project folder.",
                Command = Redact(rule.Command),
                Cwd = Redact(cwd ?? rule.Cwd),
                Url = Redact(url.Url),
                ConfiguredPort = rule.Port,
            };
        return null;
    }

    /// <summary>
    /// Build the terminal diagnostic when startup validation gives up: either
    /// the configured startup cap was reached, or the
    /// process fell silent for the idle window while the URL stayed unreachable.
    /// </summary>
    internal static ProjectUrlDiagnostic StartupFailure(
        ProjectUrlStartRule? rule, string url, string? cwd,
        bool hardCapReached, bool everPortReachable, int idleSeconds,
        int startupLimitSeconds, string stdoutTail, string stderrTail)
    {
        var startupLimit = FormatDuration(startupLimitSeconds);
        var summary = hardCapReached
            ? (everPortReachable
                ? $"The port opened, but the preview did not become ready within the {startupLimit} startup limit."
                : $"The URL did not become reachable within the {startupLimit} startup limit.")
            : (everPortReachable
                ? $"The port opened, but HTTP content did not become ready and the process produced no console output for {idleSeconds}s."
                : $"The URL is not reachable and the process produced no console output for {idleSeconds}s.");
        return Base(rule, url, cwd) with
        {
            Classification = everPortReachable ? ProjectUrlDiagnosisClasses.Timeout : ProjectUrlDiagnosisClasses.PortNeverOpened,
            Summary = summary,
            RecommendedAction = "Verify the port, URL, and readiness target in Settings, then Retry.",
            ProcessCreated = true, TimedOut = true, PortReachable = everPortReachable,
            StartupFailureReason = hardCapReached
                ? ProjectUrlStartupFailureReasons.StartupLimit
                : ProjectUrlStartupFailureReasons.SilenceTimeout,
            StdoutTail = stdoutTail, StderrTail = stderrTail,
        };
    }

    private static string FormatDuration(int seconds) =>
        seconds % 60 == 0 ? $"{seconds / 60}-minute" : $"{seconds}-second";

    private static int? ResolveLocalTargetPort(ProjectUrlRecord url)
    {
        if (!Uri.TryCreate(url.StartRule?.HealthUrl ?? url.Url, UriKind.Absolute, out var target))
            return null;
        if (!IsLocalHost(target.Host)) return null;
        return url.StartRule?.Port ?? target.Port;
    }

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));

    /// <summary>
    /// Validate a candidate configuration for the Settings quick setup. The
    /// spawned validation process never outlives the request — saving and
    /// starting the real URL is a separate action.
    /// </summary>
    public async Task<ProjectUrlDiagnostic> TestAsync(ProjectRecord project, ProjectUrlRecord candidate, CancellationToken cancellationToken)
    {
        try
        {
            return await StartAsync(project, candidate, cancellationToken);
        }
        finally
        {
            Stop(project.Id, candidate.Id);
        }
    }

    private async Task<ProjectUrlDiagnostic> ProbeTargetAsync(ProjectUrlStartRule? rule, string url, ProjectUrlDiagnostic? seed, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(rule?.HealthUrl ?? url, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return InvalidDiagnostic(rule, url, "The URL or health target is not a valid HTTP address.");

        var port = rule?.Port ?? target.Port;
        var portReachable = await IsPortReachableAsync(target.Host, port, cancellationToken);
        var basis = (seed ?? Base(rule, url, rule?.Cwd)) with { PortReachable = portReachable };
        if (!portReachable)
            return basis with
            {
                Classification = seed?.ProcessCreated == true ? ProjectUrlDiagnosisClasses.PortNeverOpened : ProjectUrlDiagnosisClasses.NotStarted,
                Summary = "Nothing is accepting connections at the configured preview address.",
                RecommendedAction = rule == null ? "Open Settings and add a start configuration." : "Start the service or review its setup.",
            };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await _httpFactory.CreateClient("project-url-readiness")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status >= 400)
                return basis with
                {
                    Classification = ProjectUrlDiagnosisClasses.HttpError,
                    Summary = $"The service responded with HTTP {status}.",
                    RecommendedAction = "Correct the health target or fix the server response, then Retry.", HttpStatus = status,
                };
            var framePolicy = BlockingFramePolicy(response);
            if (framePolicy != null)
                return basis with
                {
                    Classification = ProjectUrlDiagnosisClasses.ContentNotRenderable,
                    Summary = "The page is healthy, but its response headers block embedded previews.",
                    RecommendedAction = "Allow the Agent Studio origin in the page's frame policy, or open it externally.",
                    HttpStatus = status, ContentReady = true, IframeReady = false, FramePolicy = framePolicy,
                };
            var bytes = await ReadBoundedAsync(response.Content, 4096, cancellationToken);
            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            var sample = Encoding.UTF8.GetString(bytes);
            var meaningfulHtml = Regex.IsMatch(sample,
                @"<(?!/?(?:html|head|body|meta|link|script|style|title)\b)[a-z][^>]*>",
                RegexOptions.IgnoreCase);
            var renderable = bytes.Length > 0 && (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                ? (!media.Contains("html", StringComparison.OrdinalIgnoreCase) || meaningfulHtml)
                : media.Length == 0 && meaningfulHtml);
            if (!renderable)
                return basis with
                {
                    Classification = ProjectUrlDiagnosisClasses.ContentNotRenderable,
                    Summary = "The service responded, but it did not return renderable page content.",
                    RecommendedAction = "Choose a page URL that returns HTML, or open the target externally.",
                    HttpStatus = status, ContentReady = false, IframeReady = false,
                };
            return basis with
            {
                Classification = ProjectUrlDiagnosisClasses.Running,
                Summary = "The preview service is ready and returned renderable content.",
                RecommendedAction = "No recovery action is needed.", HttpStatus = status, ContentReady = true,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return basis with
            {
                Classification = ProjectUrlDiagnosisClasses.Timeout,
                Summary = "The port accepted a connection, but the HTTP readiness check did not complete.",
                RecommendedAction = "Verify the health target and readiness timeout.", TimedOut = ex is TaskCanceledException,
                StderrTail = Redact(ex.Message),
            };
        }
    }

    internal static ProjectUrlDiagnostic InvalidDiagnostic(ProjectUrlStartRule? rule, string? url, string summary) => new()
    {
        Classification = ProjectUrlDiagnosisClasses.InvalidConfiguration,
        Summary = summary,
        RecommendedAction = "Open Settings and complete URL Preview quick setup.",
        Command = Redact(rule?.Command), Cwd = Redact(rule?.Cwd), Url = Redact(url), ConfiguredPort = rule?.Port,
    };

    internal static string Redact(string? value)
    {
        var redacted = SecretRegex.Replace(value ?? "", match => match.Groups["bearer"].Success
            ? match.Groups["bearer"].Value + "[REDACTED]"
            : match.Groups["userinfo"].Success
                ? match.Groups["userinfo"].Value + "[REDACTED]@"
                : match.Groups["key"].Value + "[REDACTED]");
        return Tail(redacted, OutputTailLimit);
    }

    internal static string Tail(string value, int limit) => value.Length <= limit ? value : "…" + value[^limit..];

    private static ProjectUrlDiagnostic Base(ProjectUrlStartRule? rule, string url, string? cwd) => new()
    {
        Command = Redact(rule?.Command), Cwd = Redact(cwd), Url = Redact(url), ConfiguredPort = rule?.Port,
    };

    private static string? BlockingFramePolicy(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Frame-Options", out var xfoValues))
        {
            var xfo = string.Join(", ", xfoValues);
            if (xfo.Contains("DENY", StringComparison.OrdinalIgnoreCase) ||
                xfo.Contains("SAMEORIGIN", StringComparison.OrdinalIgnoreCase))
                return Tail($"X-Frame-Options: {xfo}", 512);
        }

        if (!response.Headers.TryGetValues("Content-Security-Policy", out var cspValues))
            return null;
        var csp = string.Join("; ", cspValues);
        var ancestors = Regex.Match(csp, @"(?:^|;)\s*frame-ancestors\s+(?<sources>[^;]+)", RegexOptions.IgnoreCase);
        if (!ancestors.Success) return null;
        var sources = ancestors.Groups["sources"].Value.Trim();
        if (sources.Contains("'none'", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sources, "'self'", StringComparison.OrdinalIgnoreCase))
            return Tail($"Content-Security-Policy: frame-ancestors {sources}", 512);
        return null;
    }

    private ProjectUrlDiagnostic Save(string projectId, string urlId, ProjectUrlDiagnostic value)
    {
        _latest[Key(projectId, urlId)] = value;
        return value;
    }

    /// <summary>Diagnostic-path cwd resolution: relative values resolve against the project source roots.</summary>
    private static string? ResolveDiagnosticCwd(ProjectRecord project, string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? project.RepositoryPath ?? project.RootPath :
        Path.IsPathRooted(configured) ? configured : Path.Combine(project.RepositoryPath ?? project.RootPath ?? "", configured);

    private static async Task<bool> IsPortReachableAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(350), cancellationToken);
            return true;
        }
        catch { return false; }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[limit];
        var read = await stream.ReadAsync(buffer.AsMemory(0, limit), cancellationToken);
        return buffer[..read];
    }

    private (string Stdout, string Stderr) OutputTails(string projectId, string urlId)
        => _sessions.TryGetValue(Key(projectId, urlId), out var session)
            ? (session.StdoutTail.Value, session.StderrTail.Value)
            : ("", "");

    /// <summary>UTC timestamp of the most recent process console output for the URL.</summary>
    private DateTime LastOutputAt(string projectId, string urlId)
        => _sessions.TryGetValue(Key(projectId, urlId), out var session)
            ? session.LastOutputUtc
            : DateTime.UtcNow;

    internal static ProcessStartInfo BuildStartInfo(string command, string cwd, bool isolated = false)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Shared profiles keep the API's credentials and service configuration
        // out of repository start rules. Local keeps its normal shell environment.
        if (isolated)
        {
            psi.Environment.Clear();
            foreach (var name in new[] { "PATH", "SystemRoot", "ComSpec", "PATHEXT", "TMPDIR", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    psi.Environment[name] = value;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return psi;
    }

    /// <summary>Resolve only from explicit URL configuration or project source roots.</summary>
    public static string ResolveWorkingDirectory(ProjectRecord project, ProjectUrlStartRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Cwd))
        {
            if (!Directory.Exists(rule.Cwd))
                throw new InvalidOperationException($"Working directory does not exist: {rule.Cwd}");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.Cwd));
        }

        foreach (var candidate in new[] { project.RepositoryPath, project.RootPath })
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        var configured = project.RepositoryPath ?? project.RootPath;
        throw new InvalidOperationException(configured == null
            ? "No working directory is configured. Set a URL cwd or project repository/root path."
            : $"Working directory does not exist: {configured}");
    }

    private void RetirePrevious(string key)
    {
        if (!_sessions.TryRemove(key, out var previous)) return;
        StopSession(previous, "restarted");
        previous.Process.Dispose();
        previous.StartupCancellation.Dispose();
    }

    private void StopSession(Session session, string reason)
    {
        lock (session.Gate)
        {
            if (session.State is ProjectUrlProcessStates.Stopped or ProjectUrlProcessStates.Exited)
                return;
            try
            {
                session.StartupCancellation.Cancel();
                if (!session.Process.HasExited)
                    session.Process.Kill(entireProcessTree: true);
                if (!session.Process.WaitForExit(5000))
                    throw new InvalidOperationException("Process did not stop within five seconds.");

                session.State = ProjectUrlProcessStates.Stopped;
                session.FinishedAtUtc = DateTimeOffset.UtcNow;
                session.ExitCode = TryGetExitCode(session.Process);
                AppendOutputLocked(session, $"[studio] Process {reason}.");
                _logger.LogInformation(
                    "project-url-stopped project={ProjectId} url={UrlId} pid={Pid} reason={Reason}",
                    session.ProjectId, session.UrlId, session.ProcessId, reason);
            }
            catch (Exception ex)
            {
                session.State = ProjectUrlProcessStates.Failed;
                session.FinishedAtUtc = DateTimeOffset.UtcNow;
                AppendOutputLocked(session, $"[studio] Stop failed: {ex.Message}");
                _logger.LogWarning(ex,
                    "project-url-stop-failed project={ProjectId} url={UrlId} pid={Pid} reason={Reason}",
                    session.ProjectId, session.UrlId, session.ProcessId, reason);
            }
        }
    }

    private void MarkExited(Session session)
    {
        lock (session.Gate)
        {
            if (session.State is ProjectUrlProcessStates.Stopped or ProjectUrlProcessStates.Exited)
                return;
            if (session.State == ProjectUrlProcessStates.Failed)
            {
                session.ExitCode = TryGetExitCode(session.Process);
                AppendOutputLocked(session,
                    $"[studio] Process exited with code {session.ExitCode?.ToString() ?? "unknown"}.");
                return;
            }
            session.State = ProjectUrlProcessStates.Exited;
            session.FinishedAtUtc = DateTimeOffset.UtcNow;
            session.ExitCode = TryGetExitCode(session.Process);
            AppendOutputLocked(session,
                $"[studio] Process exited with code {session.ExitCode?.ToString() ?? "unknown"}.");
            _logger.LogInformation(
                "project-url-exited project={ProjectId} url={UrlId} pid={Pid} exitCode={ExitCode}",
                session.ProjectId, session.UrlId, session.ProcessId, session.ExitCode);
        }
    }

    private void MarkReadinessReady(string projectId, string urlId)
    {
        if (!_sessions.TryGetValue(Key(projectId, urlId), out var session)) return;
        lock (session.Gate)
        {
            if (session.State != ProjectUrlProcessStates.Starting || session.Process.HasExited) return;
            session.State = ProjectUrlProcessStates.Running;
            AppendOutputLocked(session, "[studio] Preview readiness confirmed.");
        }
    }

    private static void MarkReadinessFailed(Session session, string summary)
    {
        lock (session.Gate)
        {
            if (session.State != ProjectUrlProcessStates.Starting || session.Process.HasExited) return;
            session.State = ProjectUrlProcessStates.Failed;
            session.FinishedAtUtc = DateTimeOffset.UtcNow;
            AppendOutputLocked(session, $"[studio] Readiness failed: {summary}");
        }
    }

    private void AppendOutput(Session session, string? line, bool isError)
    {
        if (line == null) return;
        // Real process output resets the console-silence window (StartAsync).
        session.LastOutputUtc = DateTime.UtcNow;
        lock (session.Gate) AppendOutputLocked(session, line);
        if (isError)
        {
            session.StderrTail.Append(line);
            _logger.LogWarning("project-url-error project={ProjectId} url={UrlId} text={Text}",
                session.ProjectId, session.UrlId, line);
        }
        else
        {
            session.StdoutTail.Append(line);
            _logger.LogDebug("project-url-output project={ProjectId} url={UrlId} text={Text}",
                session.ProjectId, session.UrlId, line);
        }
    }

    private static void AppendOutputLocked(Session session, string line)
    {
        session.Output.Add(line);
        if (session.Output.Count > MaxOutputLines)
            session.Output.RemoveRange(0, session.Output.Count - MaxOutputLines);
    }

    private static ProjectUrlProcessSnapshot Snapshot(Session session)
    {
        lock (session.Gate)
        {
            return new ProjectUrlProcessSnapshot(
                session.ProjectId,
                session.UrlId,
                session.Command,
                session.Cwd,
                session.State,
                session.ProcessId,
                session.StartedAtUtc,
                session.FinishedAtUtc,
                session.ExitCode,
                [.. session.Output]);
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    private void TryKillAndDispose(Process process)
    {
        try
        {
            if (process.Id > 0 && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "project-url-failed-launch-cleanup-failed");
        }
        finally { process.Dispose(); }
    }

    private static string Key(string projectId, string urlId) => $"{projectId}::{urlId}";

    /// <summary>Bounded string tail used for redacted stdout/stderr diagnostics.</summary>
    private sealed class TailBuffer(int limit)
    {
        private readonly StringBuilder _value = new();
        private readonly object _gate = new();
        public string Value { get { lock (_gate) return Tail(_value.ToString(), limit); } }
        public void Append(string? line)
        {
            if (line == null) return;
            lock (_gate)
            {
                _value.AppendLine(line);
                if (_value.Length > limit * 2) _value.Remove(0, _value.Length - limit);
            }
        }
    }

    private sealed class Session(
        string projectId,
        string urlId,
        string command,
        string cwd,
        Process process)
    {
        private long _lastOutputTicks = DateTime.UtcNow.Ticks;

        public object Gate { get; } = new();
        /// <summary>UTC time of the last process console output; lock-free for the readiness poll.</summary>
        public DateTime LastOutputUtc
        {
            get => new(Interlocked.Read(ref _lastOutputTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastOutputTicks, value.Ticks);
        }
        public string ProjectId { get; } = projectId;
        public string UrlId { get; } = urlId;
        public string Command { get; } = command;
        public string Cwd { get; } = cwd;
        public Process Process { get; } = process;
        public CancellationTokenSource StartupCancellation { get; } = new();
        public int ProcessId { get; set; }
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public int? ExitCode { get; set; }
        public string State { get; set; } = ProjectUrlProcessStates.Starting;
        public List<string> Output { get; } = [];
        public TailBuffer StdoutTail { get; } = new(OutputTailLimit);
        public TailBuffer StderrTail { get; } = new(OutputTailLimit);
    }
}

public static class ProjectUrlProcessStates
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Stopped = "stopped";
    public const string Failed = "failed";

    public static bool IsActive(string state) => state is Starting or Running;
}

public sealed record ProjectUrlProcessSnapshot(
    string ProjectId,
    string UrlId,
    string Command,
    string Cwd,
    string State,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int? ExitCode,
    IReadOnlyList<string> Output)
{
    /// <summary>Compatibility marker retained for existing start callers.</summary>
    public bool Started => ProcessId > 0;
}
