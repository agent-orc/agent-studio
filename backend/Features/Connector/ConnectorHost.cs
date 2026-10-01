using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Connector;

public static class ConnectorHost
{
    public static void ConfigureConnectorProfile(this WebApplicationBuilder builder)
    {
        var options = ConnectorOptions.Load(builder.Configuration);
        ValidateConfiguredListener(builder.Configuration);

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.IPv6Loopback, 5031));

        builder.Services.RemoveAll<IHostedService>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(_ => ConnectorRouteInventory.Load());
        builder.Services.AddSingleton(services => new ConnectorSessionStore(
            options.SessionLifetime,
            services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<IConnectorCredentialSource, ConnectorCredentialSource>();
        builder.Services.AddSingleton<ConnectorCredentialProvider>();
        builder.Services.AddSingleton(ConnectorProtocolRange.Supported);
        builder.Services.AddSingleton<IConnectorUpstreamTransport, ConnectorUpstreamTransport>();
        builder.Services.AddSingleton<ConnectorUpstreamManager>();
        builder.Services.AddSingleton<ConnectorProxy>();
    }

    public static async Task RunConnectorProfileAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ConnectorOptions>();
        app.UseRouting();
        var webSockets = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        foreach (var origin in options.StudioOrigins) webSockets.AllowedOrigins.Add(origin);
        app.UseWebSockets(webSockets);
        app.UseMiddleware<ConnectorSecurityMiddleware>();
        ConnectorRouteSurface.MapAndValidate(
            app,
            app.Services.GetRequiredService<ConnectorRouteInventory>());
        app.Lifetime.ApplicationStarted.Register(() => _ = LogInitialAttachAsync(app));
        await app.RunAsync();
    }

    /// <summary>
    /// Attaches once at startup so the operator log states immediately whether
    /// the upstream accepted the credential and a shared protocol version. The
    /// outcome is cached; browser requests reuse it instead of renegotiating.
    /// </summary>
    private static async Task LogInitialAttachAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentStudio.Connector");
        try
        {
            var upstream = app.Services.GetRequiredService<ConnectorUpstreamManager>();
            var snapshot = upstream.Capture();
            var attach = await upstream.EnsureAttachedAsync(snapshot, app.Lifetime.ApplicationStopping);
            if (attach.Ready)
            {
                logger.LogInformation(
                    "Connector attached to {Upstream} (generation {Generation}, Task Server {ServerVersion}) with /api/v1 protocol {ApiProtocol} and Studio hub protocol {HubProtocol}.",
                    snapshot.MaskedName, snapshot.Generation, attach.ServerVersion, attach.Protocol, attach.HubProtocol);
            }
            else
            {
                logger.LogWarning(
                    "Connector attach to {Upstream} (generation {Generation}) refused: {FailureCode}. {FailureReason} Studio requests are answered with 503 until the attach succeeds.",
                    snapshot.MaskedName, snapshot.Generation, attach.FailureCode, attach.FailureReason);
            }
        }
        catch (OperationCanceledException exception)
        {
            SilentCatch.Note(exception, "Connector startup attach is abandoned when the host stops.");
        }
    }

    internal static void ValidateConfiguredListener(IConfiguration configuration)
    {
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new InvalidOperationException(
                "The connector owns its only Kestrel endpoint and does not accept configured endpoints.");
        }

        var configuredUrls = configuration[WebHostDefaults.ServerUrlsKey];
        if (string.IsNullOrWhiteSpace(configuredUrls)) return;
        var urls = configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Any(url => !string.Equals(url, "http://[::1]:5031", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The connector may listen only on http://[::1]:5031.");
    }
}
