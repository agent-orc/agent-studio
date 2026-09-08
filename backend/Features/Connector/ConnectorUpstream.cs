using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Connector;

public sealed record ConnectorUpstreamSnapshot(
    string Mode,
    Uri BaseUri,
    string? TlsCertificateSha256,
    ConnectorCredential? Credential,
    int MinimumProtocol,
    int MaximumProtocol,
    long Generation,
    string MaskedName);

public sealed record ConnectorUpstreamProbe(
    bool Ready,
    string? FailureCode,
    int Protocol,
    DateTimeOffset? SuccessfulAtUtc);

public sealed record ConnectorUpstreamSwitchEvidence(
    bool ExactVersionRestoreVerified,
    bool PreviousAuthorityDrainedOrReadOnly);

public sealed record ConnectorUpstreamSwitchResult(bool Switched, string? FailureCode, long Generation);

public interface IConnectorUpstreamTransport
{
    Task<HttpResponseMessage> SendAsync(
        ConnectorUpstreamSnapshot snapshot,
        HttpRequestMessage request,
        CancellationToken cancellationToken);

    Task<ClientWebSocket> ConnectWebSocketAsync(
        ConnectorUpstreamSnapshot snapshot,
        Uri uri,
        IReadOnlyList<string> subProtocols,
        string? clientId,
        CancellationToken cancellationToken);
}

public sealed class ConnectorUpstreamTransport : IConnectorUpstreamTransport, IDisposable
{
    private readonly ConcurrentDictionary<long, HttpClient> _clients = new();

    public Task<HttpResponseMessage> SendAsync(
        ConnectorUpstreamSnapshot snapshot,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AddConnectorHeaders(request.Headers, snapshot);
        var client = _clients.GetOrAdd(snapshot.Generation, _ => CreateClient(snapshot));
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(
        ConnectorUpstreamSnapshot snapshot,
        Uri uri,
        IReadOnlyList<string> subProtocols,
        string? clientId,
        CancellationToken cancellationToken)
    {
        if (snapshot.Credential is null)
            throw new InvalidOperationException("The Studio credential is not loaded.");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {snapshot.Credential.Bearer}");
        socket.Options.SetRequestHeader(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        socket.Options.SetRequestHeader(
            TaskServerProtocol.ClientVersionHeaderName,
            typeof(ConnectorUpstreamTransport).Assembly.GetName().Version?.ToString(3) ?? "unknown");
        if (!string.IsNullOrWhiteSpace(clientId))
            socket.Options.SetRequestHeader("X-Client-Id", clientId);
        foreach (var protocol in subProtocols) socket.Options.AddSubProtocol(protocol);
        if (snapshot.TlsCertificateSha256 is not null)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                CertificateMatches(snapshot.TlsCertificateSha256, certificate, chain, errors);
        }
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }

    internal static void AddConnectorHeaders(HttpRequestHeaders headers, ConnectorUpstreamSnapshot snapshot)
    {
        if (snapshot.Credential is null)
            throw new InvalidOperationException("The Studio credential is not loaded.");
        headers.Remove("Authorization");
        headers.Remove(TaskServerProtocol.HeaderName);
        headers.Remove(TaskServerProtocol.ClientVersionHeaderName);
        headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.Credential.Bearer);
        headers.TryAddWithoutValidation(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        headers.TryAddWithoutValidation(
            TaskServerProtocol.ClientVersionHeaderName,
            typeof(ConnectorUpstreamTransport).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    }

    private static HttpClient CreateClient(ConnectorUpstreamSnapshot snapshot)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        if (snapshot.TlsCertificateSha256 is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                CertificateMatches(snapshot.TlsCertificateSha256, certificate, chain, errors);
        }
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(100),
        };
    }

    private static bool CertificateMatches(
        string expectedSha256,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return false;
        using var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        var now = DateTime.UtcNow;
        if (certificate2.NotBefore.ToUniversalTime() > now || certificate2.NotAfter.ToUniversalTime() < now)
            return false;
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate2.RawData));
        return string.Equals(fingerprint, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ConnectorUpstreamManager
{
    private readonly IConnectorUpstreamTransport _transport;
    private readonly ConnectorSessionStore _sessions;
    private ConnectorUpstreamSnapshot _current;
    private long _lastSuccessfulProbeUnixMilliseconds;

    public ConnectorUpstreamManager(
        ConnectorOptions options,
        IConnectorCredentialSource credentials,
        IConnectorUpstreamTransport transport,
        ConnectorSessionStore sessions)
    {
        _transport = transport;
        _sessions = sessions;
        var credential = credentials.Load(options).Credential;
        _current = new ConnectorUpstreamSnapshot(
            options.UpstreamMode,
            options.UpstreamBaseUri,
            options.TlsCertificateSha256,
            credential,
            TaskServerProtocol.MinimumSupported,
            TaskServerProtocol.MaximumSupported,
            options.Generation,
            options.MaskedUpstreamName);
    }

    public ConnectorUpstreamSnapshot Capture() => Volatile.Read(ref _current);

    public async Task<ConnectorUpstreamProbe> ProbeAsync(
        ConnectorUpstreamSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Credential is null)
            return new ConnectorUpstreamProbe(false, "credential-unavailable", TaskServerProtocol.Current, null);
        try
        {
            using var readiness = new HttpRequestMessage(HttpMethod.Get, new Uri(snapshot.BaseUri, "readyz"));
            ConnectorUpstreamTransport.AddConnectorHeaders(readiness.Headers, snapshot);
            using var readyResponse = await _transport.SendAsync(snapshot, readiness, cancellationToken);
            if (!readyResponse.IsSuccessStatusCode)
                return new ConnectorUpstreamProbe(false, "upstream-not-ready", TaskServerProtocol.Current, null);

            using var compatibility = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(snapshot.BaseUri, "api/v1/protocol/compatibility"))
            {
                Content = JsonContent.Create(new ProtocolCompatibilityRequest(
                    "studio",
                    typeof(ConnectorUpstreamManager).Assembly.GetName().Version?.ToString(3) ?? "unknown",
                    TaskServerProtocol.Current)),
            };
            ConnectorUpstreamTransport.AddConnectorHeaders(compatibility.Headers, snapshot);
            using var compatibilityResponse = await _transport.SendAsync(snapshot, compatibility, cancellationToken);
            if (!compatibilityResponse.IsSuccessStatusCode)
                return new ConnectorUpstreamProbe(false, "protocol-incompatible", TaskServerProtocol.Current, null);
            var response = await compatibilityResponse.Content.ReadFromJsonAsync<ProtocolCompatibilityResponse>(cancellationToken);
            if (response is null || !response.Supported
                || response.Server.MinimumSupported > snapshot.MaximumProtocol
                || response.Server.MaximumSupported < snapshot.MinimumProtocol)
                return new ConnectorUpstreamProbe(false, "protocol-incompatible", TaskServerProtocol.Current, null);

            var successfulAt = DateTimeOffset.UtcNow;
            Volatile.Write(ref _lastSuccessfulProbeUnixMilliseconds, successfulAt.ToUnixTimeMilliseconds());
            return new ConnectorUpstreamProbe(true, null, TaskServerProtocol.Current, successfulAt);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or OperationCanceledException
                                          or InvalidOperationException
                                          or JsonException)
        {
            return new ConnectorUpstreamProbe(false, "upstream-unavailable", TaskServerProtocol.Current, null);
        }
    }

    public async Task<ConnectorUpstreamSwitchResult> TrySwitchAsync(
        ConnectorUpstreamSnapshot candidate,
        ConnectorUpstreamSwitchEvidence evidence,
        CancellationToken cancellationToken)
    {
        var observed = Capture();
        if (candidate.Generation <= observed.Generation)
            return new ConnectorUpstreamSwitchResult(false, "generation-not-newer", observed.Generation);
        if (!evidence.ExactVersionRestoreVerified)
            return new ConnectorUpstreamSwitchResult(false, "exact-version-restore-unverified", observed.Generation);
        if (!evidence.PreviousAuthorityDrainedOrReadOnly)
            return new ConnectorUpstreamSwitchResult(false, "previous-authority-writable", observed.Generation);

        var probe = await ProbeAsync(candidate, cancellationToken);
        if (!probe.Ready)
            return new ConnectorUpstreamSwitchResult(false, probe.FailureCode, observed.Generation);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _current, candidate, observed), observed))
            return new ConnectorUpstreamSwitchResult(false, "generation-changed", Capture().Generation);

        _sessions.RotateAll();
        return new ConnectorUpstreamSwitchResult(true, null, candidate.Generation);
    }

    public DateTimeOffset? LastSuccessfulProbeUtc
    {
        get
        {
            var value = Volatile.Read(ref _lastSuccessfulProbeUnixMilliseconds);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }
}
