using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRunner;

/// <summary>
/// AGT-3231 (Dossier integrierte-plattform §4-§5, IP-2): the checked-in network
/// model of the work and run zones on a Docker host. The policy file names
/// destinations (control plane, Git hosts, package registries, model APIs,
/// secret store) and zones with their bridge networks. A work zone has no
/// inbound path and an egress allowlist; a run zone may leave the host but
/// never reaches a control-plane or secret-store destination, and each tenant
/// gets its own network. Installation-specific addresses come in as
/// <c>${NAME}</c> references, so the same file serves every host.
/// </summary>
public sealed record ZonePolicyDocument(
    int SchemaVersion,
    string Version,
    IReadOnlyDictionary<string, ZoneDestination> Destinations,
    IReadOnlyList<ZoneDefinition> Zones,
    ZoneVerificationSettings? Verification = null);

/// <param name="Class"><c>egress</c>, <c>control-plane</c> or <c>secret-store</c>.</param>
/// <param name="Ports">Empty or absent means every port and protocol.</param>
public sealed record ZoneDestination(
    string Class,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<int>? Ports = null,
    IReadOnlyList<string>? Protocols = null,
    string? Description = null);

/// <param name="Kind"><c>work</c> or <c>run</c>.</param>
public sealed record ZoneDefinition(
    string Name,
    string Kind,
    IReadOnlyList<ZoneNetwork> Networks,
    ZoneEgress Egress,
    ZoneProbes Verify);

public sealed record ZoneNetwork(string Name, string Bridge, string Subnet, string? Tenant = null);

/// <param name="Default"><c>deny</c> for a work zone, <c>allow</c> for a run zone.</param>
public sealed record ZoneEgress(
    string Default,
    IReadOnlyList<string>? Allow = null,
    IReadOnlyList<string>? Deny = null);

public sealed record ZoneProbes(
    IReadOnlyList<ZoneProbeTarget> Reachable,
    IReadOnlyList<ZoneProbeTarget> Blocked);

/// <summary>
/// A probe names a destination (its first host and first port unless given)
/// or a literal host and port.
/// </summary>
public sealed record ZoneProbeTarget(string? Destination = null, string? Host = null, int? Port = null);

public sealed record ZoneVerificationSettings(string ProbeImage = ZonePolicyRules.DefaultProbeImage, int TimeoutSeconds = 4);

public static class ZoneKinds
{
    public const string Work = "work";
    public const string Run = "run";
}

public static class ZoneDestinationClasses
{
    public const string Egress = "egress";
    public const string ControlPlane = "control-plane";
    public const string SecretStore = "secret-store";
}

/// <summary>A destination after <c>${NAME}</c> expansion and name resolution.</summary>
/// <param name="Hosts">The configured hosts after expansion, for reachable probes.</param>
/// <param name="Addresses">IPv4 CIDRs the rules match, sorted and distinct.</param>
public sealed record ResolvedZoneDestination(
    string Name,
    string Class,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> Protocols);

/// <param name="Host">The name shown in reports.</param>
/// <param name="Address">What the probe connects to: the host name for a
/// reachable probe (so DNS is exercised), a resolved IPv4 address for a blocked
/// probe (so a DNS failure cannot pass as a blocked connection).</param>
public sealed record ResolvedZoneProbe(string Expectation, string Host, string Address, int Port);

public sealed record ResolvedZone(
    string Name,
    string Kind,
    IReadOnlyList<ZoneNetwork> Networks,
    IReadOnlyList<ResolvedZoneDestination> Allowed,
    IReadOnlyList<ResolvedZoneDestination> Denied,
    IReadOnlyList<ResolvedZoneProbe> Probes);

public sealed record ResolvedZonePolicy(
    string Version,
    string PolicySha256,
    IReadOnlyList<ResolvedZone> Zones,
    ZoneVerificationSettings Verification);

public interface IZoneAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsZoneAddressResolver : IZoneAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        => (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).ToArray();
}

public sealed class ZonePolicyException(IReadOnlyList<string> errors)
    : Exception("Zone policy is invalid:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class ZonePolicyParser
{
    public const int SchemaVersion = 1;
    private const int MaxBridgeNameLength = 15;
    private const int MaxMultiportPorts = 15;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Parse and structurally validate a policy document.</summary>
    public static ZonePolicyDocument Parse(string json)
    {
        ZonePolicyDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ZonePolicyDocument>(json, Json);
        }
        catch (JsonException exception)
        {
            throw new ZonePolicyException([$"not a valid zone policy document: {exception.Message}"]);
        }
        if (document is null) throw new ZonePolicyException(["the policy document is empty"]);
        var errors = Validate(document);
        if (errors.Count > 0) throw new ZonePolicyException(errors);
        return document;
    }

    public static string Sha256(string json)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public static IReadOnlyList<string> Validate(ZonePolicyDocument document)
    {
        var errors = new List<string>();
        if (document.SchemaVersion != SchemaVersion)
            errors.Add($"schemaVersion {document.SchemaVersion} is not supported; expected {SchemaVersion}");
        if (string.IsNullOrWhiteSpace(document.Version))
            errors.Add("version is required");
        if (document.Destinations is null || document.Zones is null)
        {
            errors.Add("destinations and zones are required");
            return errors;
        }
        foreach (var (name, destination) in document.Destinations)
            ValidateDestination(name, destination, errors);
        if (document.Zones.Count == 0) errors.Add("at least one zone is required");

        var zoneNames = new HashSet<string>(StringComparer.Ordinal);
        var networkNames = new HashSet<string>(StringComparer.Ordinal);
        var bridges = new HashSet<string>(StringComparer.Ordinal);
        var subnets = new List<(string Network, uint Start, uint End)>();
        foreach (var zone in document.Zones)
        {
            var label = $"zone {zone.Name}";
            if (string.IsNullOrWhiteSpace(zone.Name) || !zoneNames.Add(zone.Name))
                errors.Add($"{label}: a unique zone name is required");
            if (zone.Kind is not (ZoneKinds.Work or ZoneKinds.Run))
                errors.Add($"{label}: kind must be '{ZoneKinds.Work}' or '{ZoneKinds.Run}'");
            if (zone.Networks is not { Count: > 0 })
                errors.Add($"{label}: at least one network is required");
            foreach (var network in zone.Networks ?? [])
            {
                if (string.IsNullOrWhiteSpace(network.Name) || !networkNames.Add(network.Name))
                    errors.Add($"{label}: network name '{network.Name}' must be present and unique");
                if (string.IsNullOrWhiteSpace(network.Bridge) || network.Bridge.Length > MaxBridgeNameLength
                    || !network.Bridge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                    || !bridges.Add(network.Bridge))
                    errors.Add($"{label}: bridge '{network.Bridge}' must be a unique interface name of at most {MaxBridgeNameLength} characters");
                if (!TryParseCidr(network.Subnet, out var start, out var end))
                    errors.Add($"{label}: subnet '{network.Subnet}' is not an IPv4 CIDR");
                else
                {
                    foreach (var other in subnets.Where(other => start <= other.End && other.Start <= end))
                        errors.Add($"{label}: subnet {network.Subnet} of {network.Name} overlaps {other.Network}");
                    subnets.Add((network.Name, start, end));
                }
                if (zone.Kind == ZoneKinds.Run && string.IsNullOrWhiteSpace(network.Tenant))
                    errors.Add($"{label}: run network {network.Name} must name its tenant");
            }
            if (zone.Kind == ZoneKinds.Run)
            {
                var tenants = (zone.Networks ?? []).Select(network => network.Tenant).ToList();
                if (tenants.Distinct(StringComparer.Ordinal).Count() != tenants.Count)
                    errors.Add($"{label}: each tenant needs its own network");
            }
            ValidateEgress(zone, label, document.Destinations, errors);
            ValidateProbes(zone, label, document.Destinations, errors);
        }
        if (document.Verification is { } verification)
        {
            if (string.IsNullOrWhiteSpace(verification.ProbeImage))
                errors.Add("verification.probeImage must not be empty");
            if (verification.TimeoutSeconds is < 1 or > 60)
                errors.Add("verification.timeoutSeconds must be between 1 and 60");
        }
        return errors;
    }

    private static void ValidateDestination(string name, ZoneDestination destination, List<string> errors)
    {
        var label = $"destination {name}";
        if (destination.Class is not (ZoneDestinationClasses.Egress or ZoneDestinationClasses.ControlPlane
            or ZoneDestinationClasses.SecretStore))
            errors.Add($"{label}: class must be egress, control-plane or secret-store");
        if (destination.Hosts is not { Count: > 0 } || destination.Hosts.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{label}: at least one host is required");
        var ports = destination.Ports ?? [];
        if (ports.Any(port => port is < 1 or > 65535))
            errors.Add($"{label}: ports must be between 1 and 65535");
        if (ports.Distinct().Count() > MaxMultiportPorts)
            errors.Add($"{label}: at most {MaxMultiportPorts} ports fit one rule");
        if ((destination.Protocols ?? []).Any(protocol => protocol is not ("tcp" or "udp")))
            errors.Add($"{label}: protocols must be tcp or udp");
        if (ports.Count == 0 && destination.Protocols is { Count: > 0 })
            errors.Add($"{label}: protocols need ports");
    }

    private static void ValidateEgress(
        ZoneDefinition zone, string label, IReadOnlyDictionary<string, ZoneDestination> destinations, List<string> errors)
    {
        if (zone.Egress is null)
        {
            errors.Add($"{label}: egress is required");
            return;
        }
        var allow = zone.Egress.Allow ?? [];
        var deny = zone.Egress.Deny ?? [];
        foreach (var reference in allow.Concat(deny).Where(reference => !destinations.ContainsKey(reference)))
            errors.Add($"{label}: unknown destination '{reference}'");
        if (zone.Kind == ZoneKinds.Work)
        {
            if (zone.Egress.Default != "deny")
                errors.Add($"{label}: a work zone denies egress by default");
            if (deny.Count > 0)
                errors.Add($"{label}: a work zone lists only allowed destinations");
            foreach (var reference in allow.Where(reference => destinations.TryGetValue(reference, out var destination)
                         && destination.Class == ZoneDestinationClasses.SecretStore))
                errors.Add($"{label}: a work zone must not reach the secret store '{reference}'");
        }
        else if (zone.Kind == ZoneKinds.Run)
        {
            if (zone.Egress.Default != "allow")
                errors.Add($"{label}: a run zone allows declared egress and denies the listed destinations");
            if (allow.Count > 0)
                errors.Add($"{label}: a run zone lists only denied destinations");
            foreach (var (name, destination) in destinations)
            {
                if (destination.Class is ZoneDestinationClasses.ControlPlane or ZoneDestinationClasses.SecretStore
                    && !deny.Contains(name, StringComparer.Ordinal))
                    errors.Add($"{label}: a run zone must deny {destination.Class} destination '{name}'");
            }
        }
    }

    private static void ValidateProbes(
        ZoneDefinition zone, string label, IReadOnlyDictionary<string, ZoneDestination> destinations, List<string> errors)
    {
        if (zone.Verify is null || zone.Verify.Reachable is not { Count: > 0 } || zone.Verify.Blocked is not { Count: > 0 })
        {
            errors.Add($"{label}: verify needs at least one reachable and one blocked probe");
            return;
        }
        foreach (var probe in zone.Verify.Reachable.Concat(zone.Verify.Blocked))
        {
            if (probe.Destination is null && probe.Host is null)
            {
                errors.Add($"{label}: a probe names a destination, a host, or a host of a destination");
                continue;
            }
            if (probe.Port is { } port && port is < 1 or > 65535)
                errors.Add($"{label}: probe port {port} is out of range");
            if (probe.Destination is { } reference)
            {
                if (!destinations.TryGetValue(reference, out var destination))
                    errors.Add($"{label}: probe names unknown destination '{reference}'");
                else if (probe.Port is null && (destination.Ports ?? []).Count == 0)
                    errors.Add($"{label}: probe of '{reference}' needs a port because the destination has none");
            }
            else if (probe.Port is null)
                errors.Add($"{label}: probe of host '{probe.Host}' needs a port");
        }
    }

    /// <summary>
    /// Expand <c>${NAME}</c> references and resolve host names to IPv4
    /// addresses. Resolution happens on the host at apply time; the rendered
    /// rules record the addresses that were current then.
    /// </summary>
    public static async Task<ResolvedZonePolicy> ResolveAsync(
        ZonePolicyDocument document,
        string policySha256,
        IReadOnlyDictionary<string, string> environment,
        IZoneAddressResolver resolver,
        CancellationToken ct,
        IReadOnlyCollection<string>? zoneNames = null)
    {
        var errors = new List<string>();
        var selected = document.Zones
            .Where(zone => zoneNames is not { Count: > 0 } || zoneNames.Contains(zone.Name, StringComparer.Ordinal))
            .ToList();
        foreach (var unknown in (zoneNames ?? []).Where(name => document.Zones.All(zone => zone.Name != name)))
            errors.Add($"zone '{unknown}' is not in the policy");
        // A work host never needs the run zone's secret-store address and the
        // reverse, so only destinations the selected zones use are resolved.
        var referenced = selected
            .SelectMany(zone => (zone.Egress.Allow ?? []).Concat(zone.Egress.Deny ?? [])
                .Concat(zone.Verify.Reachable.Concat(zone.Verify.Blocked)
                    .Select(probe => probe.Destination).OfType<string>()))
            .ToHashSet(StringComparer.Ordinal);
        var resolved = new Dictionary<string, ResolvedZoneDestination>(StringComparer.Ordinal);
        foreach (var (name, destination) in document.Destinations
                     .Where(item => referenced.Contains(item.Key))
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var hosts = new List<string>();
            foreach (var entry in destination.Hosts)
                hosts.AddRange(Expand(entry, $"destination {name}", environment, errors));
            var addresses = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var host in hosts)
            {
                if (TryNormalizeCidr(host, out var cidr))
                {
                    addresses.Add(cidr);
                    continue;
                }
                if (host.Contains('/'))
                {
                    errors.Add($"destination {name}: '{host}' is not a canonical IPv4 CIDR");
                    continue;
                }
                if (IPAddress.TryParse(host, out _) || host.Contains(':'))
                {
                    errors.Add($"destination {name}: '{host}' is not an IPv4 address; zone networks carry IPv4 only");
                    continue;
                }
                IReadOnlyList<IPAddress> found;
                try
                {
                    found = await resolver.ResolveAsync(host, ct);
                }
                catch (SocketException exception)
                {
                    errors.Add($"destination {name}: '{host}' does not resolve ({exception.SocketErrorCode})");
                    continue;
                }
                var ipv4 = found.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToList();
                if (ipv4.Count == 0) errors.Add($"destination {name}: '{host}' has no IPv4 address");
                foreach (var address in ipv4) addresses.Add($"{address}/32");
            }
            var ports = (destination.Ports ?? []).Distinct().ToList();
            var protocols = ports.Count == 0
                ? new List<string>()
                : (destination.Protocols is { Count: > 0 } configured ? configured.Distinct().ToList() : ["tcp"]);
            resolved[name] = new ResolvedZoneDestination(
                name, destination.Class, hosts, addresses.OrderBy(SortKey).ToList(), ports, protocols);
        }

        var zones = new List<ResolvedZone>();
        foreach (var zone in selected)
        {
            var allow = (zone.Egress.Allow ?? []).Select(name => resolved[name]).ToList();
            var deny = (zone.Egress.Deny ?? []).Select(name => resolved[name]).ToList();
            var probes = new List<ResolvedZoneProbe>();
            foreach (var probe in zone.Verify.Reachable)
            {
                var (host, port) = probe.Destination is { } reference
                    ? (probe.Host ?? resolved[reference].Hosts.FirstOrDefault(), probe.Port ?? resolved[reference].Ports.FirstOrDefault())
                    : (probe.Host, probe.Port ?? 0);
                if (host is null)
                {
                    errors.Add($"zone {zone.Name}: reachable probe of '{probe.Destination}' has no host");
                    continue;
                }
                if (TryNormalizeCidr(host, out var cidr) && !cidr.EndsWith("/32", StringComparison.Ordinal))
                {
                    errors.Add($"zone {zone.Name}: reachable probe needs a host, not the range {cidr}");
                    continue;
                }
                probes.Add(new ResolvedZoneProbe(ZoneProbeExpectations.Reachable, host, host.Split('/')[0], port));
            }
            foreach (var probe in zone.Verify.Blocked)
            {
                if (probe is { Destination: { } reference, Host: null })
                {
                    var destination = resolved[reference];
                    var port = probe.Port ?? destination.Ports.FirstOrDefault();
                    foreach (var address in destination.Addresses)
                        probes.Add(new ResolvedZoneProbe(ZoneProbeExpectations.Blocked, reference, FirstHost(address), port));
                    continue;
                }
                var target = probe.Host!;
                var blockedPort = probe.Port ?? resolved[probe.Destination!].Ports[0];
                if (TryNormalizeCidr(target, out var literal))
                {
                    probes.Add(new ResolvedZoneProbe(ZoneProbeExpectations.Blocked, target, FirstHost(literal), blockedPort));
                    continue;
                }
                IReadOnlyList<IPAddress> found;
                try { found = await resolver.ResolveAsync(target, ct); }
                catch (SocketException exception)
                {
                    errors.Add($"zone {zone.Name}: blocked probe host '{target}' does not resolve ({exception.SocketErrorCode})");
                    continue;
                }
                var first = found.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
                if (first is null) errors.Add($"zone {zone.Name}: blocked probe host '{target}' has no IPv4 address");
                else probes.Add(new ResolvedZoneProbe(ZoneProbeExpectations.Blocked, target, first.ToString(), blockedPort));
            }
            zones.Add(new ResolvedZone(zone.Name, zone.Kind, zone.Networks, allow, deny, probes));
        }
        if (errors.Count > 0) throw new ZonePolicyException(errors);
        return new ResolvedZonePolicy(document.Version, policySha256, zones,
            document.Verification ?? new ZoneVerificationSettings());
    }

    private static IEnumerable<string> Expand(
        string entry, string label, IReadOnlyDictionary<string, string> environment, List<string> errors)
    {
        var trimmed = entry.Trim();
        if (!trimmed.StartsWith("${", StringComparison.Ordinal))
            return [trimmed];
        if (!trimmed.EndsWith('}') || trimmed.Length < 4)
        {
            errors.Add($"{label}: '{entry}' is not a whole-value ${{NAME}} reference");
            return [];
        }
        var name = trimmed[2..^1];
        if (!environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label}: ${{{name}}} is not set; add it to the host's zone policy environment file");
            return [];
        }
        return value.Split([',', ' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static bool TryParseCidr(string? value, out uint start, out uint end)
    {
        start = end = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32) return false;
        if (!IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        var raw = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        if ((raw & ~mask) != 0) return false;
        start = raw;
        end = raw | ~mask;
        return true;
    }

    /// <summary>An IPv4 literal becomes <c>a.b.c.d/32</c>; a CIDR must be canonical.</summary>
    internal static bool TryNormalizeCidr(string value, out string cidr)
    {
        cidr = string.Empty;
        var candidate = value.Contains('/') ? value : value + "/32";
        if (!TryParseCidr(candidate, out _, out _)) return false;
        cidr = candidate;
        return true;
    }

    /// <summary>The address a blocked probe connects to inside a CIDR.</summary>
    private static string FirstHost(string cidr)
    {
        TryParseCidr(cidr, out var start, out var end);
        var host = end > start + 1 ? start + 1 : start;
        return new IPAddress([(byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host]).ToString();
    }

    private static (uint, int) SortKey(string cidr)
    {
        TryParseCidr(cidr, out var start, out _);
        return (start, int.Parse(cidr.Split('/')[1]));
    }
}

public static class ZoneProbeExpectations
{
    public const string Reachable = "reachable";
    public const string Blocked = "blocked";
}
