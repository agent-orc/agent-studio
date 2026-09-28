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

/// <summary>
/// Result of one attach handshake against the upstream Task Server. A ready
/// probe carries the negotiated <c>/api/v1</c> and Studio hub versions; a
/// refused probe carries a stable code and an operator-readable reason.
/// </summary>
public sealed record ConnectorUpstreamProbe(
    bool Ready,
    string? FailureCode,
    int Protocol,
    DateTimeOffset? SuccessfulAtUtc,
    string? FailureReason = null,
    int? HubProtocol = null,
    string? ServerVersion = null);

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
        int apiProtocol,
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
        if (request.Headers.Authorization is null)
            AddConnectorHeaders(request.Headers, snapshot, TaskServerProtocol.Current);
        var client = _clients.GetOrAdd(snapshot.Generation, _ => CreateClient(snapshot));
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(
        ConnectorUpstreamSnapshot snapshot,
        Uri uri,
        IReadOnlyList<string> subProtocols,
        string? clientId,
        int apiProtocol,
        CancellationToken cancellationToken)
    {
        if (snapshot.Credential is null)
            throw new InvalidOperationException("The Studio credential is not loaded.");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {snapshot.Credential.Bearer}");
        socket.Options.SetRequestHeader(TaskServerProtocol.HeaderName, apiProtocol.ToString());
        socket.Options.SetRequestHeader(TaskServerProtocol.ClientVersionHeaderName, ConnectorVersion);
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

    internal static string ConnectorVersion
        => typeof(ConnectorUpstreamTransport).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    internal static void AddConnectorHeaders(
        HttpRequestHeaders headers,
        ConnectorUpstreamSnapshot snapshot,
        int apiProtocol)
    {
        if (snapshot.Credential is null)
            throw new InvalidOperationException("The Studio credential is not loaded.");
        headers.Remove("Authorization");
        headers.Remove(TaskServerProtocol.HeaderName);
        headers.Remove(TaskServerProtocol.ClientVersionHeaderName);
        headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.Credential.Bearer);
        headers.TryAddWithoutValidation(TaskServerProtocol.HeaderName, apiProtocol.ToString());
        headers.TryAddWithoutValidation(TaskServerProtocol.ClientVersionHeaderName, ConnectorVersion);
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
    /// <summary>How long a successful attach is trusted before the next request renegotiates.</summary>
    internal static readonly TimeSpan AttachedRecheckInterval = TimeSpan.FromSeconds(60);
    /// <summary>How long a refused attach is reported before the next request retries the handshake.</summary>
    internal static readonly TimeSpan RefusedRecheckInterval = TimeSpan.FromSeconds(2);

    private readonly ConnectorCredentialProvider _credentials;
    private readonly IConnectorUpstreamTransport _transport;
    private readonly ConnectorSessionStore _sessions;
    private readonly ConnectorProtocolRange _protocols;
    private readonly string _expectedHubPath;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private ConnectorUpstreamSnapshot _current;
    private CachedAttachment? _attachment;
    private long _lastSuccessfulProbeUnixMilliseconds;

    public ConnectorUpstreamManager(
        ConnectorOptions options,
        ConnectorCredentialProvider credentials,
        IConnectorUpstreamTransport transport,
        ConnectorSessionStore sessions,
        ConnectorProtocolRange protocols,
        ConnectorRouteInventory inventory,
        TimeProvider time)
    {
        _credentials = credentials;
        _transport = transport;
        _sessions = sessions;
        _protocols = protocols;
        _time = time;
        _expectedHubPath = inventory.TaskServerOperations.Single(operation => operation.Method == "WS").TargetRoute;
        _current = new ConnectorUpstreamSnapshot(
            options.UpstreamMode,
            options.UpstreamBaseUri,
            options.TlsCertificateSha256,
            credentials.Current().Credential,
            protocols.MinimumApi,
            protocols.MaximumApi,
            options.Generation,
            options.MaskedUpstreamName);
    }

    /// <summary>
    /// The current upstream with the current credential. A rotated credential
    /// replaces the snapshot's credential in place; the upstream itself changes
    /// only through <see cref="TrySwitchAsync"/>.
    /// </summary>
    public ConnectorUpstreamSnapshot Capture()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _current);
            var credential = _credentials.Current().Credential;
            if (Equals(observed.Credential, credential)) return observed;
            var refreshed = observed with { Credential = credential };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, refreshed, observed), observed))
                return refreshed;
        }
    }

    /// <summary>
    /// The attach gate every forwarded request passes. A successful handshake
    /// is reused for <see cref="AttachedRecheckInterval"/> as long as neither
    /// the upstream generation nor the credential changed.
    /// </summary>
    public async Task<ConnectorUpstreamProbe> EnsureAttachedAsync(
        ConnectorUpstreamSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var key = new AttachmentKey(snapshot.Generation, _credentials.Revision);
        if (TryFreshAttachment(key) is { } fresh) return fresh;

        await _attachGate.WaitAsync(cancellationToken);
        try
        {
            if (TryFreshAttachment(key) is { } raced) return raced;
            var probe = await ProbeAsync(snapshot, cancellationToken);
            Volatile.Write(ref _attachment, new CachedAttachment(key, probe, _time.GetUtcNow()));
            return probe;
        }
        finally
        {
            _attachGate.Release();
        }
    }

    /// <summary>The upstream refused the Studio credential: re-read it and renegotiate on the next request.</summary>
    public void ReportCredentialRejected()
    {
        _credentials.Invalidate();
        InvalidateAttachment();
    }

    /// <summary>The upstream answered a protocol rejection: renegotiate on the next request.</summary>
    public void InvalidateAttachment() => Volatile.Write(ref _attachment, null);

    public async Task<ConnectorUpstreamProbe> ProbeAsync(
        ConnectorUpstreamSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Credential is null)
        {
            var loaded = _credentials.Current();
            return Refused(
                loaded.FailureCode ?? ConnectorCredentialFailureCodes.Unavailable,
                loaded.FailureMessage ?? "The Studio credential is not available.");
        }
        try
        {
            using var readiness = new HttpRequestMessage(HttpMethod.Get, new Uri(snapshot.BaseUri, "readyz"));
            ConnectorUpstreamTransport.AddConnectorHeaders(readiness.Headers, snapshot, TaskServerProtocol.Current);
            using var readyResponse = await _transport.SendAsync(snapshot, readiness, cancellationToken);
            if (!readyResponse.IsSuccessStatusCode)
            {
                return Refused(
                    ConnectorAttachFailureCodes.UpstreamNotReady,
                    $"The {snapshot.MaskedName} answered readiness with HTTP {(int)readyResponse.StatusCode}; its authority is not restored yet.");
            }

            using var attach = new HttpRequestMessage(HttpMethod.Post, new Uri(snapshot.BaseUri, "api/v1/protocol/attach"))
            {
                Content = JsonContent.Create(new ProtocolAttachRequest(
                    TaskServerProtocol.StudioClientKind,
                    ConnectorUpstreamTransport.ConnectorVersion,
                    snapshot.MinimumProtocol,
                    snapshot.MaximumProtocol,
                    _protocols.MinimumHub,
                    _protocols.MaximumHub)),
            };
            ConnectorUpstreamTransport.AddConnectorHeaders(attach.Headers, snapshot, snapshot.MaximumProtocol);
            using var attachResponse = await _transport.SendAsync(snapshot, attach, cancellationToken);
            var negotiation = await ReadAttachResponseAsync(attachResponse, cancellationToken);
            var decision = ConnectorAttachPolicy.Evaluate(
                _protocols with { MinimumApi = snapshot.MinimumProtocol, MaximumApi = snapshot.MaximumProtocol },
                (int)attachResponse.StatusCode,
                negotiation,
                _expectedHubPath);
            if (!decision.Attached)
            {
                if (decision.FailureCode == ConnectorAttachFailureCodes.CredentialRejected) _credentials.Invalidate();
                return Refused(decision.FailureCode!, decision.FailureReason!, decision.ServerVersion);
            }

            var successfulAt = _time.GetUtcNow();
            Volatile.Write(ref _lastSuccessfulProbeUnixMilliseconds, successfulAt.ToUnixTimeMilliseconds());
            return new ConnectorUpstreamProbe(
                true,
                null,
                decision.ApiProtocol!.Value,
                successfulAt,
                HubProtocol: decision.HubProtocol,
                ServerVersion: decision.ServerVersion);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or OperationCanceledException
                                          or InvalidOperationException)
        {
            return Refused(
                ConnectorAttachFailureCodes.UpstreamUnavailable,
                $"The connector could not reach the {snapshot.MaskedName} ({exception.GetType().Name}). " +
                "Check the SSH forward or WireGuard link and Connector:Upstream:BaseUrl.");
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

        Volatile.Write(
            ref _attachment,
            new CachedAttachment(new AttachmentKey(candidate.Generation, _credentials.Revision), probe, _time.GetUtcNow()));
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

    private ConnectorUpstreamProbe? TryFreshAttachment(AttachmentKey key)
    {
        var cached = Volatile.Read(ref _attachment);
        if (cached is null || cached.Key != key) return null;
        var ttl = cached.Probe.Ready ? AttachedRecheckInterval : RefusedRecheckInterval;
        return _time.GetUtcNow() - cached.CheckedAtUtc < ttl ? cached.Probe : null;
    }

    private static async Task<ProtocolAttachResponse?> ReadAttachResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            return null;
        try
        {
            return await response.Content.ReadFromJsonAsync<ProtocolAttachResponse>(cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ConnectorUpstreamProbe Refused(string code, string reason, string? serverVersion = null)
        => new(false, code, TaskServerProtocol.Current, null, reason, ServerVersion: serverVersion);

    private readonly record struct AttachmentKey(long Generation, long CredentialRevision);

    private sealed record CachedAttachment(AttachmentKey Key, ConnectorUpstreamProbe Probe, DateTimeOffset CheckedAtUtc);
}
