using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// One connect attempt against one loopback origin, kept as evidence even
/// when a later attempt succeeds.
/// </summary>
public sealed record FrontendProbeAttempt(string Url, bool Reachable, string Detail, int DurationMs);

/// <summary>
/// Verdict of the post-restart frontend probe plus everything an operator
/// needs to argue with it: every origin that was tried with its outcome, and
/// the listener list for the frontend port read at the moment of the verdict.
/// </summary>
public sealed record FrontendProbeResult(
    bool Up,
    string? ReachedUrl,
    int Port,
    int Seconds,
    IReadOnlyList<FrontendProbeAttempt> Attempts,
    IReadOnlyList<string> Listeners);

/// <summary>
/// Loopback origins a frontend probe has to try before it may call the
/// frontend down.
///
/// AGT-2862: <c>ng serve</c> without an explicit <c>--host</c> binds
/// "localhost", which resolves to <c>::1</c> on a Windows host, while the
/// Update Service polled <c>http://127.0.0.1:4011</c> only. The v0.6.0
/// upgrade therefore reported "frontend dev server did not come up" while
/// <c>curl http://localhost:4011/</c> answered 200 (run 1027d2e7,
/// 17.09.2026). The dev server now binds the IPv4 loopback explicitly
/// (<c>frontend/angular.json</c>), and the probe additionally tries the other
/// loopback spellings so a server that is nonetheless on <c>::1</c> - an
/// operator-started <c>ng serve</c>, an outer wrapper that predates the bind
/// - is recognised as up instead of declared down.
/// </summary>
public static class LoopbackProbeTargets
{
    /// <summary>
    /// Fixed order, so two runs on the same host produce comparable evidence.
    /// The configured origin always goes first: an operator who points
    /// <see cref="UpdateServiceOptions.FrontendUrl"/> somewhere specific gets
    /// that answer first, and the extra spellings only widen the search.
    /// </summary>
    private static readonly string[] LoopbackHosts = { "localhost", "127.0.0.1", "::1" };

    /// <summary>
    /// Origins to probe for <paramref name="configuredUrl"/>. A non-loopback
    /// origin is returned unchanged: widening a remote address to loopback
    /// would probe a different machine's frontend. An unparsable origin
    /// returns an empty list, which the caller treats as "not configured".
    /// </summary>
    public static IReadOnlyList<string> For(string? configuredUrl)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri)) return Array.Empty<string>();
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return Array.Empty<string>();

        var targets = new List<string> { Origin(uri.Scheme, uri.DnsSafeHost, uri.Port) };
        if (!IsLoopbackHost(uri.DnsSafeHost)) return targets;

        foreach (var host in LoopbackHosts)
        {
            var candidate = Origin(uri.Scheme, host, uri.Port);
            if (!targets.Contains(candidate, StringComparer.OrdinalIgnoreCase)) targets.Add(candidate);
        }
        return targets;
    }

    /// <summary>
    /// True for the three spellings of "this machine": the literal name and
    /// either loopback address family.
    /// </summary>
    public static bool IsLoopbackHost(string? host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    /// <summary>Host part of a URL, re-bracketing an IPv6 literal.</summary>
    public static string Origin(string scheme, string host, int port)
        => $"{scheme}://{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:{port}";
}

/// <summary>
/// Listener evidence for one TCP port, in the shape an operator would read
/// out of <c>netstat -ano</c>. Enumerated through
/// <see cref="IPGlobalProperties"/> rather than by spawning <c>netstat</c>,
/// so it stays inside the repository's process-spawn rules and works the same
/// on both hosts.
/// </summary>
public static class LoopbackListeners
{
    public static IReadOnlyList<string> ForPort(int port)
    {
        try
        {
            var rows = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == port)
                .Select(Describe)
                .OrderBy(row => row, StringComparer.Ordinal)
                .ToArray();
            return rows.Length > 0 ? rows : new[] { $"(no process is listening on port {port})" };
        }
        catch (Exception ex)
        {
            return new[] { $"(listener enumeration unavailable: {ex.GetType().Name}: {ex.Message})" };
        }
    }

    private static string Describe(IPEndPoint endpoint)
        => endpoint.AddressFamily == AddressFamily.InterNetworkV6
            ? $"TCP [{endpoint.Address}]:{endpoint.Port} LISTENING"
            : $"TCP {endpoint.Address}:{endpoint.Port} LISTENING";
}

/// <summary>
/// Polls the frontend dev server after a restart. It answers the only
/// question the pipeline cares about - can a user reach the frontend on this
/// host - and records what it saw either way.
/// </summary>
public static class FrontendProbe
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Result for a run that has no frontend origin configured. Treated as
    /// "up" so an installation that does not run a dev server is not blocked
    /// by a check it never opted into.
    /// </summary>
    public static FrontendProbeResult NotConfigured(string? configuredUrl) => new(
        Up: true,
        ReachedUrl: null,
        Port: 0,
        Seconds: 0,
        Attempts: new[]
        {
            new FrontendProbeAttempt(configuredUrl ?? "(unset)", true,
                "no probeable frontend origin configured; frontend check skipped", 0),
        },
        Listeners: Array.Empty<string>());

    /// <summary>
    /// Tries every loopback spelling of <paramref name="configuredUrl"/> in
    /// turn, repeating until one accepts a connection or the budget runs out.
    /// The returned attempt list carries the last outcome per origin, so the
    /// evidence is one row per origin rather than one row per poll.
    /// </summary>
    public static async Task<FrontendProbeResult> WaitForAsync(
        string? configuredUrl,
        TimeSpan budget,
        CancellationToken ct,
        TimeSpan? connectTimeout = null,
        TimeSpan? pollInterval = null)
    {
        var targets = LoopbackProbeTargets.For(configuredUrl);
        if (targets.Count == 0) return NotConfigured(configuredUrl);

        var port = new Uri(targets[0]).Port;
        var connectBudget = connectTimeout ?? DefaultConnectTimeout;
        var interval = pollInterval ?? DefaultPollInterval;
        var started = DateTime.UtcNow;
        var deadline = started + budget;
        var attempts = new Dictionary<string, FrontendProbeAttempt>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            foreach (var target in targets)
            {
                var attempt = await TryConnectAsync(target, connectBudget, ct);
                attempts[target] = attempt;
                if (attempt.Reachable)
                {
                    return new FrontendProbeResult(
                        Up: true,
                        ReachedUrl: target,
                        Port: port,
                        Seconds: Elapsed(started),
                        Attempts: Ordered(targets, attempts),
                        Listeners: LoopbackListeners.ForPort(port));
                }
            }

            if (DateTime.UtcNow >= deadline) break;
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }

        return new FrontendProbeResult(
            Up: false,
            ReachedUrl: null,
            Port: port,
            Seconds: Elapsed(started),
            Attempts: Ordered(targets, attempts),
            Listeners: LoopbackListeners.ForPort(port));
    }

    private static async Task<FrontendProbeAttempt> TryConnectAsync(string target, TimeSpan connectTimeout, CancellationToken ct)
    {
        var uri = new Uri(target);
        var startedTicks = Environment.TickCount64;
        try
        {
            using var tcp = new TcpClient();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(connectTimeout);
            await tcp.ConnectAsync(uri.DnsSafeHost, uri.Port, attemptCts.Token);
            return new FrontendProbeAttempt(target, tcp.Connected,
                tcp.Connected ? "connected" : "connect returned without a socket", Since(startedTicks));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FrontendProbeAttempt(target, false,
                $"no answer within {connectTimeout.TotalSeconds:F0}s", Since(startedTicks));
        }
        catch (Exception ex)
        {
            return new FrontendProbeAttempt(target, false, $"{ex.GetType().Name}: {ex.Message}", Since(startedTicks));
        }
    }

    /// <summary>Attempts in target order, so the report reads top-down.</summary>
    private static IReadOnlyList<FrontendProbeAttempt> Ordered(
        IReadOnlyList<string> targets, IReadOnlyDictionary<string, FrontendProbeAttempt> attempts)
        => targets.Where(attempts.ContainsKey).Select(target => attempts[target]).ToArray();

    private static int Elapsed(DateTime started) => (int)(DateTime.UtcNow - started).TotalSeconds;

    private static int Since(long startedTicks) => (int)(Environment.TickCount64 - startedTicks);

    /// <summary>
    /// Human-readable probe evidence for the run folder and for
    /// <c>summary.md</c>. AGT-2862 deliverable: every URL tried with its
    /// status or error, plus the listener list for the frontend port at the
    /// moment of the verdict.
    /// </summary>
    public static string Render(FrontendProbeResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- frontend probe ---");
        sb.AppendLine($"verdict: {(result.Up ? "UP" : "DOWN")}");
        if (result.ReachedUrl is not null) sb.AppendLine($"reached: {result.ReachedUrl}");
        sb.AppendLine($"elapsed: {result.Seconds}s");
        sb.AppendLine("attempts:");
        foreach (var attempt in result.Attempts)
            sb.AppendLine($"  {attempt.Url} -> {(attempt.Reachable ? "reachable" : "unreachable")} ({attempt.Detail}) after {attempt.DurationMs} ms");
        if (result.Port > 0)
        {
            sb.AppendLine($"listeners on port {result.Port}:");
            foreach (var listener in result.Listeners) sb.AppendLine($"  {listener}");
        }
        return sb.ToString();
    }
}
