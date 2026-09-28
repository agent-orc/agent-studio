using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using AgentStudio.Connector;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Tests;

/// <summary>
/// How a connector host under test is wired. A null transport or credential
/// source keeps the production implementation, which the real-process
/// negative matrix relies on.
/// </summary>
internal sealed record ConnectorTestSetup(
    IConnectorUpstreamTransport? Transport = null,
    IConnectorCredentialSource? Credentials = null,
    ConnectorProtocolRange? Protocols = null,
    IReadOnlyDictionary<string, string?>? Settings = null);

/// <summary>
/// A booted connector host and its client. WebApplicationFactory runs the
/// real entry point inside the xunit process, so
/// <see cref="ConnectorHost.ValidateConfiguredListener"/> reads the
/// configuration of whichever process started the test run. The host
/// therefore boots with the ambient listener configuration removed: the
/// connector owns exactly one endpoint and must not inherit a listener from
/// its launcher. Callers run in the serial WebApplicationFactory collection,
/// so no other host boots while the ambient configuration is set aside.
/// </summary>
internal sealed class ConnectorUnderTest : IAsyncDisposable
{
    public static readonly Uri BaseAddress = new("http://[::1]:5031");

    private readonly WebApplicationFactory<Program> _factory;

    private ConnectorUnderTest(WebApplicationFactory<Program> factory, HttpClient client)
    {
        _factory = factory;
        Client = client;
    }

    public HttpClient Client { get; }
    public IServiceProvider Services => _factory.Services;

    public static ConnectorUnderTest Boot(RecordingTransport transport)
        => Boot(new ConnectorTestSetup(transport, new MutableCredentialSource("studio-secret")));

    public static ConnectorUnderTest Boot(ConnectorTestSetup setup)
    {
        using var owned = new OwnedListenerConfiguration();
        var factory = BuildFactory(setup);
        try
        {
            // CreateClient starts the host, so the boot - and with it the
            // listener guard - has to happen inside the scope rather than
            // at the first request.
            return new ConnectorUnderTest(factory, NewClient(factory));
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    /// <summary>A second browser: its own cookie jar against the same connector.</summary>
    public HttpClient NewBrowser() => NewClient(_factory);

    /// <summary>A client without a cookie jar, for replaying captured cookies verbatim.</summary>
    public HttpClient NewCookielessBrowser()
    {
        var client = _factory.Server.CreateClient();
        client.BaseAddress = BaseAddress;
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    private static WebApplicationFactory<Program> BuildFactory(ConnectorTestSetup setup)
    {
        var settings = new Dictionary<string, string?>
        {
            [ConnectorProfile.ConfigurationKey] = "connector",
            ["Connector:Mode"] = "native",
            ["Connector:Upstream:Mode"] = "remote",
            ["Connector:Upstream:BaseUrl"] = "https://task-server.invalid",
        };
        foreach (var (key, value) in setup.Settings ?? new Dictionary<string, string?>())
            settings[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
            {
                if (setup.Credentials is not null)
                {
                    services.RemoveAll<IConnectorCredentialSource>();
                    services.AddSingleton(setup.Credentials);
                }
                if (setup.Transport is not null)
                {
                    services.RemoveAll<IConnectorUpstreamTransport>();
                    services.AddSingleton(setup.Transport);
                }
                if (setup.Protocols is not null)
                {
                    services.RemoveAll<ConnectorProtocolRange>();
                    services.AddSingleton(setup.Protocols);
                }
            });
        });
    }

    private static HttpClient NewClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    /// <summary>
    /// Removes every variable that carries listener configuration
    /// (<see cref="HostListenerEnvironment"/>) for the duration of a host boot
    /// and restores the process environment afterwards.
    /// </summary>
    private sealed class OwnedListenerConfiguration : IDisposable
    {
        private readonly List<KeyValuePair<string, string?>> _removed = [];

        public OwnedListenerConfiguration()
        {
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var name = (string)entry.Key;
                if (!HostListenerEnvironment.Carries(name)) continue;
                _removed.Add(new KeyValuePair<string, string?>(name, entry.Value as string));
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _removed) Environment.SetEnvironmentVariable(name, value);
        }
    }
}

/// <summary>Browser-side helpers: session bootstrap, cookie reads, and a mutation carrying Origin and CSRF.</summary>
internal static class ConnectorBrowser
{
    public static async Task<HttpResponseMessage> SessionAsync(HttpClient client, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connector/session");
        request.Headers.Add("Origin", origin ?? ConnectorOptions.DefaultStudioOrigin);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    public static async Task<string> CsrfAsync(HttpClient client, string? origin = null)
    {
        using var session = await SessionAsync(client, origin);
        return ReadCookie(session, ConnectorSessionStore.CsrfCookieName);
    }

    public static string ReadCookie(HttpResponseMessage response, string name)
    {
        var prefix = name + "=";
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..cookie.IndexOf(';')];
    }

    public static HttpRequestMessage Mutation(
        HttpMethod method,
        string path,
        string? origin,
        string? csrf,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (csrf is not null) request.Headers.Add(ConnectorSessionStore.CsrfHeaderName, csrf);
        request.Content = JsonContent.Create(body ?? new { });
        return request;
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            return json.ValueKind == System.Text.Json.JsonValueKind.Object && json.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>A credential store the test rotates in place, like an operator rewriting the Credential Manager entry.</summary>
internal sealed class MutableCredentialSource(string? bearer) : IConnectorCredentialSource
{
    private string? _bearer = bearer;
    public int Loads;

    public void Rotate(string? bearer) => Volatile.Write(ref _bearer, bearer);

    public ConnectorCredentialLoadResult Load(ConnectorOptions options)
    {
        Interlocked.Increment(ref Loads);
        var bearer = Volatile.Read(ref _bearer);
        return bearer is null
            ? new ConnectorCredentialLoadResult(null, ConnectorCredentialFailureCodes.Unavailable, "No Studio credential is stored.")
            : new ConnectorCredentialLoadResult(new ConnectorCredential(bearer), null);
    }
}

internal sealed record ObservedRequest(
    string Method,
    string Path,
    string? Authorization,
    string? Protocol,
    string? ClientId,
    bool HasCookie,
    bool HasForwardedFor);

/// <summary>
/// Stands in for the Task Server: answers readiness and the attach handshake,
/// and records every forwarded Studio request.
/// </summary>
internal sealed class RecordingTransport : IConnectorUpstreamTransport
{
    private readonly object _gate = new();
    private readonly List<ObservedRequest> _proxied = [];

    public IReadOnlyList<ObservedRequest> ProxiedRequests
    {
        get { lock (_gate) return _proxied.ToArray(); }
    }

    public List<string?> AttachAuthorizations { get; } = [];

    /// <summary>The Task Server's answer to an attach offer; defaults to a matching server.</summary>
    public Func<ProtocolAttachRequest, string?, (HttpStatusCode Status, object Body)> Attach { get; set; } =
        (request, _) => (HttpStatusCode.OK, MatchingServer(request));

    /// <summary>Optional status the fake Task Server answers forwarded Studio requests with.</summary>
    public Func<ObservedRequest, HttpStatusCode> ForwardStatus { get; set; } = _ => HttpStatusCode.OK;

    public static ProtocolRangeDto ServerRange(int minimumApi = 1, int maximumApi = 2, int minimumHub = 1, int maximumHub = 1)
        => new(
            maximumApi,
            minimumApi,
            maximumApi,
            "test-server",
            "task-server",
            ["studio"],
            null,
            [new HubProtocolRangeDto("studio", TaskServerHubProtocol.StudioHubPath, maximumHub, minimumHub, maximumHub)]);

    public static ProtocolAttachResponse MatchingServer(ProtocolAttachRequest request)
        => new(
            true,
            ProtocolAttachCodes.Attached,
            ServerRange(),
            Math.Min(request.MaximumApiProtocol, TaskServerProtocol.MaximumSupported),
            Math.Min(request.MaximumHubProtocol, TaskServerHubProtocol.MaximumSupported),
            TaskServerHubProtocol.StudioHubPath,
            "studio-robert-windows",
            null);

    public async Task<HttpResponseMessage> SendAsync(
        ConnectorUpstreamSnapshot snapshot,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/readyz") return Json(HttpStatusCode.OK, new { status = "ready" });
        if (path == "/api/v1/protocol/attach")
        {
            var offer = await request.Content!.ReadFromJsonAsync<ProtocolAttachRequest>(cancellationToken);
            var authorization = request.Headers.Authorization?.ToString();
            lock (_gate) AttachAuthorizations.Add(authorization);
            var (status, body) = Attach(offer!, authorization);
            return Json(status, body);
        }

        var observed = new ObservedRequest(
            request.Method.Method,
            path,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues(TaskServerProtocol.HeaderName, out var protocols) ? protocols.SingleOrDefault() : null,
            request.Headers.TryGetValues("X-Client-Id", out var clientIds) ? clientIds.SingleOrDefault() : null,
            request.Headers.Contains("Cookie"),
            request.Headers.Contains("X-Forwarded-For"));
        lock (_gate) _proxied.Add(observed);
        return Json(ForwardStatus(observed), new { proxied = true });
    }

    public Task<ClientWebSocket> ConnectWebSocketAsync(
        ConnectorUpstreamSnapshot snapshot,
        Uri uri,
        IReadOnlyList<string> subProtocols,
        string? clientId,
        int apiProtocol,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
        => new(status) { Content = JsonContent.Create(body, body.GetType()) };
}
