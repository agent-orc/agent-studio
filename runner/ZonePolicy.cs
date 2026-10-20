using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>
/// AGT-3231 (Dossier integrierte-plattform §4-§5, IP-2): the checked-in network
/// model of the work and run zones on a Docker host. The policy file names
/// destinations (control plane, Git hosts, package registries, model APIs,
/// secret store) and zones with their bridge networks. A work zone has no
/// inbound path and an egress allowlist; a run zone can reach public egress
/// but denies private, ZeroTier, control-plane and secret-store destinations.
/// Each tenant gets its own network and audited private exceptions.
/// Installation-specific addresses come in as
/// <c>${NAME}</c> references, so the same file serves every host.
/// </summary>
public sealed record ZonePolicyDocument(
    int SchemaVersion,
    string Version,
    IReadOnlyDictionary<string, ZoneDestination> Destinations,
    IReadOnlyList<ZoneDefinition> Zones,
    ZoneVerificationSettings? Verification = null,
    ZoneResolverSettings? Resolver = null);

/// <summary>
/// The host-side resolver of the work zones. Work containers resolve through
/// it (<c>--dns</c> the bridge gateway); every answer for a name a work zone
/// allows lands in that destination's ipset, so the allowed set follows what
/// the containers actually resolve.
/// </summary>
/// <param name="Upstreams">IPv4 resolvers the zone resolver forwards to.</param>
/// <param name="SetTimeoutSeconds">How long an address stays allowed after the
/// last answer that contained it.</param>
public sealed record ZoneResolverSettings(IReadOnlyList<string> Upstreams, int SetTimeoutSeconds = 86400);

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

public sealed record ZoneNetwork(string Name, string Bridge, string Subnet, string? Tenant = null,
    IReadOnlyList<ZonePrivateAllowance>? PrivateAllow = null);

/// <summary>An audited, tenant-scoped exception to the run zone's private egress deny.</summary>
public sealed record ZonePrivateAllowance(string Cidr, string Reason);

/// <param name="Default"><c>deny</c> for a work zone, <c>allow</c> for a run zone.</param>
public sealed record ZoneEgress(
    string Default,
    IReadOnlyList<string>? Allow = null,
    IReadOnlyList<string>? Deny = null,
    IReadOnlyList<string>? ZeroTierRanges = null);

public sealed record ZoneProbes(
    IReadOnlyList<ZoneProbeTarget> Reachable,
    IReadOnlyList<ZoneProbeTarget> Blocked);

/// <summary>
/// A probe names a destination (its first host and first port unless given)
/// or a literal host and port.
/// </summary>
public sealed record ZoneProbeTarget(string? Destination = null, string? Host = null, int? Port = null,
    bool RequireControl = true);

/// <param name="ProbeImage">Pinned by digest, so every host probes with the same binary.</param>
/// <param name="ControlNetwork">The non-zone Docker network the positive control of
/// each blocked probe runs from.</param>
public sealed record ZoneVerificationSettings(
    string ProbeImage = ZonePolicyRules.DefaultProbeImage,
    int TimeoutSeconds = 4,
    string ControlNetwork = ZonePolicyRules.DefaultControlNetwork);

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
/// <param name="Literals">The IPv4 addresses and CIDRs listed literally; the rules
/// match them as they are.</param>
/// <param name="Names">The host names. A work zone allows them through the
/// destination's ipset, which the zone resolver fills.</param>
/// <param name="Addresses">What a denial and a blocked probe match: the literals
/// plus, for a destination that a zone denies or a blocked probe names, the
/// addresses its names resolved to at apply time. Sorted and distinct.</param>
public sealed record ResolvedZoneDestination(
    string Name,
    string Class,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Literals,
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> Protocols)
{
    /// <summary>The ipset a work zone matches the names by, or null without names.</summary>
    public string? IpSet => Names.Count > 0 ? ZonePolicyRules.IpSetName(Name) : null;
}

/// <param name="Host">The name shown in reports.</param>
/// <param name="Address">What the probe connects to: the host name for a
/// reachable probe (so DNS is exercised), a resolved IPv4 address for a blocked
/// probe (so a DNS failure cannot pass as a blocked connection).</param>
/// <param name="Unresolved">Why a blocked probe's host has no address. The
/// probe then fails during verification instead of stopping the apply.</param>
public sealed record ResolvedZoneProbe(string Expectation, string Host, string Address, int Port,
    string? Unresolved = null, bool RequireControl = true);

/// <summary>
/// A denied name that did not resolve at apply time. Its denial is missing
/// from the rules; every other rule still loads, and the verification records
/// the problem as a failed check.
/// </summary>
public sealed record ZoneResolutionProblem(string Destination, string Detail);

public sealed record ResolvedZone(
    string Name,
    string Kind,
    IReadOnlyList<ZoneNetwork> Networks,
    IReadOnlyList<ResolvedZoneDestination> Allowed,
    IReadOnlyList<ResolvedZoneDestination> Denied,
    IReadOnlyList<ResolvedZoneProbe> Probes,
    IReadOnlyList<string>? ZeroTierRanges = null);

/// <param name="Upstreams">IPv4 addresses after expansion.</param>
public sealed record ResolvedZoneResolver(IReadOnlyList<string> Upstreams, int SetTimeoutSeconds);

/// <param name="Resolver">Null when no work zone is selected.</param>
/// <param name="Problems">Denied names that did not resolve; null or empty when all did.</param>
public sealed record ResolvedZonePolicy(
    string Version,
    string PolicySha256,
    IReadOnlyList<ResolvedZone> Zones,
    ZoneVerificationSettings Verification,
    ResolvedZoneResolver? Resolver = null,
    IReadOnlyList<ZoneResolutionProblem>? Problems = null);

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
    private const int MaxSetTimeoutSeconds = 2147483;

    /// <summary>
    /// Zone, network, tenant and destination names go verbatim into the
    /// <c>#</c> lines and <c>--comment</c> values of the iptables-restore
    /// payload, so they carry no quote, space or line break.
    /// </summary>
    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]{0,47}\\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The version goes verbatim into the <c>#</c> lines of both payloads and
    /// the resolver configuration; a line break would inject restore lines.
    /// </summary>
    private static readonly Regex VersionPattern = new("^[0-9A-Za-z][0-9A-Za-z._-]{0,63}\\z", RegexOptions.CultureInvariant);

    /// <summary>A DNS name as the zone resolver's configuration lines carry it.</summary>
    private static readonly Regex HostNamePattern = new(
        "^(?=.{1,253}\\z)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*\\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex DigestPinnedImage = new("^[^\\s@]+@sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex DockerNetworkName = new("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\\z", RegexOptions.CultureInvariant);

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
        else if (!VersionPattern.IsMatch(document.Version))
            errors.Add($"version {Shown(document.Version)} must match ^[0-9A-Za-z][0-9A-Za-z._-]{{0,63}}$ "
                       + "(it goes verbatim into the rule and resolver headers)");
        if (document.Destinations is null || document.Zones is null)
        {
            errors.Add("destinations and zones are required");
            return errors;
        }
        foreach (var (name, destination) in document.Destinations)
            ValidateDestination(name, destination, errors);
        if (document.Zones.Count == 0) errors.Add("at least one zone is required");
        if (document.Zones.Any(zone => zone.Kind == ZoneKinds.Work))
            ValidateResolver(document.Resolver, errors);

        var zoneNames = new HashSet<string>(StringComparer.Ordinal);
        var networkNames = new HashSet<string>(StringComparer.Ordinal);
        var bridges = new HashSet<string>(StringComparer.Ordinal);
        var subnets = new List<(string Network, uint Start, uint End)>();
        foreach (var zone in document.Zones)
        {
            var label = $"zone {Shown(zone.Name)}";
            if (string.IsNullOrWhiteSpace(zone.Name) || !zoneNames.Add(zone.Name))
                errors.Add($"{label}: a unique zone name is required");
            else if (!IsName(zone.Name))
                errors.Add($"{label}: {NameRule}");
            if (zone.Kind is not (ZoneKinds.Work or ZoneKinds.Run))
                errors.Add($"{label}: kind must be '{ZoneKinds.Work}' or '{ZoneKinds.Run}'");
            if (zone.Networks is not { Count: > 0 })
                errors.Add($"{label}: at least one network is required");
            foreach (var network in zone.Networks ?? [])
            {
                if (string.IsNullOrWhiteSpace(network.Name) || !networkNames.Add(network.Name))
                    errors.Add($"{label}: network name '{Shown(network.Name)}' must be present and unique");
                else if (!IsName(network.Name))
                    errors.Add($"{label}: network name {Shown(network.Name)}: {NameRule}");
                if (string.IsNullOrWhiteSpace(network.Bridge) || network.Bridge.Length > MaxBridgeNameLength
                    || !network.Bridge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                    || !bridges.Add(network.Bridge))
                    errors.Add($"{label}: bridge '{network.Bridge}' must be a unique interface name of at most {MaxBridgeNameLength} characters");
                if (!TryParseCidr(network.Subnet, out var start, out var end))
                    errors.Add($"{label}: subnet '{Shown(network.Subnet)}' is not an IPv4 CIDR");
                else if (TryNormalizeCidr(network.Subnet, out var canonical) && canonical != network.Subnet)
                    errors.Add($"{label}: subnet '{network.Subnet}' is not in canonical form; write {canonical}");
                else
                {
                    foreach (var other in subnets.Where(other => start <= other.End && other.Start <= end))
                        errors.Add($"{label}: subnet {network.Subnet} of {network.Name} overlaps {other.Network}");
                    subnets.Add((network.Name, start, end));
                }
                if (zone.Kind == ZoneKinds.Run && string.IsNullOrWhiteSpace(network.Tenant))
                    errors.Add($"{label}: run network {Shown(network.Name)} must name its tenant");
                else if (network.Tenant is { } tenant && !IsName(tenant))
                    errors.Add($"{label}: tenant {Shown(tenant)}: {NameRule}");
                if (zone.Kind != ZoneKinds.Run && network.PrivateAllow is { Count: > 0 })
                    errors.Add($"{label}: privateAllow is only valid on a run network");
                foreach (var allowance in network.PrivateAllow ?? [])
                {
                    if (!TryNormalizeCidr(allowance.Cidr, out var allowed) || allowed != allowance.Cidr)
                        errors.Add($"{label}: privateAllow '{Shown(allowance.Cidr)}' must be a canonical IPv4 address or CIDR");
                    if (string.IsNullOrWhiteSpace(allowance.Reason) || allowance.Reason.Length > 80
                        || allowance.Reason.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (' ' or '-' or '_' or '.' or '/')))
                        errors.Add($"{label}: privateAllow {Shown(allowance.Cidr)} needs an audit reason of at most 80 plain characters");
                }
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
            if (string.IsNullOrWhiteSpace(verification.ProbeImage) || !DigestPinnedImage.IsMatch(verification.ProbeImage))
                errors.Add("verification.probeImage must be pinned by digest (name@sha256:<64 hex>)");
            else if (verification.ProbeImage.StartsWith('-'))
                errors.Add("verification.probeImage must not start with '-' (docker would read it as an option)");
            if (verification.TimeoutSeconds is < 1 or > 60)
                errors.Add("verification.timeoutSeconds must be between 1 and 60");
            if (string.IsNullOrWhiteSpace(verification.ControlNetwork) || !DockerNetworkName.IsMatch(verification.ControlNetwork))
                errors.Add("verification.controlNetwork must name a Docker network");
            else if (networkNames.Contains(verification.ControlNetwork))
                errors.Add($"verification.controlNetwork {verification.ControlNetwork} is a zone network; "
                           + "the positive control runs outside the zones");
        }
        return errors;
    }

    private const string NameRule = "names match ^[a-z0-9][a-z0-9-]{0,47}$ (they go verbatim into the rule comments)";

    private static bool IsName(string value) => NamePattern.IsMatch(value);

    /// <summary>A value as an error message may show it: quotes, backslashes and control characters escaped.</summary>
    private static string Shown(string? value)
        => value is null
            ? "(none)"
            : string.Concat(value.Select(character => character switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                _ => character.ToString(),
            }));

    private static void ValidateResolver(ZoneResolverSettings? resolver, List<string> errors)
    {
        if (resolver is null)
        {
            errors.Add("resolver is required: work containers resolve through the zone resolver, which fills the allowed sets");
            return;
        }
        if (resolver.Upstreams is not { Count: > 0 } || resolver.Upstreams.Any(string.IsNullOrWhiteSpace))
            errors.Add("resolver.upstreams needs at least one resolver");
        if (resolver.SetTimeoutSeconds is < 60 or > MaxSetTimeoutSeconds)
            errors.Add($"resolver.setTimeoutSeconds must be between 60 and {MaxSetTimeoutSeconds}");
    }

    private static void ValidateDestination(string name, ZoneDestination destination, List<string> errors)
    {
        var label = $"destination {Shown(name)}";
        if (!IsName(name))
            errors.Add($"{label}: {NameRule}");
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
        if (zone.Kind != ZoneKinds.Run && zone.Egress.ZeroTierRanges is { Count: > 0 })
            errors.Add($"{label}: zeroTierRanges is only valid on a run zone");
        foreach (var reference in allow.Concat(deny).Where(reference => !destinations.ContainsKey(reference)))
            errors.Add($"{label}: unknown destination '{Shown(reference)}'");
        if (zone.Kind == ZoneKinds.Work)
        {
            if (zone.Egress.Default != "deny")
                errors.Add($"{label}: a work zone denies egress by default");
            if (deny.Count > 0)
                errors.Add($"{label}: a work zone lists only allowed destinations");
            foreach (var reference in allow.Where(reference => destinations.TryGetValue(reference, out var destination)
                         && destination.Class == ZoneDestinationClasses.SecretStore))
                errors.Add($"{label}: a work zone must not reach the secret store '{reference}'");
            foreach (var reference in allow.Where(reference => ZonePolicyRules.IpSetName(reference).Length > ZonePolicyRules.MaxIpSetNameLength))
                errors.Add($"{label}: allowed destination '{Shown(reference)}' is too long for its ipset "
                           + $"{ZonePolicyRules.IpSetName(reference)} (at most {ZonePolicyRules.MaxIpSetNameLength} characters)");
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
        foreach (var (probe, blocked) in zone.Verify.Reachable.Select(probe => (probe, false))
                     .Concat(zone.Verify.Blocked.Select(probe => (probe, true))))
        {
            if (!probe.RequireControl && (zone.Kind != ZoneKinds.Run || !blocked))
                errors.Add($"{label}: requireControl=false is only valid for a blocked run probe");
            if (!probe.RequireControl && zone.Kind == ZoneKinds.Run && blocked
                && probe.Host != "169.254.169.254")
                errors.Add($"{label}: only metadata 169.254.169.254 may omit an outside-zone control");
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
                    errors.Add($"{label}: probe names unknown destination '{Shown(reference)}'");
                else if (probe.Port is null && (destination.Ports ?? []).Count == 0)
                    errors.Add($"{label}: probe of '{reference}' needs a port because the destination has none");
            }
            else if (probe.Port is null)
                errors.Add($"{label}: probe of host '{probe.Host}' needs a port");
        }
    }

    /// <summary>
    /// Expand <c>${NAME}</c> references and sort each destination's hosts into
    /// literal addresses and names. A work zone allows names through the
    /// destination's ipset, which the zone resolver fills with whatever the
    /// containers resolve, so a name is resolved here only where an address
    /// must be known up front: for a run zone's denial and for a blocked probe.
    /// A configuration error throws. A name that does not resolve does not: a
    /// denied name becomes a <see cref="ZoneResolutionProblem"/> and a blocked
    /// probe host an unresolved probe, so the apply still loads every other
    /// rule (DNS may not be up yet at boot) and records the state as failed.
    /// </summary>
    public static async Task<ResolvedZonePolicy> ResolveAsync(
        ZonePolicyDocument document,
        string policySha256,
        IReadOnlyDictionary<string, string> environment,
        IZoneAddressResolver resolver,
        CancellationToken ct,
        IReadOnlyCollection<string>? zoneNames = null,
        IReadOnlyCollection<string>? zeroTierHostRanges = null)
    {
        var errors = new List<string>();
        var problems = new List<ZoneResolutionProblem>();
        var selected = document.Zones
            .Where(zone => zoneNames is not { Count: > 0 } || zoneNames.Contains(zone.Name, StringComparer.Ordinal))
            .ToList();
        foreach (var unknown in (zoneNames ?? []).Where(name => document.Zones.All(zone => zone.Name != name)))
            errors.Add($"zone '{Shown(unknown)}' is not in the policy");
        // A work host never needs the run zone's secret-store address and the
        // reverse, so only destinations the selected zones use are resolved.
        var referenced = selected
            .SelectMany(zone => (zone.Egress.Allow ?? []).Concat(zone.Egress.Deny ?? [])
                .Concat(zone.Verify.Reachable.Concat(zone.Verify.Blocked)
                    .Select(probe => probe.Destination).OfType<string>()))
            .ToHashSet(StringComparer.Ordinal);
        var needAddresses = selected
            .SelectMany(zone => (zone.Egress.Deny ?? [])
                .Concat(zone.Verify.Blocked.Where(probe => probe.Host is null).Select(probe => probe.Destination).OfType<string>()))
            .ToHashSet(StringComparer.Ordinal);
        var resolved = new Dictionary<string, ResolvedZoneDestination>(StringComparer.Ordinal);
        foreach (var (name, destination) in document.Destinations
                     .Where(item => referenced.Contains(item.Key))
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var label = $"destination {name}";
            var hosts = new List<string>();
            foreach (var entry in destination.Hosts)
                hosts.AddRange(Expand(entry, label, environment, errors));
            var literals = new SortedSet<string>(StringComparer.Ordinal);
            var names = new List<string>();
            foreach (var host in hosts)
            {
                if (ClassifyHost(host, label, errors) is not { } classified) continue;
                if (classified.IsLiteral) literals.Add(classified.Value);
                else if (!names.Contains(classified.Value, StringComparer.Ordinal)) names.Add(classified.Value);
            }
            var addresses = new SortedSet<string>(literals, StringComparer.Ordinal);
            if (needAddresses.Contains(name))
            {
                foreach (var host in names)
                {
                    var (found, problem) = await ResolveIpv4Async(resolver, host, ct);
                    if (problem is not null) problems.Add(new ZoneResolutionProblem(name, $"{label}: {problem}"));
                    foreach (var address in found)
                        addresses.Add($"{address}/32");
                }
            }
            var ports = (destination.Ports ?? []).Distinct().ToList();
            var protocols = ports.Count == 0
                ? new List<string>()
                : (destination.Protocols is { Count: > 0 } configured ? configured.Distinct().ToList() : ["tcp"]);
            resolved[name] = new ResolvedZoneDestination(name, destination.Class, hosts,
                literals.OrderBy(SortKey).ToList(), names, addresses.OrderBy(SortKey).ToList(), ports, protocols);
        }

        var zones = new List<ResolvedZone>();
        foreach (var zone in selected)
        {
            var zeroTierRanges = new SortedSet<string>(StringComparer.Ordinal);
            if (zone.Kind == ZoneKinds.Run)
            {
                foreach (var entry in zone.Egress.ZeroTierRanges ?? [])
                foreach (var range in ExpandOptional(entry, "zeroTierRanges", environment))
                    AddZeroTierRange(range, errors, zeroTierRanges);
                foreach (var range in zeroTierHostRanges ?? [])
                    AddZeroTierRange(range, errors, zeroTierRanges);
            }
            var allow = (zone.Egress.Allow ?? []).Select(name => resolved[name]).ToList();
            var deny = (zone.Egress.Deny ?? []).Select(name => resolved[name]).ToList();
            foreach (var network in zone.Networks)
            foreach (var allowance in network.PrivateAllow ?? [])
            {
                if (!TryParseCidr(allowance.Cidr, out var allowedStart, out var allowedEnd)) continue;
                var restricted = ZonePolicyRules.PrivateIpv4Ranges.Concat(zeroTierRanges)
                    .Any(range => TryParseCidr(range, out var start, out var end)
                                  && start <= allowedStart && allowedEnd <= end);
                if (!restricted)
                    errors.Add($"zone {zone.Name}: tenant {network.Tenant} privateAllow {allowance.Cidr} is not within a denied private or ZeroTier range");
                if (deny.SelectMany(destination => destination.Addresses).Any(address =>
                        TryParseCidr(address, out var start, out var end)
                        && start <= allowedEnd && allowedStart <= end))
                    errors.Add($"zone {zone.Name}: tenant {network.Tenant} privateAllow {allowance.Cidr} overlaps a denied control-plane or secret-store destination");
            }
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
                host = ExpandProbeHost(host, zone.Name, environment, errors);
                if (host is null) continue;
                if (TryNormalizeCidr(host, out var cidr) && !cidr.EndsWith("/32", StringComparison.Ordinal))
                {
                    errors.Add($"zone {zone.Name}: reachable probe needs a host, not the range {cidr}");
                    continue;
                }
                probes.Add(new ResolvedZoneProbe(ZoneProbeExpectations.Reachable, host, host.Split('/')[0], port));
            }
            foreach (var probe in zone.Verify.Blocked)
            {
                // Without a port, a blocked probe covers every port the
                // destination declares: a denial must hold on each of them.
                IReadOnlyList<int> ports = probe.Port is { } given ? [given] : resolved[probe.Destination!].Ports;
                if (probe is { Destination: { } reference, Host: null })
                {
                    foreach (var address in resolved[reference].Addresses)
                    foreach (var port in ports)
                        probes.Add(new ResolvedZoneProbe(ZoneProbeExpectations.Blocked, reference, FirstHost(address), port,
                            RequireControl: probe.RequireControl));
                    continue;
                }
                var target = ExpandProbeHost(probe.Host!, zone.Name, environment, errors);
                if (target is null) continue;
                if (TryNormalizeCidr(target, out var literal))
                {
                    foreach (var port in ports)
                        probes.Add(new ResolvedZoneProbe(ZoneProbeExpectations.Blocked, target, FirstHost(literal), port,
                            RequireControl: probe.RequireControl));
                    continue;
                }
                var (resolvedTarget, unresolved) = await ResolveIpv4Async(resolver, target, ct);
                foreach (var port in ports)
                    probes.Add(resolvedTarget.Count > 0
                        ? new ResolvedZoneProbe(ZoneProbeExpectations.Blocked, target, resolvedTarget[0].ToString(), port,
                            RequireControl: probe.RequireControl)
                        : new ResolvedZoneProbe(ZoneProbeExpectations.Blocked, target, target, port,
                            $"zone {zone.Name}: blocked probe host {unresolved}", probe.RequireControl));
            }
            foreach (var probe in probes.Where(probe => !probe.RequireControl && probe.Unresolved is null))
            {
                if (probe.Address != "169.254.169.254")
                    errors.Add($"zone {zone.Name}: only metadata 169.254.169.254 may omit an outside-zone control");
            }
            zones.Add(new ResolvedZone(zone.Name, zone.Kind, zone.Networks, allow, deny, probes,
                zeroTierRanges.OrderBy(SortKey).ToList()));
        }

        ResolvedZoneResolver? zoneResolver = null;
        if (selected.Any(zone => zone.Kind == ZoneKinds.Work) && document.Resolver is { } settings)
        {
            var upstreams = new List<string>();
            foreach (var entry in settings.Upstreams)
            {
                foreach (var upstream in Expand(entry, "resolver", environment, errors))
                {
                    if (IPAddress.TryParse(upstream, out var address) && address.AddressFamily == AddressFamily.InterNetwork
                        && TryNormalizeCidr(upstream, out var single) && single.EndsWith("/32", StringComparison.Ordinal))
                    {
                        if (!upstreams.Contains(address.ToString(), StringComparer.Ordinal)) upstreams.Add(address.ToString());
                    }
                    else
                        errors.Add($"resolver: upstream '{Shown(upstream)}' is not an IPv4 address");
                }
            }
            zoneResolver = new ResolvedZoneResolver(upstreams, settings.SetTimeoutSeconds);
        }
        if (errors.Count > 0) throw new ZonePolicyException(errors);
        return new ResolvedZonePolicy(document.Version, policySha256, zones,
            document.Verification ?? new ZoneVerificationSettings(), zoneResolver, problems);
    }

    /// <summary>
    /// A host entry is an IPv4 address or canonical CIDR (kept literal) or a
    /// DNS name (lower-cased). Anything else is an error.
    /// </summary>
    private static (bool IsLiteral, string Value)? ClassifyHost(string host, string label, List<string> errors)
    {
        if (TryNormalizeCidr(host, out var cidr)) return (true, cidr);
        if (host.Contains('/'))
        {
            errors.Add($"{label}: '{Shown(host)}' is not a canonical IPv4 CIDR");
            return null;
        }
        if (host.Contains(':'))
        {
            errors.Add($"{label}: '{Shown(host)}' is not an IPv4 address; zone networks carry IPv4 only");
            return null;
        }
        var name = host.ToLowerInvariant().TrimEnd('.');
        if (IPAddress.TryParse(host, out _) || !HostNamePattern.IsMatch(name) || name.Split('.')[^1].All(char.IsAsciiDigit))
        {
            errors.Add($"{label}: '{Shown(host)}' is neither an IPv4 address nor a DNS name");
            return null;
        }
        return (false, name);
    }

    /// <returns>The IPv4 addresses, and why there are none.</returns>
    private static async Task<(IReadOnlyList<IPAddress> Addresses, string? Problem)> ResolveIpv4Async(
        IZoneAddressResolver resolver, string host, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> found;
        try
        {
            found = await resolver.ResolveAsync(host, ct);
        }
        catch (SocketException exception)
        {
            return ([], $"'{host}' does not resolve ({exception.SocketErrorCode})");
        }
        var ipv4 = found.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToList();
        return ipv4.Count == 0 ? (ipv4, $"'{host}' has no IPv4 address") : (ipv4, null);
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

    private static IEnumerable<string> ExpandOptional(
        string entry, string label, IReadOnlyDictionary<string, string> environment)
    {
        if (entry.StartsWith("${", StringComparison.Ordinal) && entry.EndsWith('}')
            && (!environment.TryGetValue(entry[2..^1], out var configured) || string.IsNullOrWhiteSpace(configured)))
            return [];
        var errors = new List<string>();
        var expanded = Expand(entry, label, environment, errors).ToList();
        if (errors.Count > 0) throw new ZonePolicyException(errors);
        return expanded;
    }

    private static string? ExpandProbeHost(string host, string zone,
        IReadOnlyDictionary<string, string> environment, List<string> errors)
    {
        var expanded = Expand(host, $"zone {zone} probe", environment, errors).ToList();
        if (expanded.Count == 1) return expanded[0];
        if (expanded.Count > 1) errors.Add($"zone {zone}: a probe host must resolve to one address");
        return null;
    }

    private static void AddZeroTierRange(string value, List<string> errors, SortedSet<string> ranges)
    {
        if (!TryNormalizeCidr(value, out var canonical) || canonical != value)
            errors.Add($"zeroTierRanges '{Shown(value)}' must be a canonical IPv4 address or CIDR");
        else
            ranges.Add(canonical);
    }

    internal static bool TryParseCidr(string? value, out uint start, out uint end)
        => TryParseCidr(value, out start, out end, out _);

    /// <summary>
    /// A dotted-quad IPv4 network with a prefix and no host bits. Octets are
    /// plain decimal, so the octal and hexadecimal forms
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> accepts never
    /// turn into a different address.
    /// </summary>
    private static bool TryParseCidr(string? value, out uint start, out uint end, out int prefix)
    {
        start = end = 0;
        prefix = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
            || prefix is < 0 or > 32) return false;
        var octets = parts[0].Split('.');
        if (octets.Length != 4) return false;
        uint raw = 0;
        foreach (var octet in octets)
        {
            if (octet.Length is < 1 or > 3 || !octet.All(char.IsAsciiDigit) || (octet.Length > 1 && octet[0] == '0')) return false;
            var number = uint.Parse(octet, CultureInfo.InvariantCulture);
            if (number > 255) return false;
            raw = raw << 8 | number;
        }
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        if ((raw & ~mask) != 0) return false;
        start = raw;
        end = raw | ~mask;
        return true;
    }

    /// <summary>
    /// An IPv4 literal becomes <c>a.b.c.d/32</c>; a CIDR must have no host
    /// bits. The result is always the canonical <c>addr/prefix</c> form, so
    /// <c>10.0.0.0/08</c> and <c>10.0.0.0/8</c> render the same rule.
    /// </summary>
    internal static bool TryNormalizeCidr(string value, out string cidr)
    {
        cidr = string.Empty;
        var candidate = value.Contains('/') ? value : value + "/32";
        if (!TryParseCidr(candidate, out var start, out _, out var prefix)) return false;
        cidr = $"{ToAddress(start)}/{prefix.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    internal static string NetworkOf(IPAddress address, int prefix)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefix is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefix));
        var bytes = address.GetAddressBytes();
        var raw = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return $"{ToAddress(raw & mask)}/{prefix}";
    }

    /// <summary>The address Docker gives the bridge itself: the subnet's first host.</summary>
    public static string GatewayOf(string subnet)
    {
        TryParseCidr(subnet, out var start, out _);
        return ToAddress(start + 1);
    }

    private static string ToAddress(uint value)
        => new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]).ToString();

    /// <summary>The address a blocked probe connects to inside a CIDR.</summary>
    private static string FirstHost(string cidr)
    {
        TryParseCidr(cidr, out var start, out var end);
        return ToAddress(end > start + 1 ? start + 1 : start);
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
