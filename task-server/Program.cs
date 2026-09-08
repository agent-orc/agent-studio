using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using System.Text.Json;

TaskServerCommandLine command;
try
{
    command = TaskServerCommandLine.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

if (command.Kind == TaskServerCommandKind.Version)
{
    Console.WriteLine($"task-server {TaskServerBuildIdentity.Current.DisplayVersion}");
    return 0;
}

if (command.Kind == TaskServerCommandKind.Retention)
    return await RetentionCommand.RunAsync(command.Retention!, default);

var builder = WebApplication.CreateBuilder(command.HostArguments);
if (string.Equals(Environment.GetEnvironmentVariable("TASK_SERVER_PROFILE"), "local-compatibility", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.LocalCompatibility.json", optional: false, reloadOnChange: false);
builder.Services.AddSingleton(serviceProvider =>
    TaskServerBootstrapOptions.Load(
        serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<TaskServerStartupExecutionAdmission>();
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
builder.Services.AddSignalR();
builder.Services.AddSingleton<IResultFinalizationSummaryGenerator, ApplicationResultFinalizationSummaryGenerator>();
builder.Services.AddSingleton<TaskServerStore>();
builder.Services.AddSingleton<RuntimeCapacitySettingsService>();
builder.Services.AddSingleton<HostProjectPolicyService>();
builder.Services.AddSingleton<LegacyMigrationService>();
builder.Services.AddSingleton<RetentionManagementService>();
builder.Services.AddSingleton<FullBackupManagementService>();
builder.Services.AddSingleton<IResultRefDeleter, GitResultRefDeleter>();
builder.Services.AddHostedService<TaskServerInvariantReconciliationService>();
builder.Services.AddHostedService<ResultRefGcHostedService>();
builder.Services.AddHostedService<RetentionSchedulerHostedService>();

var configuredUrl = builder.Configuration["LISTEN_URL"]
                    ?? builder.Configuration[$"{TaskServerOptions.SectionName}:ListenUrl"];
if (!string.IsNullOrWhiteSpace(configuredUrl)
    && !builder.Environment.IsEnvironment("Testing")
    && string.IsNullOrWhiteSpace(builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)))
    builder.WebHost.UseUrls(configuredUrl);

var app = builder.Build();
var store = app.Services.GetRequiredService<TaskServerStore>();
if (command.Kind == TaskServerCommandKind.Backup)
{
    try
    {
        await store.InitializeForBackupAsync();
        var backup = await store.CreateBackupAsync(
            new BackupRequest(command.BackupName ?? "timer"),
            "task-server-backup-command",
            default);
        Console.WriteLine(JsonSerializer.Serialize(
            backup,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Task Server backup failed: {exception.Message}");
        return 1;
    }
}

if (command.Kind == TaskServerCommandKind.FullBackup)
{
    try
    {
        await store.InitializeForBackupAsync();
        var fullBackupCommand = command.FullBackup!;
        object result = fullBackupCommand.Operation switch
        {
            "full" => await store.CreateFullBackupAsync("task-server-backup-command", default),
            "verify-full" => await store.VerifyFullBackupAsync(fullBackupCommand.BackupId!, default),
            "restore-full" => await store.RestoreFullBackupAsync(fullBackupCommand.BackupId!, "task-server-backup-command", default),
            _ => throw new InvalidOperationException($"Unknown full backup operation '{fullBackupCommand.Operation}'."),
        };
        Console.WriteLine(JsonSerializer.Serialize(
            result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = fullBackupCommand.Json }));
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Task Server full backup failed: {exception.Message}");
        return 1;
    }
}

await store.InitializeAsync(app.Lifetime.ApplicationStopping);
var bootstrap = app.Services.GetRequiredService<TaskServerBootstrapOptions>();
if (bootstrap.RequiresAuthentication)
{
    await BootstrapPrincipalAsync(
        store,
        "bootstrap-studio",
        TaskServerPrincipalKinds.Studio,
        bootstrap.StudioAuthenticationToken,
        runnerId: null,
        app.Lifetime.ApplicationStopping);
    await BootstrapPrincipalAsync(
        store,
        "bootstrap-engine",
        TaskServerPrincipalKinds.Engine,
        bootstrap.EngineAuthenticationToken,
        runnerId: null,
        app.Lifetime.ApplicationStopping);
    if (bootstrap.BootstrapRunnerAuthenticationToken is not null)
        await BootstrapPrincipalAsync(
            store,
            $"runner:{bootstrap.BootstrapRunnerId}",
            TaskServerPrincipalKinds.Runner,
            bootstrap.BootstrapRunnerAuthenticationToken,
            bootstrap.BootstrapRunnerId,
            app.Lifetime.ApplicationStopping);
    if (bootstrap.LegacyRunnerAuthenticationToken is not null)
        await BootstrapPrincipalAsync(
            store,
            "deprecated-shared-runner",
            TaskServerPrincipalKinds.Runner,
            bootstrap.LegacyRunnerAuthenticationToken,
            runnerId: null,
            app.Lifetime.ApplicationStopping);
}
app.UseRouting();
app.UsePublicDemoExecutionLock();
app.UseMiddleware<TaskServerAuthenticationMiddleware>();
app.UseMiddleware<TaskServerProtocolMiddleware>();
app.MapTaskServerEndpoints();
app.MapHub<TaskServerEventsHub>("/hubs/events")
    .RequireTaskServerScope(TaskServerScopes.EventsSubscribe);
TaskServerPublicDemoExecutionRouteInventory.ValidateStartup(
    app,
    app.Services.GetRequiredService<TaskServerStartupExecutionAdmission>());
static async Task BootstrapPrincipalAsync(
    TaskServerStore store,
    string principalId,
    string kind,
    string? configuredCredential,
    string? runnerId,
    CancellationToken cancellationToken)
{
    var issued = await store.EnsureBootstrapPrincipalAsync(
        principalId,
        kind,
        configuredCredential,
        runnerId,
        cancellationToken);
    if (issued is not null)
        Console.WriteLine(
            $"INITIAL {kind.ToUpperInvariant()} CREDENTIAL (shown once): {issued.Credential}");
}

await app.RunAsync();
return 0;

public partial class Program;
