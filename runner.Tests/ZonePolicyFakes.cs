using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentRunner;

namespace AgentRunner.Tests;

/// <summary>
/// Deterministic DNS for zone policy tests: every name maps to one address in
/// 198.18.0.0/15 derived from its hash, unless overridden, rotating through a
/// scripted sequence of answers, or marked missing.
/// </summary>
internal sealed class FakeZoneResolver : IZoneAddressResolver
{
    public Dictionary<string, string[]> Overrides { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Queue<string[]>> Rotating { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);
    public List<string> Queries { get; } = [];

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        => Task.FromResult(Resolve(host));

    public IReadOnlyList<IPAddress> Resolve(string host)
    {
        Queries.Add(host);
        if (Missing.Contains(host)) throw new SocketException((int)SocketError.HostNotFound);
        if (Rotating.TryGetValue(host, out var answers) && answers.Count > 0)
            return answers.Dequeue().Select(IPAddress.Parse).ToList();
        if (Overrides.TryGetValue(host, out var addresses)) return addresses.Select(IPAddress.Parse).ToList();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
        return [new IPAddress([198, (byte)(18 + (hash[0] & 1)), hash[1], (byte)Math.Max(1, (int)hash[2])])];
    }
}

/// <summary>Kernel ipsets with per-entry timeouts on an injected clock.</summary>
internal sealed class FakeIpSets(TimeProvider clock)
{
    private readonly Dictionary<string, (int Timeout, Dictionary<string, DateTimeOffset> Entries)> _sets = new(StringComparer.Ordinal);

    public IEnumerable<string> Names => _sets.Keys;

    public bool Exists(string name) => _sets.ContainsKey(name);

    public int Timeout(string name) => _sets[name].Timeout;

    public void Create(string name, int timeoutSeconds) => _sets[name] = (timeoutSeconds, new(StringComparer.Ordinal));

    /// <summary>Like dnsmasq's netlink add without NLM_F_EXCL: an existing entry gets a fresh timeout.</summary>
    public void Add(string name, IPAddress address)
    {
        var set = _sets[name];
        set.Entries[address.ToString()] = clock.GetUtcNow().AddSeconds(set.Timeout);
    }

    public bool Contains(string name, IPAddress address)
        => _sets.TryGetValue(name, out var set)
           && set.Entries.TryGetValue(address.ToString(), out var expires)
           && clock.GetUtcNow() < expires;
}

/// <summary>
/// The zone resolver as dnsmasq runs it from the rendered configuration:
/// forward a name only when a <c>server=/domain/upstream</c> line covers it
/// (otherwise answer REFUSED, here a <see cref="SocketException"/> with
/// <see cref="SocketError.NoRecovery"/>), then add every IPv4 answer for a
/// configured name or one of its subdomains to that name's ipsets before
/// answering. A general <c>server=</c> line would forward every name.
/// </summary>
internal sealed class FakeZoneDnsmasq
{
    private readonly List<(string Domain, string[] Sets)> _ipsets = [];
    private readonly HashSet<string> _forwarded = new(StringComparer.Ordinal);
    private readonly bool _forwardsAll;
    private readonly FakeZoneResolver _upstream;
    private readonly FakeIpSets _sets;

    public FakeZoneDnsmasq(string config, FakeZoneResolver upstream, FakeIpSets sets)
    {
        _upstream = upstream;
        _sets = sets;
        foreach (var line in config.Split('\n'))
        {
            if (line.StartsWith("ipset=/", StringComparison.Ordinal))
            {
                var parts = line["ipset=/".Length..].Split('/');
                _ipsets.Add((parts[0], parts[1].Split(',')));
            }
            else if (line.StartsWith("server=/", StringComparison.Ordinal))
                _forwarded.Add(line["server=/".Length..].Split('/')[0]);
            else if (line.StartsWith("server=", StringComparison.Ordinal))
                _forwardsAll = true;
        }
    }

    private static bool Covers(string domain, string name)
        => name == domain || name.EndsWith("." + domain, StringComparison.Ordinal);

    public IReadOnlyList<IPAddress> Resolve(string name)
    {
        if (!_forwardsAll && !_forwarded.Any(domain => Covers(domain, name)))
            throw new SocketException((int)SocketError.NoRecovery);
        var answers = _upstream.Resolve(name);
        var sets = _ipsets
            .Where(item => Covers(item.Domain, name))
            .OrderByDescending(item => item.Domain.Length)
            .Select(item => item.Sets)
            .FirstOrDefault();
        foreach (var set in sets ?? [])
        foreach (var address in answers.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
            _sets.Add(set, address);
        return answers;
    }
}

/// <summary>
/// Evaluates a rendered <c>iptables-restore</c> payload for one new TCP
/// connection, for the subset of matches <see cref="ZonePolicyRules"/> emits.
/// With no rules loaded every connection completes, like a host before apply.
/// Docker's default bridge (docker0, 172.17.0.0/16) and one Compose network
/// (br-compose, 172.18.0.0/16) sit next to the zone bridges.
/// </summary>
internal sealed class FakeZoneFirewall
{
    private readonly List<(string Chain, List<string> Tokens)> _rules = [];
    private readonly List<(string Bridge, uint Start, uint End, uint Gateway)> _bridges = [];
    private readonly FakeIpSets? _sets;

    public FakeZoneFirewall(IEnumerable<ZoneNetworkSpec> networks, string? iptables, FakeIpSets? sets = null)
    {
        _sets = sets;
        foreach (var (bridge, subnet) in networks.Select(network => (network.Bridge, network.Subnet))
                     .Append(("docker0", "172.17.0.0/16")).Append(("br-compose", "172.18.0.0/16")))
        {
            ZonePolicyParser.TryParseCidr(subnet, out var start, out var end);
            _bridges.Add((bridge, start, end, start + 1));
        }
        foreach (var line in (iptables ?? string.Empty).Split('\n'))
        {
            if (!line.StartsWith("-A ", StringComparison.Ordinal)) continue;
            var tokens = Tokenize(line);
            _rules.Add((tokens[1], tokens.Skip(2).ToList()));
        }
    }

    /// <param name="translated">Where Docker's port forwarding sends the
    /// connection (DNAT before FORWARD), or null.</param>
    public ZoneProbeOutcome Connect(string fromBridge, IPAddress address, int port, IPAddress? translated = null)
    {
        if (_rules.Count == 0) return ZoneProbeOutcome.Connected;
        var original = ToUInt(address);
        var raw = translated is null ? original : ToUInt(translated);
        var toHost = _bridges.Any(bridge => bridge.Gateway == raw);
        var outInterface = _bridges.FirstOrDefault(bridge => raw >= bridge.Start && raw <= bridge.End).Bridge ?? "eth0";
        var chain = toHost ? ZonePolicyRules.InputChain : ZonePolicyRules.ForwardChain;
        foreach (var (ruleChain, tokens) in _rules.Where(rule => rule.Chain == chain))
        {
            if (!Matches(tokens, fromBridge, toHost ? null : outInterface, raw, original, port)) continue;
            var target = tokens[tokens.IndexOf("-j") + 1];
            return target switch
            {
                "RETURN" => ZoneProbeOutcome.Connected,
                "DROP" => ZoneProbeOutcome.TimedOut,
                "REJECT" => ZoneProbeOutcome.Rejected,
                _ => throw new InvalidOperationException($"unexpected target {target}"),
            };
        }
        return ZoneProbeOutcome.Connected;
    }

    private bool Matches(List<string> tokens, string inInterface, string? outInterface, uint address, uint original, int port)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            switch (tokens[index])
            {
                case "--ctstate":
                    // Every probe is a NEW connection.
                    if (!tokens[++index].Split(',').Contains("NEW")) return false;
                    break;
                case "-i":
                    if (tokens[++index] != inInterface) return false;
                    break;
                case "-o":
                    var pattern = tokens[++index];
                    if (pattern.EndsWith('+')
                            ? outInterface?.StartsWith(pattern[..^1], StringComparison.Ordinal) != true
                            : pattern != outInterface)
                        return false;
                    break;
                case "-d":
                    if (!InCidr(tokens[++index], address)) return false;
                    break;
                case "--ctorigdst":
                    if (!InCidr(tokens[++index], original)) return false;
                    break;
                case "--match-set":
                    var set = tokens[++index];
                    index++;
                    if (_sets?.Contains(set, ToAddress(address)) != true) return false;
                    break;
                case "-p":
                    if (tokens[++index] != "tcp") return false;
                    break;
                case "--dport":
                    if (int.Parse(tokens[++index]) != port) return false;
                    break;
                case "--dports":
                    if (!tokens[++index].Split(',').Select(int.Parse).Contains(port)) return false;
                    break;
                case "--comment":
                case "-m":
                case "--reject-with":
                    index++;
                    break;
                case "-j":
                    return true;
                default:
                    throw new InvalidOperationException($"the fake firewall does not know {tokens[index]}");
            }
        }
        return true;
    }

    private static bool InCidr(string cidr, uint address)
    {
        ZonePolicyParser.TryParseCidr(cidr, out var start, out var end);
        return address >= start && address <= end;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in line)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (character == ' ' && !quoted)
            {
                if (current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
    }

    private static IPAddress ToAddress(uint value)
        => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}

/// <summary>
/// A zone network seen through the fake firewall and fake DNS. A probe with a
/// <c>dns</c> server resolves through the fake zone resolver when one is
/// configured, so its answers fill the ipsets; the control network
/// (Docker's default bridge) sits outside every zone.
/// </summary>
internal sealed class FakeZoneNetwork(
    IReadOnlyList<ZoneNetworkSpec> networks,
    Func<string?> loadedRules,
    FakeZoneResolver resolver,
    FakeIpSets? sets = null,
    Func<string?>? resolverConfig = null) : IZoneNetworkProbe
{
    public Dictionary<string, List<string>> Published { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string Address, int Port), ZoneProbeObservation> Forced { get; } = new();
    public Dictionary<(string Network, string Address, int Port), ZoneProbeObservation> ForcedFrom { get; } = new();
    public string? PrepareFailure { get; set; }
    public List<(string Network, string Address, int Port)> Connections { get; } = [];
    public List<(string Network, string? Dns)> DnsUsed { get; } = [];

    public Task PrepareAsync(CancellationToken ct)
        => PrepareFailure is null ? Task.CompletedTask : throw new InvalidOperationException(PrepareFailure);

    public Task<ZoneProbeObservation> ConnectAsync(string network, string? dns, string address, int port, CancellationToken ct)
    {
        Connections.Add((network, address, port));
        DnsUsed.Add((network, dns));
        if (ForcedFrom.TryGetValue((network, address, port), out var forcedFrom)) return Task.FromResult(forcedFrom);
        if (Forced.TryGetValue((address, port), out var forced)) return Task.FromResult(forced);
        IPAddress target;
        if (!IPAddress.TryParse(address, out target!))
        {
            try
            {
                target = dns is not null && resolverConfig?.Invoke() is { } config && sets is not null
                    ? new FakeZoneDnsmasq(config, resolver, sets).Resolve(address)[0]
                    : resolver.Resolve(address)[0];
            }
            catch (SocketException)
            {
                return Task.FromResult(new ZoneProbeObservation(ZoneProbeOutcome.Unresolved, "Name or service not known"));
            }
        }
        var bridge = network == ZonePolicyRules.DefaultControlNetwork
            ? "docker0"
            : networks.Single(item => item.Name == network).Bridge;
        var outcome = new FakeZoneFirewall(networks, loadedRules(), sets).Connect(bridge, target, port);
        return Task.FromResult(new ZoneProbeObservation(outcome, outcome.ToString()));
    }

    public Task<IReadOnlyList<string>> PublishedPortsAsync(string network, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Published.GetValueOrDefault(network) ?? []);
}

/// <summary>
/// A scripted Docker, ipset, systemd and iptables host. It keeps networks,
/// ipsets, the zone resolver unit, the loaded zone chains per family and the
/// jumps as state, so a second apply or remove observes the first.
/// </summary>
internal sealed class FakeZoneHost(TimeProvider? clock = null) : IZoneHost
{
    public bool DockerUp { get; set; } = true;
    public bool Root { get; set; } = true;
    public bool Ip6DockerUser { get; set; } = true;

    /// <summary>False for a kernel without IPv6: no /proc/net/if_inet6.</summary>
    public bool Ipv6Kernel { get; set; } = true;

    /// <summary>ip6tables reports that the IPv6 filter table is not there.</summary>
    public bool Ip6TablesUnsupported { get; set; }

    /// <summary>The kernel has IPv6, but the ip6tables filter table is not loaded.</summary>
    public bool Ip6FilterTableMissing { get; set; }
    public bool ResolverStartFails { get; set; }
    public Dictionary<string, string> Networks { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Jumps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Loaded { get; } = new(StringComparer.Ordinal);
    public FakeIpSets Sets { get; } = new(clock ?? new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
    public bool ResolverActive { get; set; }
    public int ResolverStarts { get; private set; }
    public List<string> Commands { get; } = [];

    /// <summary>Commands that fail with "simulated failure".</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    /// <summary>The loaded IPv4 payload, or null without the zone chains.</summary>
    public string? LoadedRules
    {
        get => Loaded.GetValueOrDefault("iptables");
        set
        {
            if (value is null) Loaded.Remove("iptables");
            else Loaded["iptables"] = value;
        }
    }

    public IEnumerable<string> Mutations => Commands.Where(command =>
        command.StartsWith("docker network create", StringComparison.Ordinal)
        || command.StartsWith("ipset create", StringComparison.Ordinal)
        || command.StartsWith("systemctl start", StringComparison.Ordinal)
        || command.StartsWith("systemctl restart", StringComparison.Ordinal)
        || command is "iptables-restore --noflush" or "ip6tables-restore --noflush"
        || command.Split(' ') is [_, "-I" or "-D" or "-F" or "-X", ..]);

    public Task<ZoneCommandResult> RunAsync(
        string executable, IReadOnlyList<string> args, string? standardInput, CancellationToken ct)
    {
        var command = $"{executable} {string.Join(' ', args)}";
        Commands.Add(command);
        return Task.FromResult(Failing.Contains(command)
            ? new ZoneCommandResult(1, "", "simulated failure")
            : Answer(executable, args, standardInput));
    }

    public bool PathExists(string path) => path != ZonePolicyApplier.Ipv6InterfacesPath || Ipv6Kernel;

    private ZoneCommandResult Answer(string executable, IReadOnlyList<string> args, string? standardInput)
    {
        if (executable == "docker")
        {
            if (!DockerUp) return new ZoneCommandResult(1, "", "Cannot connect to the Docker daemon");
            return args switch
            {
                ["version", ..] => new ZoneCommandResult(0, "27.3.1\n", ""),
                ["network", "inspect", "--format", _, var name] => Networks.TryGetValue(name, out var inspected)
                    ? new ZoneCommandResult(0, inspected + "\n", "")
                    : new ZoneCommandResult(1, "", $"Error response from daemon: network {name} not found"),
                ["network", "create", ..] => Create(args),
                _ => new ZoneCommandResult(1, "", "unexpected docker command"),
            };
        }
        if (executable == "systemctl")
        {
            return args switch
            {
                ["is-active", "--quiet", _] => new ZoneCommandResult(ResolverActive ? 0 : 3, "", ""),
                ["start" or "restart", _] when Root && ResolverStartFails => new ZoneCommandResult(1, "",
                    "Job for agent-zone-dns.service failed because the control process exited with error code."),
                ["start" or "restart", _] when Root => Start(),
                _ => new ZoneCommandResult(1, "", "Failed to start agent-zone-dns.service: Access denied"),
            };
        }
        if (!Root) return new ZoneCommandResult(4, "", $"{executable} v1.8.10 (nf_tables): Permission denied (you must be root)");
        if (executable.StartsWith("ip6tables", StringComparison.Ordinal) && (Ip6TablesUnsupported || !Ipv6Kernel))
            return new ZoneCommandResult(3, "", $"{executable} v1.8.10 (legacy): can't initialize ip6tables table `filter': "
                                                + "Address family not supported by protocol");
        if (executable.StartsWith("ip6tables", StringComparison.Ordinal) && Ip6FilterTableMissing)
            return new ZoneCommandResult(3, "", $"{executable} v1.8.10 (legacy): can't initialize ip6tables table `filter': "
                                                + "Table does not exist (do you need to insmod?)");
        if (executable == "ipset")
        {
            return args switch
            {
                ["-t", "list", var name] => Sets.Exists(name)
                    ? new ZoneCommandResult(0, $"Name: {name}\nType: hash:ip\nRevision: 6\n"
                                               + $"Header: family inet hashsize 1024 maxelem 65536 timeout {Sets.Timeout(name)} bucketsize 12\n"
                                               + "Size in memory: 216\nReferences: 0\nNumber of entries: 0\n", "")
                    : new ZoneCommandResult(1, "", "ipset v7.19: The set with the given name does not exist"),
                ["create", var name, "hash:ip", "family", "inet", "timeout", var timeout] => CreateSet(name, int.Parse(timeout)),
                _ => new ZoneCommandResult(1, "", "unexpected ipset command"),
            };
        }
        if (executable.EndsWith("-restore", StringComparison.Ordinal))
        {
            var family = executable[..^"-restore".Length];
            var tokens = (standardInput ?? "").Split([' ', '\n']);
            var missingSet = tokens.Where((token, index) => index > 0 && tokens[index - 1] == "--match-set")
                .FirstOrDefault(set => !Sets.Exists(set));
            if (missingSet is not null)
                return new ZoneCommandResult(2, "", $"{executable} v1.8.10 (nf_tables): Set {missingSet} doesn't exist.");
            if (args.Contains("--test")) return new ZoneCommandResult(0, "", "");
            Loaded[family] = standardInput!;
            _deleted.RemoveWhere(item => item.StartsWith($"{family}:", StringComparison.Ordinal));
            return new ZoneCommandResult(0, "", "");
        }
        return args switch
        {
            ["-C", var chain, "-j", var target] => !ChainExists(executable, chain)
                ? new ZoneCommandResult(2, "", $"{executable}: No chain/target/match by that name.")
                : Jumps.Contains($"{executable}:{chain}>{target}")
                    ? new ZoneCommandResult(0, "", "")
                    : new ZoneCommandResult(1, "", $"{executable}: Bad rule (does a matching rule exist in that chain?)."),
            ["-I", var chain, "1", "-j", var target] => Insert(executable, chain, target),
            ["-D", var chain, "-j", var target] => Jumps.Remove($"{executable}:{chain}>{target}")
                ? new ZoneCommandResult(0, "", "")
                : new ZoneCommandResult(1, "", $"{executable}: Bad rule (does a matching rule exist in that chain?)."),
            ["-S", var chain] => ChainExists(executable, chain)
                ? new ZoneCommandResult(0, $"-N {chain}\n", "")
                : new ZoneCommandResult(1, "", $"{executable}: No chain/target/match by that name."),
            ["-F", _] => new ZoneCommandResult(0, "", ""),
            ["-X", var chain] => Delete(executable, chain),
            _ => new ZoneCommandResult(1, "", $"unexpected {executable} command"),
        };
    }

    private bool ChainExists(string executable, string chain)
        => chain.StartsWith("AGENT-ZONE-", StringComparison.Ordinal)
            ? Loaded.ContainsKey(executable) && !_deleted.Contains($"{executable}:{chain}")
            : chain != "DOCKER-USER" || executable == "iptables" || Ip6DockerUser;

    private ZoneCommandResult Delete(string executable, string chain)
    {
        if (!ChainExists(executable, chain))
            return new ZoneCommandResult(1, "", $"{executable}: No chain/target/match by that name.");
        if (Jumps.Contains($"{executable}:DOCKER-USER>{chain}") || Jumps.Contains($"{executable}:INPUT>{chain}")
            || Jumps.Contains($"{executable}:FORWARD>{chain}"))
            return new ZoneCommandResult(1, "", $"{executable} v1.8.10 (nf_tables): CHAIN_USER_DEL failed (Device or resource busy): chain {chain}");
        _deleted.Add($"{executable}:{chain}");
        // The payload declares both chains; once both are gone nothing is loaded.
        if (!ChainExists(executable, ZonePolicyRules.ForwardChain) && !ChainExists(executable, ZonePolicyRules.InputChain))
        {
            Loaded.Remove(executable);
            _deleted.RemoveWhere(item => item.StartsWith($"{executable}:", StringComparison.Ordinal));
        }
        return new ZoneCommandResult(0, "", "");
    }

    private ZoneCommandResult Create(IReadOnlyList<string> args)
    {
        string Option(string prefix) => args.First(arg => arg.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        var subnet = args[args.ToList().IndexOf("--subnet") + 1];
        var gateway = args[args.ToList().IndexOf("--gateway") + 1];
        Networks[args[^1]] = $"bridge|{subnet},|{gateway},|{Option("com.docker.network.bridge.name=")}|"
                             + Option("com.docker.network.bridge.enable_icc=") + "|"
                             + (args.Contains("--ipv6=false") ? "false" : "true");
        return new ZoneCommandResult(0, "network-id\n", "");
    }

    private ZoneCommandResult CreateSet(string name, int timeout)
    {
        Sets.Create(name, timeout);
        return new ZoneCommandResult(0, "", "");
    }

    private ZoneCommandResult Start()
    {
        ResolverActive = true;
        ResolverStarts++;
        return new ZoneCommandResult(0, "", "");
    }

    private ZoneCommandResult Insert(string executable, string chain, string target)
    {
        if (!ChainExists(executable, chain)) return new ZoneCommandResult(1, "", $"{executable}: No chain/target/match by that name.");
        Jumps.Add($"{executable}:{chain}>{target}");
        return new ZoneCommandResult(0, "", "");
    }
}

internal static class ZonePolicyTestFiles
{
    public static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>
    {
        ["AGENT_ZONE_DNS_RESOLVERS"] = "192.0.2.53, 192.0.2.54",
        ["AGENT_ZONE_CONTROL_PLANE_HOSTS"] = "task-server.example.internal",
        ["AGENT_ZONE_SECRET_STORE_HOSTS"] = "192.0.2.80,10.20.0.0/16",
        ["AGENT_ZONE_ZEROTIER_RANGES"] = "198.51.100.0/24",
        ["AGENT_ZONE_LAN_PROBE"] = "192.168.50.1",
        ["AGENT_ZONE_ZEROTIER_PEER"] = "198.51.100.7",
    };

    public static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        throw new InvalidOperationException("repository root not found");
    }

    public static string CheckedInPolicyPath => Path.Combine(RepoRoot(), "deploy", "zones", "zone-policy.json");

    public static string CheckedInPolicy => File.ReadAllText(CheckedInPolicyPath);

    public static async Task<ResolvedZonePolicy> ResolveAsync(
        string json, FakeZoneResolver? resolver = null, IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<string>? zones = null, IReadOnlyCollection<string>? zeroTierHostRanges = null)
        => await ZonePolicyParser.ResolveAsync(ZonePolicyParser.Parse(json), ZonePolicyParser.Sha256(json),
            environment ?? Environment, resolver ?? new FakeZoneResolver(), CancellationToken.None, zones, zeroTierHostRanges);
}
