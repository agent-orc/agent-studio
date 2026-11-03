using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.WorkContainers;

public sealed record ContainmentProbeCheck(string Kind, bool Passed, string Detail)
{
    public override string ToString() => $"{Kind} {(Passed ? "pass" : "FAIL")} {Detail}";
}

/// <summary>The containment checks a probe attempt reported from inside its container.</summary>
public sealed record ContainmentProbeReport(IReadOnlyList<ContainmentProbeCheck> Checks, IReadOnlyList<string> MissingKinds)
{
    /// <summary>Every probe reports each of these at least once; an absent kind fails the probe.</summary>
    public static readonly string[] RequiredKinds = ["identity", "read", "checkout", "socket", "environment", "egress"];

    public bool Passed => MissingKinds.Count == 0 && Checks.Count > 0 && Checks.All(check => check.Passed);

    public static ContainmentProbeReport Parse(string log)
    {
        var checks = new List<ContainmentProbeCheck>();
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var marker = line.IndexOf("AGENT_PROBE ", StringComparison.Ordinal);
            if (marker < 0) continue;
            var parts = line[(marker + "AGENT_PROBE ".Length)..].Split(' ', 3);
            if (parts.Length < 2 || parts[1] is not ("pass" or "fail")) continue;
            checks.Add(new ContainmentProbeCheck(parts[0], parts[1] == "pass", parts.Length == 3 ? parts[2] : ""));
        }
        var missing = RequiredKinds.Where(kind => checks.All(check => check.Kind != kind)).ToArray();
        return new ContainmentProbeReport(checks, missing);
    }
}

/// <summary>
/// The work command of an SM-11 probe attempt. It runs inside the attempt's
/// container after the exact-SHA checkout and tries what an injected agent
/// would try: read host credential and provider login paths and another
/// project's checkout, find a runtime socket or a secret in its environment,
/// see another repository in its caches, and connect to an address outside
/// the egress allowlist. Each check prints one <c>AGENT_PROBE</c> line.
/// </summary>
public static class ContainmentProbeScript
{
    private static readonly Regex SafePath = new("^/[A-Za-z0-9._/@+-]{0,511}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeHost = new("^[A-Za-z0-9.:-]{1,253}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Host paths that hold runtime access in any supported layout.</summary>
    public static readonly string[] RuntimeSockets =
    [
        "/var/run/docker.sock", "/run/docker.sock", "/run/podman/podman.sock",
        "/run/containerd/containerd.sock", "/var/run/containerd/containerd.sock",
    ];

    public static string Build(IReadOnlyList<string> forbiddenPaths, IReadOnlyList<string> blockedAddresses, string repositoryKey)
    {
        var script = new StringBuilder("""
            set -u
            probe() { echo "AGENT_PROBE $1 $2 $3"; }
            uid="$(id -u)"
            if [ "$uid" -ge FIRST_UID ] && [ "$uid" -lt LAST_UID ]; then probe identity pass "uid=$uid"; else probe identity fail "uid=$uid"; fi
            probe_read() {
              if [ -e "$1" ] || [ -L "$1" ]; then probe read fail "$1"; else probe read pass "$1"; fi
            }
            probe_checkout() {
              other="$(ls -A /cache/repo 2>/dev/null | grep -v -x -e "$1.git" -e "$1.git.tmp.*" -e "$1.git.quarantine.*" | head -n 1)"
              if [ -z "$other" ]; then probe checkout pass "only=$1.git"; else probe checkout fail "other=$other"; fi
            }
            probe_egress() {
              connected="$(curl -s -o /dev/null -w '%{time_connect}' --connect-timeout 4 --max-time 6 "telnet://$1:$2" </dev/null 2>/dev/null || true)"
              case "$connected" in
                ''|0|0.0|0.000000) probe egress pass "$1:$2 blocked" ;;
                *) probe egress fail "$1:$2 connected" ;;
              esac
            }

            """
            .Replace("FIRST_UID", WorkContainerIdentity.FirstAttemptUid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("LAST_UID", (WorkContainerIdentity.FirstAttemptUid + WorkContainerIdentity.AttemptUidCount).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        foreach (var path in forbiddenPaths)
        {
            if (!SafePath.IsMatch(path)) throw new ArgumentException($"Probe path '{path}' is not a plain absolute path.");
            script.Append(CultureInfo.InvariantCulture, $"probe_read '{path}'\n");
        }
        if (!Regex.IsMatch(repositoryKey, "^[a-z0-9][a-z0-9._-]{0,127}$"))
            throw new ArgumentException("Repository key must be a lowercase cache key.");
        script.Append(CultureInfo.InvariantCulture, $"probe_checkout '{repositoryKey}'\n");
        script.Append("socket=none\nfor path in ");
        script.Append(string.Join(' ', RuntimeSockets.Select(path => $"'{path}'")));
        script.Append("""
            ; do [ -e "$path" ] && socket="$path"; done
            if [ "$socket" = none ]; then probe socket pass none; else probe socket fail "$socket"; fi
            leaked="$(env | cut -d= -f1 | grep -E 'TOKEN|SECRET|PASSWORD|PASSWD|API_KEY|PRIVATE_KEY|CREDENTIAL' | grep -v -x -e AGENT_SECRET_DIR -e AGENT_SECRET_REQUIRED | head -n 1)"
            if [ -z "$leaked" ]; then probe environment pass none; else probe environment fail "$leaked"; fi

            """);
        foreach (var address in blockedAddresses)
        {
            var colon = address.LastIndexOf(':');
            if (colon <= 0 || !SafeHost.IsMatch(address[..colon])
                || !int.TryParse(address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw new ArgumentException($"Probe address '{address}' must be host:port.");
            script.Append(CultureInfo.InvariantCulture, $"probe_egress '{address[..colon]}' '{port}'\n");
        }
        script.Append("exit 0\n");
        return script.ToString();
    }
}

public sealed record ContainmentProbeOptions(
    string HostId,
    string RepositoryKey,
    string RepositorySource,
    string ExactSha,
    string Image,
    IReadOnlyList<string> ForbiddenPaths,
    IReadOnlyList<string> BlockedAddresses,
    string ReportDirectory,
    string Role = "coding");

public sealed record ContainmentProbeResult(ContainmentProbeReport Report, WorkContainerReceipt Receipt);

/// <summary>Runs one probe attempt through the work-container executor and records its verdict.</summary>
public static class ContainmentProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<ContainmentProbeResult> RunAsync(
        WorkContainerExecutor executor, ContainmentProbeOptions options, CancellationToken ct)
    {
        var id = "containment-probe-" + Guid.NewGuid().ToString("N")[..12];
        var attempt = new WorkContainerAttempt(options.HostId, options.Role, id, "lease-" + id, 1, 1,
            options.RepositoryKey, options.RepositorySource, options.ExactSha,
            ["/bin/sh", "-c", ContainmentProbeScript.Build(options.ForbiddenPaths, options.BlockedAddresses, options.RepositoryKey)]);
        var receipt = await executor.RunAsync(attempt, options.Image,
            new WorkContainerGrant(1, 1, WorkContainerShadow.ProvisionalGrantHalfCores), ct);
        var log = receipt.LogPath is { } path && File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        var report = ContainmentProbeReport.Parse(log);
        Directory.CreateDirectory(options.ReportDirectory);
        await File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, "containment-probe.json"), JsonSerializer.Serialize(new
        {
            report.Passed,
            receipt.Outcome,
            receipt.CleanupVerified,
            receipt.Image,
            receipt.ShaVerified,
            report.MissingKinds,
            report.Checks,
            options.HostId,
            options.RepositoryKey,
            RecordedAtUtc = DateTime.UtcNow,
        }, Json), ct);
        return new ContainmentProbeResult(report, receipt);
    }
}
