using System.Net;
using System.Net.Sockets;
using AgentStudio.Projects;

namespace AgentStudio.Registry;

public enum ProjectUrlProbeVerdict
{
    Probe,
    LocalOnly,
}

/// <summary>
/// Readiness preflight hint for literal local addresses
/// (AGT-3411). A networked server that requests a loopback or link-local URL
/// reaches its own interfaces, not the laptop the URL was registered from, so
/// such a URL is reported as <c>local-only</c> instead of being probed. The one
/// exception is a process Studio itself runs for the URL on this host: its
/// loopback address is genuinely this host's. The shared connection-time
/// transport below is authoritative for all resolved destination addresses.
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

/// <summary>
/// Project-owned permission to connect to a running preview, not a permission
/// to access arbitrary local services. Only loopback at this exact origin and
/// declared port is eligible. Private networks and metadata never opt in.
/// </summary>
internal sealed record ProjectUrlLocalPreviewPermission(string ProjectId, string UrlId, Uri Origin)
{
    internal bool Allows(Uri target, IPAddress address)
        => ProjectUrlDestinationConnector.SameOrigin(Origin, target)
            && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}

/// <summary>
/// Shared transport for Project URL preview and readiness. Resolve once per
/// connection and pass only checked IP endpoints to the socket, never a host
/// name that could resolve again. Reuse environment health address classes.
/// </summary>
internal sealed class ProjectUrlDestinationConnector
{
    internal static readonly HttpRequestOptionsKey<(string ProjectId, string UrlId)> ProjectUrlOption = new("project-url-owner");
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, Task<Stream>> _connect;

    public ProjectUrlDestinationConnector() : this(Dns.GetHostAddressesAsync, ConnectSocketAsync) { }

    internal ProjectUrlDestinationConnector(Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, Task<Stream>> connect)
    {
        _resolve = resolve;
        _connect = connect;
    }

    internal static bool SameOrigin(Uri left, Uri right)
        => left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;

    internal static ProjectUrlLocalPreviewPermission? LocalPermission(ProjectRecord project, ProjectUrlRecord url,
        ProjectUrlProcessSnapshot? process, Uri target)
    {
        if (url.StartRule is not { } rule || process is null || !process.Started
            || process.ProjectId != project.Id || process.UrlId != url.Id
            || !ProjectUrlProcessStates.IsActive(process.State) || process.Command != rule.Command
            || !Uri.TryCreate(url.Url, UriKind.Absolute, out var registered)) return null;
        var port = rule.Port ?? registered.Port;
        var declared = new[] { registered, Uri.TryCreate(rule.HealthUrl, UriKind.Absolute, out var health) ? health : registered };
        return declared.Any(origin => origin.Scheme is "http" or "https"
                && SameOrigin(origin, target) && origin.Port == port && IsLoopbackHost(origin.Host))
            ? new(project.Id, url.Id, target) : null;
    }

    private static bool IsLoopbackHost(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(
                address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    internal static ProjectUrlLocalPreviewPermission? ResolvePermission(ProjectRegistry projects,
        ProjectUrlProcessService processes, HttpRequestMessage request)
    {
        var target = request.RequestUri!;
        request.Options.TryGetValue(ProjectUrlOption, out var owner);
        foreach (var project in projects.List())
        {
            if (owner.ProjectId is not null && owner.ProjectId != project.Id) continue;
            foreach (var url in project.Urls)
            {
                if (owner.UrlId is not null && owner.UrlId != url.Id) continue;
                var permission = LocalPermission(project, url, processes.Get(project.Id, url.Id), target);
                if (permission is not null) return permission;
            }
        }
        return null;
    }

    internal async Task<Stream> ConnectAsync(Uri target, ProjectUrlLocalPreviewPermission? permission,
        CancellationToken cancellationToken)
    {
        if (target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo))
            throw new HttpRequestException("Project URL destination is not allowed.");
        var host = target.IdnHost.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _resolve(host, cancellationToken);
        SocketException? last = null;
        foreach (var address in addresses)
        {
            if (!HealthCheckAddressPolicy.Allows(address, allowPrivateNetwork: false)
                && permission?.Allows(target, address) != true) continue;
            try
            {
                return await _connect(new IPEndPoint(address, target.Port), cancellationToken);
            }
            catch (SocketException ex)
            {
                // A failed public endpoint never permits a blocked fallback.
                last = ex;
            }
        }
        throw new HttpRequestException("Project URL destination is blocked or unreachable.", last);
    }

    internal SocketsHttpHandler CreateHandler(Func<HttpRequestMessage, ProjectUrlLocalPreviewPermission?> permission) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        // Re-evaluate ownership and DNS for every request, including a browser
        // following a rewritten redirect. No connection outlives its permission.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = (context, cancellationToken) => new ValueTask<Stream>(ConnectAsync(
            context.InitialRequestMessage.RequestUri!, permission(context.InitialRequestMessage), cancellationToken)),
    };

    private static async Task<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
