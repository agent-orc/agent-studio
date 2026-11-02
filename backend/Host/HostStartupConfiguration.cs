using AgentStudio.Diagnostics;
using AgentStudio.Security;

namespace AgentStudio.Host;

internal static class HostStartupConfiguration
{
    internal static void LoadLocalSettings(WebApplicationBuilder builder, string[] args, bool underTestHost)
    {
        // Test hosts must not inherit machine-specific settings, including
        // WebApplicationFactory fixtures that use the default environment.
        if (builder.Environment.IsEnvironment("Test") || underTestHost)
            return;

        // The local file belongs above the base and environment JSON files,
        // while environment variables and command-line arguments retain their
        // normal higher precedence.
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
    }

    internal static void ConfigureRequestBodyLimit(WebApplicationBuilder builder)
    {
        var publicDemoBodyCap = SecurityProfiles.IsPublicDemo(builder.Configuration);
        var maxRequestBodyBytes = builder.Configuration.GetValue<long>(
            publicDemoBodyCap ? "Security:PublicDemoMaxRequestBodyBytes" : "Security:MaxRequestBodyBytes",
            publicDemoBodyCap ? 16 * 1024 : 25 * 1024 * 1024);
        builder.Services.AddSingleton(new ArtifactRequestLimits(maxRequestBodyBytes));
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Every public-demo mutation is denied at the edge. This tighter
            // default bounds the cost of a body reaching Kestrel first.
            options.Limits.MaxRequestBodySize = maxRequestBodyBytes;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
    }
}
