using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentRunner;

namespace AgentRunner.Tests;

/// <summary>
/// Deterministic DNS for zone policy tests: every name maps to one address in
/// 198.18.0.0/15 derived from its hash, unless overridden or marked missing.
/// </summary>
internal sealed class FakeZoneResolver : IZoneAddressResolver
{
    public Dictionary<string, string[]> Overrides { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        => Task.FromResult(Resolve(host));

    public IReadOnlyList<IPAddress> Resolve(string host)
    {
        if (Missing.Contains(host)) throw new SocketException((int)SocketError.HostNotFound);
        if (Overrides.TryGetValue(host, out var addresses)) return addresses.Select(IPAddress.Parse).ToList();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
        return [new IPAddress([198, (byte)(18 + (hash[0] & 1)), hash[1], (byte)Math.Max(1, (int)hash[2])])];
    }
}

/// <summary>
/// Evaluates a rendered <c>iptables-restore</c> payload for one new TCP
/// connection, for the subset of matches <see cref="ZonePolicyRules"/> emits.
/// With no rules loaded every connection completes, like a host before apply.
/// </summary>
internal sealed class FakeZoneFirewall
{
    private readonly List<(string Chain, List<string> Tokens)> _rules = [];
    private readonly List<(string Bridge, uint Start, uint End, uint Gateway)> _bridges = [];

    public FakeZoneFirewall(IEnumerable<ZoneNetworkSpec> networks, string? iptables)
    {
        foreach (var network in networks)
        {
            ZonePolicyParser.TryParseCidr(network.Subnet, out var start, out var end);
            _bridges.Add((network.Bridge, start, end, start + 1));
        }
        foreach (var line in (iptables ?? string.Empty).Split('\n'))
        {
            if (!line.StartsWith("-A ", StringComparison.Ordinal)) continue;
            var tokens = Tokenize(line);
            _rules.Add((tokens[1], tokens.Skip(2).ToList()));
        }
    }

    public ZoneProbeOutcome Connect(string fromBridge, IPAddress address, int port)
    {
        if (_rules.Count == 0) return ZoneProbeOutcome.Connected;
        var raw = ToUInt(address);
        var toHost = _bridges.Any(bridge => bridge.Gateway == raw);
        var outInterface = _bridges.FirstOrDefault(bridge => raw >= bridge.Start && raw <= bridge.End).Bridge ?? "eth0";
        var chain = toHost ? ZonePolicyRules.InputChain : ZonePolicyRules.ForwardChain;
        foreach (var (ruleChain, tokens) in _rules.Where(rule => rule.Chain == chain))
        {
            if (!Matches(tokens, fromBridge, toHost ? null : outInterface, raw, port)) continue;
            var target = tokens[tokens.IndexOf("-j") + 1];
            return target switch
            {
                "RETURN" => ZoneProbeOutcome.Connected,
                "DROP" => ZoneProbeOutcome.TimedOut,
                "REJECT" => ZoneProbeOutcome.Refused,
                _ => throw new InvalidOperationException($"unexpected target {target}"),
            };
        }
        return ZoneProbeOutcome.Connected;
    }

    private static bool Matches(List<string> tokens, string inInterface, string? outInterface, uint address, int port)
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
                    if (tokens[++index] != outInterface) return false;
                    break;
                case "-d":
                    ZonePolicyParser.TryParseCidr(tokens[++index], out var start, out var end);
                    if (address < start || address > end) return false;
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
            }
        }
        return true;
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
}

/// <summary>A zone network seen through the fake firewall and fake DNS.</summary>
internal sealed class FakeZoneNetwork(
    IReadOnlyList<ZoneNetworkSpec> networks, Func<string?> loadedRules, FakeZoneResolver resolver) : IZoneNetworkProbe
{
    public Dictionary<string, List<string>> Published { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string Address, int Port), ZoneProbeObservation> Forced { get; } = new();
    public string? PrepareFailure { get; set; }
    public List<(string Network, string Address, int Port)> Connections { get; } = [];

    public Task PrepareAsync(CancellationToken ct)
        => PrepareFailure is null ? Task.CompletedTask : throw new InvalidOperationException(PrepareFailure);

    public Task<ZoneProbeObservation> ConnectAsync(string network, string address, int port, CancellationToken ct)
    {
        Connections.Add((network, address, port));
        if (Forced.TryGetValue((address, port), out var forced)) return Task.FromResult(forced);
        IPAddress target;
        if (!IPAddress.TryParse(address, out target!))
        {
            try { target = resolver.Resolve(address)[0]; }
            catch (SocketException)
            {
                return Task.FromResult(new ZoneProbeObservation(ZoneProbeOutcome.Unresolved, "Name or service not known"));
            }
        }
        var bridge = networks.Single(item => item.Name == network).Bridge;
        var outcome = new FakeZoneFirewall(networks, loadedRules()).Connect(bridge, target, port);
        return Task.FromResult(new ZoneProbeObservation(outcome, outcome.ToString()));
    }

    public Task<IReadOnlyList<string>> PublishedPortsAsync(string network, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Published.GetValueOrDefault(network) ?? []);
}

/// <summary>
/// A scripted Docker and iptables host. It keeps networks, the loaded zone
/// chains and the jumps as state, so a second apply observes the first.
/// </summary>
internal sealed class FakeZoneHost : IZoneHost
{
    public bool DockerUp { get; set; } = true;
    public bool Root { get; set; } = true;
    public Dictionary<string, string> Networks { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Jumps { get; } = new(StringComparer.Ordinal);
    public string? LoadedRules { get; set; }
    public List<string> Commands { get; } = [];

    public IEnumerable<string> Mutations => Commands.Where(command =>
        command.StartsWith("docker network create", StringComparison.Ordinal)
        || command == "iptables-restore --noflush"
        || command.StartsWith("iptables -I", StringComparison.Ordinal));

    public Task<ZoneCommandResult> RunAsync(
        string executable, IReadOnlyList<string> args, string? standardInput, CancellationToken ct)
    {
        var command = $"{executable} {string.Join(' ', args)}";
        Commands.Add(command);
        return Task.FromResult(Answer(executable, args, standardInput));
    }

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
        if (!Root) return new ZoneCommandResult(4, "", "iptables v1.8.10 (nf_tables): Permission denied (you must be root)");
        if (executable == "iptables-restore")
        {
            if (args.Contains("--test")) return new ZoneCommandResult(0, "", "");
            LoadedRules = standardInput;
            return new ZoneCommandResult(0, "", "");
        }
        return args switch
        {
            ["-C", var chain, "-j", var target] => Jumps.Contains($"{chain}>{target}")
                ? new ZoneCommandResult(0, "", "")
                : new ZoneCommandResult(1, "", "iptables: Bad rule (does a matching rule exist in that chain?)."),
            ["-I", var chain, "1", "-j", var target] => Insert(chain, target),
            _ => new ZoneCommandResult(1, "", "unexpected iptables command"),
        };
    }

    private ZoneCommandResult Create(IReadOnlyList<string> args)
    {
        string Option(string prefix) => args.First(arg => arg.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        var subnet = args[args.ToList().IndexOf("--subnet") + 1];
        Networks[args[^1]] = $"bridge|{subnet},|{Option("com.docker.network.bridge.name=")}|"
                             + Option("com.docker.network.bridge.enable_icc=");
        return new ZoneCommandResult(0, "network-id\n", "");
    }

    private ZoneCommandResult Insert(string chain, string target)
    {
        Jumps.Add($"{chain}>{target}");
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
        IReadOnlyCollection<string>? zones = null)
        => await ZonePolicyParser.ResolveAsync(ZonePolicyParser.Parse(json), ZonePolicyParser.Sha256(json),
            environment ?? Environment, resolver ?? new FakeZoneResolver(), CancellationToken.None, zones);
}
