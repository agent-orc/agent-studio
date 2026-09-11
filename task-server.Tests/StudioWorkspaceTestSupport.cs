using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TaskServer.Tests;

/// <summary>
/// Program-independent HTTP test harness shared by the P1 Studio
/// route-ownership groups (see docs/studio-route-ownership). Each group's own
/// <c>MapStudio*Endpoints</c> extension is deliberately not wired into
/// <c>task-server/Program.cs</c> yet - the integrator adds that call once
/// every group has landed, per the "only create new files" rule every group
/// works under - so <see cref="StudioEndpointsTests"/>'s Program-based
/// <c>WebApplicationFactory&lt;Program&gt;</c> pattern cannot reach these new
/// routes today. This factory instead builds a standalone <see cref="WebApplication"/>
/// that replicates the slice of <c>Program.cs</c>'s wiring every Studio v1
/// route needs (<see cref="TaskServerStore"/>, the protocol-version and
/// authentication middleware, scope metadata) against an in-memory
/// <see cref="TestServer"/>, and maps only the endpoints the caller supplies.
/// That lets each group's tests exercise real HTTP requests - status codes,
/// JSON bodies, conflict codes - against its own routes independently of
/// <c>Program.cs</c> and of every other group's files.
/// </summary>
internal static class StudioWorkspaceTestApiFactory
{
    /// <param name="dataDirectory">Fresh, per-test SQLite data directory (typically a <see cref="TempDirectory"/>).</param>
    /// <param name="mapEndpoints">Called once against the built app, e.g. <c>app =&gt; app.MapStudioWorkspaceEndpoints()</c>.</param>
    /// <param name="applyGroupMigration">
    /// Optional hook run once, right after <see cref="TaskServerStore.InitializeAsync"/>, with a fresh connection to
    /// the same database file. Each P1 group's own <c>internal Task ApplyStudio*MigrationAsync(SqliteConnection, CancellationToken)</c>
    /// is not yet called from <c>TaskServerStore</c>'s main migration method - the integrator wires that in once every
    /// group has landed - so a group's tests apply their own migration here instead, e.g.
    /// <c>(store, connection, ct) =&gt; store.ApplyStudioWorkspaceMigrationAsync(connection, ct)</c>.
    /// </param>
    public static async Task<WebApplication> CreateAsync(
        string dataDirectory,
        Action<WebApplication> mapEndpoints,
        Func<TaskServerStore, SqliteConnection, CancellationToken, Task>? applyGroupMigration = null,
        CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.EnvironmentName = "Testing";
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskServer:DataDirectory"] = dataDirectory,
            ["TaskServer:ListenUrl"] = string.Empty,
            ["TaskServer:RetentionSchedulerEnabled"] = "false",
        });

        builder.Services.AddSingleton(serviceProvider =>
            TaskServerBootstrapOptions.Load(serviceProvider.GetRequiredService<IConfiguration>()));
        builder.Services
            .AddOptions<TaskServerOptions>()
            .Bind(builder.Configuration.GetSection(TaskServerOptions.SectionName))
            .Configure<TaskServerBootstrapOptions>((options, bootstrap) =>
            {
                options.DataDirectory = bootstrap.StorePath;
                options.BackupDirectory = bootstrap.BackupPath;
                options.ListenUrl = bootstrap.ListenUrl;
            });
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IResultFinalizationSummaryGenerator, ApplicationResultFinalizationSummaryGenerator>();
        builder.Services.AddSingleton<TaskServerStore>();

        var app = builder.Build();
        var store = app.Services.GetRequiredService<TaskServerStore>();
        await store.InitializeAsync(ct);

        if (applyGroupMigration is not null)
        {
            await using var migrationConnection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
            await migrationConnection.OpenAsync(ct);
            await applyGroupMigration(store, migrationConnection, ct);
        }

        app.UseRouting();
        app.UseMiddleware<TaskServerProtocolMiddleware>();
        app.UseMiddleware<TaskServerAuthenticationMiddleware>();
        mapEndpoints(app);

        await app.StartAsync(ct);
        return app;
    }
}

/// <summary>
/// Builds an <see cref="HttpClient"/> against a <see cref="StudioWorkspaceTestApiFactory"/>-created
/// app with the headers every <c>/api/v1</c> route requires (protocol version,
/// caller identity) already attached, mirroring <c>StudioEndpointsTests</c>'s
/// own private <c>Client(...)</c> helper.
/// </summary>
internal static class StudioWorkspaceTestClient
{
    public static HttpClient Create(WebApplication app, string clientId = "studio-workspace-test")
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", clientId);
        return client;
    }
}
