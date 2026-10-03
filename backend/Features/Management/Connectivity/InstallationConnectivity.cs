using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Management;

/// <summary>
/// The one installation-owned connectivity record (Dossier AGT-W63, I04, D4 option A).
/// Server origin, browser origin, private listeners and optional RunnerLinks are all
/// generated from this record; no other file selects them.
/// </summary>
public sealed record InstallationConnectivityManifest(
    int SchemaVersion,
    string InstallationId,
    int Revision,
    string Mode,
    string Package,
    string ServerOrigin,
    string BrowserOrigin,
    string? CaBundleFile,
    string? LinkOwner,
    IReadOnlyList<string> EnabledListeners,
    IReadOnlyList<ConnectivityPortOverride>? PortOverrides,
    IReadOnlyList<ConnectivityRunnerRoute> Runners)
{
    public static InstallationConnectivityManifest Parse(string json)
        => JsonSerializer.Deserialize<InstallationConnectivityManifest>(json, ConnectivityPolicy.Json)
           ?? throw new InvalidOperationException("Connectivity manifest is empty.");
}

public sealed record ConnectivityPortOverride(string Listener, int Port);

/// <summary>One runner's selected route. ReverseSsh only exists for the bounded transition.</summary>
public sealed record ConnectivityRunnerRoute(
    string RunnerId,
    string Route,
    string? SshTarget,
    string? KnownHostsFile,
    int? RemotePort,
    IReadOnlyList<int>? BackoffSeconds,
    int? HeartbeatTimeoutSeconds);

public static class ConnectivityModes
{
    public const string OneBox = "one-box";
    public const string ReverseSshTransition = "reverse-ssh-transition";
    public const string WireGuardDirect = "wireguard-direct";
    public static readonly string[] All = [OneBox, ReverseSshTransition, WireGuardDirect];
}

public static class ConnectivityPackages
{
    /// <summary>backend/Host: the only process that registers LinkSupervisor.</summary>
    public const string LegacyBackend = "legacy-backend";
    /// <summary>task-server/: standalone plane; registers no LinkSupervisor.</summary>
    public const string StandaloneTaskServer = "standalone-task-server";
    /// <summary>deploy/compose/control-plane: standalone Task Server behind the WireGuard edge.</summary>
    public const string ControlPlaneCompose = "control-plane-compose";
    public static readonly string[] All = [LegacyBackend, StandaloneTaskServer, ControlPlaneCompose];
}

public static class ConnectivityLinkOwners
{
    public const string None = "none";
    public const string TunnelKeeper = "tunnel-keeper";
    public const string LinkSupervisor = "link-supervisor";
}

public static class ConnectivityRoutes
{
    public const string Loopback = "loopback";
    public const string ReverseSsh = "reverse-ssh";
    public const string PrivateHttps = "private-https";
}

public sealed record ConnectivityListener(
    string Name,
    int DefaultPort,
    string Owner,
    string Bind,
    bool MayBePublished,
    string Purpose);

public sealed record ResolvedListener(string Name, int Port, string Owner, string Bind, string Purpose);

public sealed record ConnectivityFinding(string Code, string Subject, string Message, string Remediation);

public sealed record ResolvedConnectivity(
    string InstallationId,
    int Revision,
    string Mode,
    string Package,
    string ServerOrigin,
    string BrowserOrigin,
    string LinkOwner,
    IReadOnlyList<ResolvedListener> Listeners,
    IReadOnlyList<RunnerLinkOptionsSeed> RunnerLinks);

/// <summary>Configuration seed for one RunnerLinks entry. Secrets never appear here.</summary>
public sealed record RunnerLinkOptionsSeed(
    string RunnerId,
    string SshTarget,
    string KnownHostsFile,
    int RemotePort,
    int LocalPort,
    IReadOnlyList<int> BackoffSeconds,
    int HeartbeatTimeoutSeconds);

/// <summary>Pure resolution and validation. Effects (file reads, boot) stay in the caller.</summary>
public static class ConnectivityPolicy
{
    public const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Source defaults. They are a catalogue, not an instruction to open every port.</summary>
    public static readonly IReadOnlyList<ConnectivityListener> Catalogue =
    [
        new("legacy-api", 5031, "backend/Host", "127.0.0.1", false,
            "Legacy networked API; the reverse SSH link forwards to it."),
        new("standalone-task-server", 5071, "task-server", "127.0.0.1", false,
            "Standalone Task Server; Compose keeps it on the internal network behind the edge."),
        new("studio-bff", 5072, "studio BFF", "127.0.0.1", false,
            "Studio backend-for-frontend; holds service credentials, never the browser."),
        new("browser", 4011, "web edge", "127.0.0.1", true,
            "The one same-origin browser edge."),
        new("runner-tunnel", 15031, "sshd on the runner host", "127.0.0.1", false,
            "Runner-side loopback end of the supervised reverse SSH link."),
        new("local-updater", 5039, "update-service", "127.0.0.1", false,
            "Checkout dev/stable updater; never a fleet updater."),
        new("private-edge", 443, "control-plane edge (Caddy)", "wireguard", false,
            "Private TLS edge bound to the WireGuard address only."),
    ];

    private static readonly int[] DefaultBackoff = [5, 10, 30, 60, 120];

    /// <summary>Listeners each mode may enable. Anything else is a duplicated or exposed listener.</summary>
    public static IReadOnlyList<string> AllowedListeners(string mode, string package) => (mode, package) switch
    {
        (ConnectivityModes.WireGuardDirect, _) => ["standalone-task-server", "private-edge", "studio-bff", "browser"],
        (ConnectivityModes.ReverseSshTransition, _) => ["legacy-api", "runner-tunnel", "browser", "local-updater"],
        (_, ConnectivityPackages.LegacyBackend) => ["legacy-api", "browser", "local-updater"],
        _ => ["standalone-task-server", "studio-bff", "browser", "local-updater"],
    };

    /// <summary>Which process supervises reverse SSH links in a package. Proven from the hosts' service registration.</summary>
    public static bool PackageHostsLinkSupervisor(string package) => package == ConnectivityPackages.LegacyBackend;

    public static IReadOnlyList<ConnectivityFinding> Validate(InstallationConnectivityManifest manifest)
    {
        var findings = new List<ConnectivityFinding>();
        void Add(string code, string subject, string message, string remediation)
            => findings.Add(new(code, subject, message, remediation));

        if (manifest.SchemaVersion != SchemaVersion)
            Add("schema-version", "manifest", $"Schema version {manifest.SchemaVersion} is not {SchemaVersion}.",
                "Regenerate the manifest from deploy/connectivity/ for this release.");
        if (string.IsNullOrWhiteSpace(manifest.InstallationId) || manifest.Revision < 1)
            Add("identity", "manifest", "installationId and a positive revision are required.",
                "Set installationId once at install time and increase revision on every edit.");
        if (!ConnectivityModes.All.Contains(manifest.Mode))
            Add("mode", "manifest", $"Unknown mode '{manifest.Mode}'.", "Use one-box, reverse-ssh-transition or wireguard-direct.");
        if (!ConnectivityPackages.All.Contains(manifest.Package))
            Add("package", "manifest", $"Unknown package '{manifest.Package}'.",
                "Use legacy-backend, standalone-task-server or control-plane-compose.");

        var owner = manifest.LinkOwner ?? ConnectivityLinkOwners.None;
        var reverse = manifest.Runners.Where(r => r.Route == ConnectivityRoutes.ReverseSsh).ToArray();
        if (owner == ConnectivityLinkOwners.LinkSupervisor && !PackageHostsLinkSupervisor(manifest.Package))
            Add("link-owner-package", "linkOwner",
                $"Package '{manifest.Package}' does not register LinkSupervisor; only backend/Host does.",
                "Run the link from the legacy backend host, or use private-https routes for this package.");
        if (reverse.Length > 0 && owner == ConnectivityLinkOwners.None)
            Add("link-owner-missing", "linkOwner", "Reverse SSH routes need exactly one declared owner.",
                "Set linkOwner to tunnel-keeper (before adoption) or link-supervisor (after adoption).");
        if (reverse.Length == 0 && owner != ConnectivityLinkOwners.None)
            Add("link-owner-unused", "linkOwner", $"linkOwner '{owner}' is declared but no runner uses reverse-ssh.",
                "Set linkOwner to none and retire the old owner only after the agreed soak.");

        CheckOrigins(manifest, Add);
        CheckListeners(manifest, Add);
        CheckRunners(manifest, Add);
        return findings;
    }

    private static void CheckOrigins(InstallationConnectivityManifest manifest, Action<string, string, string, string> add)
    {
        var server = Uri.TryCreate(manifest.ServerOrigin, UriKind.Absolute, out var s) ? s : null;
        var browser = Uri.TryCreate(manifest.BrowserOrigin, UriKind.Absolute, out var b) ? b : null;
        if (server is null || server.AbsolutePath != "/")
            add("server-origin", "serverOrigin", "serverOrigin must be an absolute origin without a path.",
                "Use scheme://host:port only.");
        if (browser is null || browser.AbsolutePath != "/")
            add("browser-origin", "browserOrigin", "browserOrigin must be an absolute origin without a path.",
                "Use scheme://host:port only.");
        if (server is null || browser is null) return;

        if (manifest.Mode == ConnectivityModes.WireGuardDirect)
        {
            if (server.Scheme != Uri.UriSchemeHttps)
                add("private-tls", "serverOrigin", "Direct WireGuard mode requires an HTTPS server origin.",
                    "Point serverOrigin at the private edge, for example https://task-server.wg.internal.");
            if (server.IsLoopback || server.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                add("private-dns", "serverOrigin", "Direct WireGuard mode requires a private DNS name, not an address.",
                    "Publish the private name in WireGuard DNS and issue the edge certificate for it.");
            if (string.IsNullOrWhiteSpace(manifest.CaBundleFile))
                add("private-trust", "caBundleFile", "Direct WireGuard mode requires a pinned private CA bundle.",
                    "Set caBundleFile to the private CA certificate distributed to every runner.");
        }
        else if (!server.IsLoopback)
        {
            add("server-origin-scope", "serverOrigin",
                "Without WireGuard the server origin must be loopback; the reverse link carries remote traffic.",
                "Use http://127.0.0.1:<port> or switch the mode to wireguard-direct.");
        }
    }

    private static void CheckListeners(InstallationConnectivityManifest manifest, Action<string, string, string, string> add)
    {
        var allowed = AllowedListeners(manifest.Mode, manifest.Package);
        foreach (var name in manifest.EnabledListeners)
        {
            if (Catalogue.All(item => item.Name != name))
                add("listener-unknown", name, $"Unknown listener '{name}'.", "Use a listener name from the catalogue.");
            else if (!allowed.Contains(name))
                add("listener-not-allowed", name, $"Listener '{name}' is not part of mode '{manifest.Mode}'.",
                    "Disable it; each mode enables only the listeners its route needs.");
        }
        if (manifest.EnabledListeners.Distinct().Count() != manifest.EnabledListeners.Count)
            add("listener-duplicated", "enabledListeners", "A listener is enabled twice.", "List each listener once.");

        var ports = Resolve(manifest.EnabledListeners, manifest.PortOverrides).GroupBy(item => item.Port);
        foreach (var clash in ports.Where(group => group.Count() > 1))
            add("port-duplicated", clash.Key.ToString(),
                $"Port {clash.Key} is claimed by {string.Join(", ", clash.Select(item => item.Name))}.",
                "Give every listener its own port in portOverrides.");
        foreach (var item in manifest.PortOverrides ?? [])
            if (item.Port is < 1 or > 65535)
                add("port-range", item.Listener, $"Port {item.Port} is out of range.", "Use 1 to 65535.");
    }

    private static void CheckRunners(
        InstallationConnectivityManifest manifest, Action<string, string, string, string> add)
    {
        var ids = manifest.Runners.Select(r => r.RunnerId).ToArray();
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            add("runner-duplicated", "runners", "A runner has two routes.", "Keep one route per runner.");
        var remotePorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runner in manifest.Runners)
        {
            var routeAllowed = manifest.Mode switch
            {
                ConnectivityModes.WireGuardDirect => runner.Route == ConnectivityRoutes.PrivateHttps,
                ConnectivityModes.ReverseSshTransition => runner.Route is ConnectivityRoutes.ReverseSsh or ConnectivityRoutes.Loopback,
                _ => runner.Route == ConnectivityRoutes.Loopback,
            };
            if (!routeAllowed)
                add("route-mode", runner.RunnerId, $"Route '{runner.Route}' is not valid in mode '{manifest.Mode}'.",
                    manifest.Mode == ConnectivityModes.WireGuardDirect
                        ? "Retire the RunnerLinks forward after the soak and point the runner at the private origin."
                        : "Choose loopback for the same box or reverse-ssh for a transition host.");
            if (runner.Route != ConnectivityRoutes.ReverseSsh) continue;
            if (string.IsNullOrWhiteSpace(runner.SshTarget))
                add("ssh-target", runner.RunnerId, "Reverse SSH needs sshTarget.", "Set the runner's SSH alias or user@host.");
            if (string.IsNullOrWhiteSpace(runner.KnownHostsFile))
                add("host-key-pin", runner.RunnerId, "Reverse SSH needs a pinned known_hosts file.",
                    "Record the runner host key with ssh-keyscan, verify its fingerprint out of band, and set knownHostsFile.");
            var backoff = runner.BackoffSeconds ?? DefaultBackoff;
            if (backoff.Count == 0 || backoff.Any(value => value is < 1 or > 3600))
                add("backoff", runner.RunnerId, "Backoff must be a non-empty list of 1 to 3600 seconds.",
                    "Use the default 5, 10, 30, 60, 120.");
            var remote = runner.RemotePort ?? PortOf("runner-tunnel", manifest.PortOverrides);
            if (!remotePorts.Add($"{runner.SshTarget}:{remote}"))
                add("tunnel-duplicated", runner.RunnerId, $"Two links bind {runner.SshTarget}:{remote}.",
                    "Each runner host gets one reverse listener.");
        }
    }

    private static int PortOf(string listener, IReadOnlyList<ConnectivityPortOverride>? overrides)
        => overrides?.LastOrDefault(item => item.Listener == listener)?.Port
           ?? Catalogue.First(item => item.Name == listener).DefaultPort;

    private static IReadOnlyList<ResolvedListener> Resolve(
        IEnumerable<string> names, IReadOnlyList<ConnectivityPortOverride>? overrides)
        => names.Select(name => Catalogue.FirstOrDefault(item => item.Name == name)).OfType<ConnectivityListener>()
            .Select(item => new ResolvedListener(item.Name, PortOf(item.Name, overrides), item.Owner, item.Bind, item.Purpose))
            .ToArray();

    /// <summary>Generates every connectivity value from the one record. Throws when the record is invalid.</summary>
    public static ResolvedConnectivity Resolve(InstallationConnectivityManifest manifest)
    {
        var findings = Validate(manifest);
        if (findings.Count > 0)
            throw new InvalidOperationException("Connectivity manifest is invalid: "
                + string.Join("; ", findings.Select(item => $"{item.Code} ({item.Subject}): {item.Message}")));
        var owner = manifest.LinkOwner ?? ConnectivityLinkOwners.None;
        var localPort = PortOf("legacy-api", manifest.PortOverrides);
        var links = owner != ConnectivityLinkOwners.LinkSupervisor ? [] : manifest.Runners
            .Where(r => r.Route == ConnectivityRoutes.ReverseSsh)
            .Select(r => new RunnerLinkOptionsSeed(
                r.RunnerId, r.SshTarget!, r.KnownHostsFile!,
                r.RemotePort ?? PortOf("runner-tunnel", manifest.PortOverrides), localPort,
                r.BackoffSeconds ?? DefaultBackoff, r.HeartbeatTimeoutSeconds ?? 90))
            .ToArray();
        return new(manifest.InstallationId, manifest.Revision, manifest.Mode, manifest.Package,
            manifest.ServerOrigin.TrimEnd('/'), manifest.BrowserOrigin.TrimEnd('/'), owner,
            Resolve(manifest.EnabledListeners, manifest.PortOverrides), links);
    }

    /// <summary>The URL a runner must use to reach the bus on its selected route.</summary>
    public static string RunnerServerUrl(ResolvedConnectivity resolved, ConnectivityRunnerRoute runner) => runner.Route switch
    {
        ConnectivityRoutes.ReverseSsh =>
            $"http://127.0.0.1:{runner.RemotePort ?? resolved.Listeners.FirstOrDefault(l => l.Name == "runner-tunnel")?.Port ?? 15031}",
        _ => resolved.ServerOrigin,
    };

    /// <summary>RunnerLinks configuration keys for backend/Host. The manifest is their only source.</summary>
    public static IReadOnlyDictionary<string, string?> RunnerLinksConfiguration(ResolvedConnectivity resolved)
    {
        var values = new Dictionary<string, string?>();
        for (var index = 0; index < resolved.RunnerLinks.Count; index++)
        {
            var link = resolved.RunnerLinks[index];
            var prefix = $"RunnerLinks:{index}:";
            values[prefix + "Enabled"] = "true";
            values[prefix + "RunnerId"] = link.RunnerId;
            values[prefix + "Kind"] = "ssh-reverse";
            values[prefix + "SshTarget"] = link.SshTarget;
            values[prefix + "KnownHostsFile"] = link.KnownHostsFile;
            values[prefix + "RemotePort"] = link.RemotePort.ToString();
            values[prefix + "LocalPort"] = link.LocalPort.ToString();
            values[prefix + "HeartbeatTimeoutSeconds"] = link.HeartbeatTimeoutSeconds.ToString();
            for (var step = 0; step < link.BackoffSeconds.Count; step++)
                values[prefix + $"BackoffSeconds:{step}"] = link.BackoffSeconds[step].ToString();
        }
        return values;
    }

    /// <summary>
    /// Classifies what a runner last advertised against the current record. A stale
    /// advertisement (older revision or another server URL) is not fresh reachability.
    /// </summary>
    public static ConnectivityFinding? AdvertisementFinding(
        ResolvedConnectivity resolved, ConnectivityRunnerRoute runner, int advertisedRevision,
        string advertisedServerUrl, DateTime advertisedAt, DateTime now, TimeSpan freshness)
    {
        var expected = RunnerServerUrl(resolved, runner);
        if (advertisedRevision < resolved.Revision)
            return new("advertisement-stale-revision", runner.RunnerId,
                $"Runner advertised connectivity revision {advertisedRevision}; current is {resolved.Revision}.",
                $"Set the runner's server URL to {expected}, restart its service and wait for a fresh advertisement.");
        if (!string.Equals(advertisedServerUrl.TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase))
            return new("advertisement-route-mismatch", runner.RunnerId,
                $"Runner advertised {advertisedServerUrl}; the record selects {expected}.",
                $"Set the runner's server URL to {expected}; reconcile active attempts before retiring the old route.");
        if (now - advertisedAt > freshness)
            return new("advertisement-expired", runner.RunnerId,
                $"Last advertisement is {(int)(now - advertisedAt).TotalSeconds}s old.",
                "Run the host-side health probe; on reverse-ssh check the link state, on private-https check DNS, TLS and WireGuard.");
        return null;
    }

    /// <summary>Certificate checks for the private edge, evaluated by the host-side probe.</summary>
    public static ConnectivityFinding? CertificateFinding(
        string host, DateTime notAfter, IReadOnlyList<string> subjectNames, bool chainsToPinnedCa, DateTime now, TimeSpan warning)
    {
        if (!chainsToPinnedCa)
            return new("tls-untrusted", host, "Edge certificate does not chain to the pinned private CA.",
                "Reissue the edge certificate from the private CA, or redistribute the correct caBundleFile.");
        if (!subjectNames.Any(name => string.Equals(name, host, StringComparison.OrdinalIgnoreCase)))
            return new("tls-name-mismatch", host, "Edge certificate does not name the server origin host.",
                "Reissue the certificate for the private DNS name in serverOrigin.");
        if (notAfter <= now)
            return new("tls-expired", host, $"Edge certificate expired at {notAfter:O}.",
                "Renew the edge certificate (restart the edge after replacing it) and rerun the probe; do not disable verification.");
        if (notAfter - now <= warning)
            return new("tls-expiring", host, $"Edge certificate expires at {notAfter:O}.",
                "Renew the edge certificate before expiry.");
        return null;
    }
}
