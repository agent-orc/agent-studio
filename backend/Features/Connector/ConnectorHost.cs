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
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(_ => ConnectorRouteInventory.Load());
        builder.Services.AddSingleton<ConnectorSessionStore>();
        builder.Services.AddSingleton<IConnectorCredentialSource, ConnectorCredentialSource>();
        builder.Services.AddSingleton<IConnectorUpstreamTransport, ConnectorUpstreamTransport>();
        builder.Services.AddSingleton<ConnectorUpstreamManager>();
        builder.Services.AddSingleton<ConnectorProxy>();
    }

    public static async Task RunConnectorProfileAsync(this WebApplication app)
    {
        app.UseRouting();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            AllowedOrigins = { app.Services.GetRequiredService<ConnectorOptions>().StudioOrigin },
        });
        app.UseMiddleware<ConnectorSecurityMiddleware>();
        ConnectorRouteSurface.MapAndValidate(
            app,
            app.Services.GetRequiredService<ConnectorRouteInventory>());
        await app.RunAsync();
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
