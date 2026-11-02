using System.Net;
using System.Net.Sockets;

namespace AgentStudio.Registry;

public enum ProjectUrlProbeVerdict
{
    Probe,
    LocalOnly,
}

/// <summary>
/// Single owner of the rule whether the Studio host may request a Project URL
/// (AGT-3411). A networked server that requests a loopback or link-local URL
/// reaches its own interfaces, not the laptop the URL was registered from, so
/// such a URL is reported as <c>local-only</c> instead of being probed. The one
/// exception is a process Studio itself runs for the URL on this host: its
/// loopback address is genuinely this host's.
/// </summary>
public static class ProjectUrlProbePolicy
{
    public const string LocalOnly = "local-only";

    public const string LocalOnlyDetail =
        "This URL points at a loopback or link-local address, which the networked server does not request. "
        + "Open it on the machine that serves it.";

    public static ProjectUrlProbeVerdict Decide(string? url, bool networked, bool hostOwnsActiveProcess)
    {
        if (!networked || hostOwnsActiveProcess) return ProjectUrlProbeVerdict.Probe;
        return Uri.TryCreate(url, UriKind.Absolute, out var target) && IsLoopbackOrLinkLocal(target.Host)
            ? ProjectUrlProbeVerdict.LocalOnly
            : ProjectUrlProbeVerdict.Probe;
    }

    /// <summary>Host-scoped check on the literal host. Names other than localhost are not resolved.</summary>
    internal static bool IsLoopbackOrLinkLocal(string host)
    {
        host = host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 0.0.0.0/8 addresses this host; 169.254.0.0/16 is IPv4 link-local.
            return bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254;
        }
        return address.IsIPv6LinkLocal;
    }
}
