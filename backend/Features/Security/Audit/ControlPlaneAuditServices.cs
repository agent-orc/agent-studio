namespace AgentStudio.Security;

public static class ControlPlaneAuditServices
{
    /// <summary>
    /// Registers the control plane audit writer (owned by the access store),
    /// the configured off-host sink, the forwarder, and the verification job.
    /// </summary>
    public static IServiceCollection AddControlPlaneAudit(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<AccessSecurityStore>().ControlPlaneAudit);
        services.AddSingleton(sp => ControlPlaneAuditSinkOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient(HttpsControlPlaneAuditSink.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<ControlPlaneAuditSinkHolder>(sp =>
        {
            var options = sp.GetRequiredService<ControlPlaneAuditSinkOptions>();
            return new ControlPlaneAuditSinkHolder(options.Kind == ControlPlaneAuditSinkOptions.Https
                ? new HttpsControlPlaneAuditSink(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpsControlPlaneAuditSink.HttpClientName), options)
                : null);
        });
        services.AddSingleton<IControlPlaneAuditAlertChannel>(sp =>
            new OperatorFeedAuditAlertChannel(sp.GetRequiredService<AgentMessageBusBridge>()));
        services.AddSingleton(sp => new ControlPlaneAuditMonitor(
            sp.GetRequiredService<ControlPlaneAuditLog>(), sp.GetRequiredService<ControlPlaneAuditSinkOptions>(),
            TimeProvider.System, sp.GetRequiredService<IControlPlaneAuditAlertChannel>(),
            sp.GetService<ILogger<ControlPlaneAuditMonitor>>()));
        services.AddSingleton(sp => new AuditSinkForwarder(
            sp.GetRequiredService<ControlPlaneAuditLog>(), sp.GetRequiredService<ControlPlaneAuditMonitor>(),
            sp.GetRequiredService<ControlPlaneAuditSinkHolder>().Sink, TimeProvider.System,
            sp.GetRequiredService<IConfiguration>(), sp.GetService<ILogger<AuditSinkForwarder>>()));
        services.AddSingleton(sp => new ControlPlaneAuditVerifier(
            sp.GetRequiredService<ControlPlaneAuditLog>(), sp.GetRequiredService<ControlPlaneAuditMonitor>(),
            sp.GetRequiredService<ControlPlaneAuditSinkHolder>().Sink, TimeProvider.System,
            sp.GetRequiredService<IConfiguration>(), sp.GetService<ILogger<ControlPlaneAuditVerifier>>()));
        services.AddHostedService(sp => sp.GetRequiredService<AuditSinkForwarder>());
        services.AddHostedService(sp => sp.GetRequiredService<ControlPlaneAuditVerifier>());
        return services;
    }
}

/// <summary>The configured sink, or none; lets the forwarder and verifier share one adapter.</summary>
public sealed record ControlPlaneAuditSinkHolder(IControlPlaneAuditSink? Sink);
