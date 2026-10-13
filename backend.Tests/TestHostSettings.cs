using Microsoft.AspNetCore.Hosting;

namespace AgentStudio.Tests;

/// <summary>
/// Hands settings to one <c>WebApplicationFactory&lt;Program&gt;</c> host so
/// that every read sees them (AGT-3106).
///
/// <c>ConfigureAppConfiguration</c> alone is applied in <c>Build()</c>, after
/// <c>Program.cs</c> has already read the connector profile, the security
/// profile, the request-body cap and the rate limit. Tests used to reach those
/// early reads through process environment variables, which every host booting
/// at the same time also read, so all of them ran serially. WebApplicationFactory
/// passes <c>UseSetting</c> values to <c>WebApplication.CreateBuilder(args)</c>
/// as command-line arguments instead: they belong to this one host and are
/// visible from the first line of <c>Program.cs</c>. The in-memory source keeps
/// them on top of any source <c>Program.cs</c> adds after the builder exists.
/// </summary>
internal static class TestHostSettings
{
    public static IWebHostBuilder UseTestSettings(
        this IWebHostBuilder builder,
        IReadOnlyDictionary<string, string?> settings)
    {
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        return builder;
    }
}
