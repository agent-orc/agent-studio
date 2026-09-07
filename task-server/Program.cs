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
builder.Services.AddSingleton<IResultFinalizationSummaryGenerator, ApplicationResultFinalizationSummaryGenerator>();
builder.Services.AddSingleton<TaskServerStore>();
builder.Services.AddSingleton<RuntimeCapacitySettingsService>();
builder.Services.AddSingleton<HostProjectPolicyService>();
builder.Services.AddSingleton<LegacyMigrationService>();
builder.Services.AddSingleton<IResultRefDeleter, GitResultRefDeleter>();
builder.Services.AddHostedService<TaskServerInvariantReconciliationService>();
builder.Services.AddHostedService<ResultRefGcHostedService>();

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

await store.InitializeAsync(app.Lifetime.ApplicationStopping);
app.UseRouting();
app.UsePublicDemoExecutionLock();
app.UseMiddleware<TaskServerAuthenticationMiddleware>();
app.UseMiddleware<TaskServerProtocolMiddleware>();
app.MapTaskServerEndpoints();
TaskServerPublicDemoExecutionRouteInventory.ValidateStartup(
    app,
    app.Services.GetRequiredService<TaskServerStartupExecutionAdmission>());
await app.RunAsync();
return 0;

public partial class Program;
