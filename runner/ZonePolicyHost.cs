using System.ComponentModel;
using System.Diagnostics;

namespace AgentRunner;

public sealed record ZoneCommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>The only way zone-policy code touches the host: docker and iptables commands, and kernel files.</summary>
public interface IZoneHost
{
    Task<ZoneCommandResult> RunAsync(
        string executable, IReadOnlyList<string> args, string? standardInput, CancellationToken ct);

    /// <summary>Whether a host path such as <c>/proc/net/if_inet6</c> exists.</summary>
    bool PathExists(string path);
}

/// <summary>Runs host commands as child processes with a bounded wait.</summary>
public sealed class ProcessZoneHost(TimeSpan timeout) : IZoneHost
{
    /// <summary>Exit code reported when the executable cannot be started at all.</summary>
    public const int NotStarted = 127;

    /// <summary>
    /// Exit code reported when the host command itself overran its wait. It
    /// is negative so it never reads as a probe's own connect timeout.
    /// </summary>
    public const int Overran = -1;

    public bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public async Task<ZoneCommandResult> RunAsync(
        string executable, IReadOnlyList<string> args, string? standardInput, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new ZoneCommandResult(NotStarted, string.Empty, $"{executable} did not start");
        }
        catch (Win32Exception exception)
        {
            return new ZoneCommandResult(NotStarted, string.Empty, $"{executable} could not start: {exception.Message}");
        }
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            process.StandardInput.Close();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new ZoneCommandResult(Overran, string.Empty, $"{executable} did not finish within {timeout.TotalSeconds:0}s");
        }
        return new ZoneCommandResult(process.ExitCode, await output, await error);
    }
}

public enum ZoneProbeOutcome
{
    Connected,

    /// <summary>"No route to host": the ICMP admin-prohibited reject the zone rules send.</summary>
    Rejected,

    /// <summary>"Connection refused": a reset from the destination itself.</summary>
    Refused,
    TimedOut,
    Unresolved,
    ProbeError,
}

public sealed record ZoneProbeObservation(ZoneProbeOutcome Outcome, string Detail, bool LocalDenyObserved = false);

/// <summary>What the verifier needs to observe a zone network.</summary>
public interface IZoneNetworkProbe
{
    /// <summary>Make the probe ready (for Docker: pull the image once, outside any probe's wait).</summary>
    Task PrepareAsync(CancellationToken ct);

    /// <summary>
    /// Open one TCP connection from a fresh container on <paramref name="network"/>,
    /// resolving names through <paramref name="dns"/> when given.
    /// </summary>
    Task<ZoneProbeObservation> ConnectAsync(string network, string? dns, string address, int port, CancellationToken ct);

    /// <summary>Connect and confirm that a matching local deny rule counted the packet.</summary>
    Task<ZoneProbeObservation> ConnectWithLocalDenyAsync(
        string network, string address, int port, CancellationToken ct);

    /// <summary>Containers attached to <paramref name="network"/> that publish a host port, as "name: ports".</summary>
    Task<IReadOnlyList<string>> PublishedPortsAsync(string network, CancellationToken ct);
}

/// <summary>
/// Probes with a throw-away container: bash's <c>/dev/tcp</c> opens the
/// connection under <c>timeout</c>, so the image needs only bash and
/// coreutils or busybox. The probe container gets no published port and no
/// mount.
/// </summary>
public sealed class DockerZoneNetworkProbe(IZoneHost host, string image, int timeoutSeconds) : IZoneNetworkProbe
{
    public const string ProbeLabel = "org.agent-studio.zone-probe=true";

    public async Task PrepareAsync(CancellationToken ct)
    {
        if ((await host.RunAsync("docker", ["image", "inspect", "--format", "{{.Id}}", image], null, ct)).Succeeded)
            return;
        var pull = await host.RunAsync("docker", ["pull", image], null, ct);
        if (!pull.Succeeded)
            throw new InvalidOperationException($"probe image {image} could not be pulled: {FirstLine(pull.Error)}");
    }

    public async Task<ZoneProbeObservation> ConnectAsync(
        string network, string? dns, string address, int port, CancellationToken ct)
    {
        var args = new List<string> { "run", "--rm", "--network", network };
        if (dns is not null) args.AddRange(["--dns", dns]);
        args.AddRange(
        [
            "--label", ProbeLabel, image,
            "timeout", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "bash", "-c", "exec 3<>\"/dev/tcp/$0/$1\"", address, port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);
        return Classify(await host.RunAsync("docker", args, null, ct));
    }

    public async Task<ZoneProbeObservation> ConnectWithLocalDenyAsync(
        string network, string address, int port, CancellationToken ct)
    {
        var bridge = await host.RunAsync("docker", ["network", "inspect", "--format",
            "{{index .Options \"com.docker.network.bridge.name\"}}", network], null, ct);
        if (!bridge.Succeeded || string.IsNullOrWhiteSpace(bridge.Output))
            return new ZoneProbeObservation(ZoneProbeOutcome.ProbeError, $"bridge lookup failed: {FirstLine(bridge.Error)}");
        var before = await host.RunAsync("iptables-save", ["-c", "-t", "filter"], null, ct);
        if (!before.Succeeded)
            return new ZoneProbeObservation(ZoneProbeOutcome.ProbeError, $"firewall counter read failed: {FirstLine(before.Error)}");
        var observation = await ConnectAsync(network, null, address, port, ct);
        var after = await host.RunAsync("iptables-save", ["-c", "-t", "filter"], null, ct);
        return observation with
        {
            LocalDenyObserved = after.Succeeded && ZoneFirewallEvidence.DenyHit(
                before.Output, after.Output, bridge.Output.Trim(), address),
        };
    }

    /// <summary>
    /// Exit 0 is a completed connection. 124 (coreutils) or 143 and 137
    /// (busybox, the child killed by TERM or KILL) is the probe's own timeout,
    /// a dropped connection. 125 to 127 are Docker or image failures and a
    /// negative code an overrun host command; neither proves anything. Of the
    /// connect errors, "No route to host" is the zone rules' reject and
    /// "Connection refused" the destination's reset; any other error is
    /// inconclusive.
    /// </summary>
    internal static ZoneProbeObservation Classify(ZoneCommandResult result)
    {
        var detail = FirstLine(result.Error);
        if (result.ExitCode == 0) return new ZoneProbeObservation(ZoneProbeOutcome.Connected, "connected");
        if (result.ExitCode is 124 or 137 or 143) return new ZoneProbeObservation(ZoneProbeOutcome.TimedOut, "timed out");
        if (result.ExitCode is < 0 or (>= 125 and <= 127))
            return new ZoneProbeObservation(ZoneProbeOutcome.ProbeError, $"probe container failed (exit {result.ExitCode}): {detail}");
        // glibc says "Name or service not known", musl (the Alpine-based bash
        // image) "Name does not resolve".
        if (result.Error.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("Name does not resolve", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("name resolution", StringComparison.OrdinalIgnoreCase))
            return new ZoneProbeObservation(ZoneProbeOutcome.Unresolved, detail);
        if (result.Error.Contains("No route to host", StringComparison.OrdinalIgnoreCase))
            return new ZoneProbeObservation(ZoneProbeOutcome.Rejected, detail);
        if (result.Error.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return new ZoneProbeObservation(ZoneProbeOutcome.Refused, detail);
        return new ZoneProbeObservation(ZoneProbeOutcome.ProbeError,
            $"inconclusive connect error (exit {result.ExitCode}): {(detail.Length > 0 ? detail : "no message")}");
    }

    public async Task<IReadOnlyList<string>> PublishedPortsAsync(string network, CancellationToken ct)
    {
        var result = await host.RunAsync("docker",
            ["ps", "--filter", $"network={network}", "--format", "{{.Names}}\t{{.Ports}}"], null, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"docker ps for network {network} failed: {FirstLine(result.Error)}");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && parts[1].Contains("->", StringComparison.Ordinal))
            .Select(parts => $"{parts[0]}: {parts[1]}")
            .ToList();
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
}
