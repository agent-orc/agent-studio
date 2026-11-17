namespace AgentStudio.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
    /// <summary>OTLP intake, the built-in store, its single writer, and the ingest-token seam.</summary>
    public static IServiceCollection AddTelemetryIntake(this IServiceCollection services, bool enabled = true)
    {
        services.AddSingleton(sp => TelemetryOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<TelemetryStore>();
        services.AddSingleton<TelemetryIntakeQueue>();
        services.AddSingleton<ITelemetryIngestCredentials, AccessStoreTelemetryIngestCredentials>();
        services.AddSingleton<ITelemetryEnvironmentDirectory, DeclaredTelemetryEnvironments>();
        services.AddSingleton<ITelemetryValueMasker, CredentialTelemetryValueMasker>();
        services.AddSingleton<TelemetryIntakeService>();
        services.AddSingleton<TelemetryWriter>();
        if (enabled) services.AddHostedService(sp => sp.GetRequiredService<TelemetryWriter>());
        return services;
    }
}
