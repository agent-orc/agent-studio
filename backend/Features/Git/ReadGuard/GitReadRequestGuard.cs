using System.Diagnostics;

namespace AgentStudio.Git;

/// <summary>How the read guard answers a Git start it does not allow.</summary>
public enum GitReadGuardMode
{
    /// <summary>Production: count the spawn, log a warning, let it run.</summary>
    Warn,

    /// <summary>Tests and an attached debugger: throw before the process starts.</summary>
    Strict,
}

/// <summary>
/// Thrown when a GET request starts Git without a frozen exception for its
/// route, or past that route's spawn ceiling (decision D6 of the Git facts
/// Dossier). It deliberately does not derive from
/// <see cref="InvalidOperationException"/> or <see cref="IOException"/>, which
/// several Git helpers catch and turn into "unknown".
/// </summary>
public sealed class GitProcessOnReadRequestException(
    string method, string routeTemplate, string command, string reason)
    : Exception($"git {command} started on read request {method} {routeTemplate} ({reason}). "
        + "Read requests serve published facts; add no exception, move the work to the fact producer.")
{
    public string Method { get; } = method;
    public string RouteTemplate { get; } = routeTemplate;
    public string Command { get; } = command;
    public string Reason { get; } = reason;
}

/// <summary>
/// Ambient read-request scope for the Git process boundary (AGT-3321, Git facts
/// S1). <see cref="GitReadRequestGuardMiddleware"/> opens one scope per GET
/// request from its HTTP method and route template, and
/// <see cref="GitProcessBoundary"/> asks <see cref="OnGitProcessStarting"/>
/// before every Git start. A route listed in
/// <see cref="GitReadSpawnExceptionManifest"/> may spawn up to its ceiling and
/// is counted as migration debt; any other spawn is a violation.
///
/// <para>The scope is an <see cref="AsyncLocal{T}"/>, so work the request fans
/// out to the thread pool still counts against it, while hosted services that
/// never ran under a request do not. A task that inherited the scope and
/// outlives its request reports a detached spawn instead of a violation;
/// request code that starts deliberate background work wraps it in
/// <see cref="Detach"/>.</para>
/// </summary>
internal static class GitReadRequestGuard
{
    private static readonly AsyncLocal<GitReadRequestScope?> Ambient = new();
    private static int _mode = (int)DefaultMode();

    /// <summary>
    /// Process-wide mode. Production builds warn. A Debug build with an attached
    /// debugger, the backend test assembly and an explicit
    /// <c>GitReadGuard:Mode=Strict</c> throw. Stable runs a Debug build through
    /// <c>dotnet run</c>, so the build configuration alone must not decide.
    /// </summary>
    internal static GitReadGuardMode Mode
    {
        get => (GitReadGuardMode)Volatile.Read(ref _mode);
        set => Volatile.Write(ref _mode, (int)value);
    }

    /// <summary>Per-route counters since process start, read by the Admin git-telemetry surface.</summary>
    internal static GitReadGuardLedger Ledger { get; } = new();

    internal static GitReadRequestScope? Current => Ambient.Value;

    internal static GitReadRequestScope BeginRead(string method, string routeTemplate, ILogger? logger = null)
    {
        var scope = new GitReadRequestScope(method, routeTemplate,
            GitReadSpawnExceptionManifest.Find(method, routeTemplate), Ambient.Value, logger);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// Leaves the read scope for work that does not belong to the response, such
    /// as a refresh a read deliberately starts in the background. Dispose
    /// restores the scope.
    /// </summary>
    internal static IDisposable Detach()
    {
        var prior = Ambient.Value;
        Ambient.Value = null;
        return new Restore(prior);
    }

    internal static void OnGitProcessStarting(string command)
    {
        var scope = Ambient.Value;
        if (scope is null) return;
        var verdict = scope.RecordSpawn(command);
        if (verdict is GitReadSpawnVerdict.Allowed or GitReadSpawnVerdict.Detached) return;
        if (Mode != GitReadGuardMode.Strict) return;
        scope.MarkThrown();
        throw scope.Violation(command);
    }

    private static GitReadGuardMode DefaultMode()
    {
#if DEBUG
        if (Debugger.IsAttached) return GitReadGuardMode.Strict;
#endif
        return GitReadGuardMode.Warn;
    }

    private sealed class Restore(GitReadRequestScope? prior) : IDisposable
    {
        public void Dispose() => Ambient.Value = prior;
    }

    internal static void RestoreAmbient(GitReadRequestScope? scope) => Ambient.Value = scope;
}

/// <summary>What the guard decided about one Git start inside a read scope.</summary>
public enum GitReadSpawnVerdict
{
    /// <summary>The route has a frozen exception and is within its ceiling.</summary>
    Allowed,

    /// <summary>The route has no frozen exception.</summary>
    Unlisted,

    /// <summary>The route has an exception and this spawn exceeds its ceiling.</summary>
    CeilingBreached,

    /// <summary>The request already ended; an inherited background task started Git.</summary>
    Detached,
}

/// <summary>Spawn evidence of one GET request.</summary>
internal sealed class GitReadRequestScope : IDisposable
{
    private readonly GitReadRequestScope? _parent;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private readonly List<string> _commands = [];
    private bool _closed;

    internal GitReadRequestScope(string method, string routeTemplate, GitReadSpawnException? exception,
        GitReadRequestScope? parent, ILogger? logger)
    {
        Method = method;
        RouteTemplate = routeTemplate;
        Exception = exception;
        _parent = parent;
        _logger = logger;
    }

    internal string Method { get; }
    internal string RouteTemplate { get; }
    internal GitReadSpawnException? Exception { get; }
    internal bool ThrownAtSpawn { get; private set; }

    internal int Spawns
    {
        get { lock (_gate) return _commands.Count; }
    }

    internal IReadOnlyList<string> Commands
    {
        get { lock (_gate) return _commands.ToArray(); }
    }

    /// <summary>The worst verdict any spawn of this request received so far.</summary>
    internal GitReadSpawnVerdict Verdict
    {
        get
        {
            lock (_gate)
            {
                if (_commands.Count == 0) return GitReadSpawnVerdict.Allowed;
                if (Exception is null) return GitReadSpawnVerdict.Unlisted;
                return _commands.Count > Exception.SpawnCeiling
                    ? GitReadSpawnVerdict.CeilingBreached
                    : GitReadSpawnVerdict.Allowed;
            }
        }
    }

    internal bool Violated => Verdict is GitReadSpawnVerdict.Unlisted or GitReadSpawnVerdict.CeilingBreached;

    internal GitReadSpawnVerdict RecordSpawn(string command)
    {
        lock (_gate)
        {
            if (_closed)
            {
                GitReadRequestGuard.Ledger.RecordDetached(Method, RouteTemplate);
                return GitReadSpawnVerdict.Detached;
            }
            _commands.Add(command);
            if (Exception is null) return GitReadSpawnVerdict.Unlisted;
            return _commands.Count > Exception.SpawnCeiling
                ? GitReadSpawnVerdict.CeilingBreached
                : GitReadSpawnVerdict.Allowed;
        }
    }

    internal void MarkThrown() => ThrownAtSpawn = true;

    internal GitProcessOnReadRequestException Violation(string command)
        => new(Method, RouteTemplate, command, Exception is null
            ? "no frozen exception for this route"
            : $"{Spawns} spawns exceed the frozen ceiling of {Exception.SpawnCeiling}");

    /// <summary>Ends the request: later spawns of inherited tasks count as detached.</summary>
    public void Dispose()
    {
        int spawns;
        string[] commands;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            spawns = _commands.Count;
            commands = _commands.ToArray();
        }
        if (ReferenceEquals(GitReadRequestGuard.Current, this)) GitReadRequestGuard.RestoreAmbient(_parent);
        var verdict = Verdict;
        GitReadRequestGuard.Ledger.RecordRequest(Method, RouteTemplate, spawns, verdict);
        if (verdict is GitReadSpawnVerdict.Unlisted or GitReadSpawnVerdict.CeilingBreached)
        {
            _logger?.LogWarning(
                "git-read-guard-violation method={Method} route={Route} spawns={Spawns} ceiling={Ceiling} reason={Reason} commands=[{Commands}]",
                Method, RouteTemplate, spawns, Exception?.SpawnCeiling, verdict == GitReadSpawnVerdict.Unlisted
                    ? "unlisted" : "ceiling-breached",
                string.Join(",", commands));
        }
    }
}
