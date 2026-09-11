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
    })
    .Configure<IConfiguration>((options, configuration) =>
    {
        if (!string.IsNullOrWhiteSpace(configuration["ARCHIVE_PATH"]))
            options.RetentionArchivePath = configuration["ARCHIVE_PATH"];
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
builder.Services.AddSingleton<IRetentionRuntimeLoadGate, SystemRetentionRuntimeLoadGate>();
builder.Services.AddSingleton<ITaskServerEventPublisher, SignalRTaskServerEventPublisher>();
builder.Services.AddSingleton<IResultRefDeleter, GitResultRefDeleter>();
builder.Services.AddSingleton<IStudioEventPublisher, SignalRStudioEventPublisher>();
builder.Services.AddSingleton<StudioChatAttachmentStore>();
builder.Services.AddSingleton<StudioLifecycleCoordinator>();
builder.Services.AddStudioP2Services();
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
var migration = app.Services.GetRequiredService<LegacyMigrationService>();
var commandJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
if (command.Kind == TaskServerCommandKind.Inventory)
{
    try
    {
        var source = Path.GetFullPath(command.Source!);
        var inventory = await migration.InventoryAsync(
            new LegacyMigrationRequest(
                source,
                command.WorkspaceName ?? Path.GetFileName(source),
                FreezeConfirmed: false),
            default);
        PrintInventoryTable(inventory, Console.Error);
        Console.WriteLine(JsonSerializer.Serialize(inventory, commandJson));
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Task Server inventory failed: {exception.Message}");
        return 1;
    }
}

if (command.Kind == TaskServerCommandKind.Import)
{
    try
    {
        var inventoryJson = await File.ReadAllTextAsync(Path.GetFullPath(command.InventoryPath!));
        var inventory = JsonSerializer.Deserialize<LegacyMigrationInventory>(inventoryJson, commandJson)
                        ?? throw new InvalidDataException("The inventory JSON root is null.");
        LegacyMigrationService.ValidateInventoryHash(inventory);
        await store.InitializeAsync();
        if (command.Mode == TaskServerMode.Maintenance && store.Mode != TaskServerMode.Maintenance)
            await store.ChangeModeAsync(
                new ChangeModeRequest(TaskServerMode.Maintenance, "offline legacy import command"),
                "task-server-import-command",
                default);
        var source = Path.GetFullPath(command.Source!);
        var result = await migration.ImportAsync(
            new LegacyMigrationRequest(
                source,
                command.WorkspaceName ?? Path.GetFileName(inventory.LegacyRoot),
                FreezeConfirmed: true,
                ExpectedMigrationId: inventory.MigrationId,
                RequireAttemptAuthority: inventory.AuthorityEpoch > 0),
            inventory,
            "task-server-import-command",
            default);
        Console.WriteLine(JsonSerializer.Serialize(result, commandJson));
        return 0;
    }
    catch (Exception exception)
    {
        var code = exception is TaskServerConflictException conflict ? $" [{conflict.Code}]" : string.Empty;
        Console.Error.WriteLine($"Task Server import failed{code}: {exception.Message}");
        return 1;
    }
}

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
app.MapStudioEndpoints();
// Studio route-ownership P1 "task detail and hosts" bundle
// (docs/studio-route-ownership/index.html).
app.MapStudioHostsEndpoints();
app.MapStudioProjectMetaEndpoints();
app.MapStudioRunnerOrchestratorEndpoints();
app.MapStudioTaskMetadataEndpoints();
app.MapStudioTaskLifecycleExtrasEndpoints();
app.MapStudioTaskArtifactsEndpoints();
app.MapStudioTaskHistoryEndpoints();
app.MapStudioTaskReviewEndpoints();
app.MapStudioWorkspaceEndpoints();
// Studio route-ownership P2 "operations and insight" bundle
// (docs/studio-route-ownership/index.html).
app.MapStudioOperationsEndpoints();
app.MapStudioP2Endpoints();
app.MapHub<TaskServerEventsHub>("/hubs/events")
    .RequireTaskServerScope(TaskServerScopes.EventsSubscribe);
app.MapHub<TaskServerStudioHub>("/hubs/v1/studio")
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

static void PrintInventoryTable(LegacyMigrationInventory inventory, TextWriter writer)
{
    writer.WriteLine("Project | State | Tasks");
    writer.WriteLine("--------|-------|------");
    foreach (var project in inventory.ProjectCounts ?? [])
    foreach (var state in project.States.OrderBy(item => item.Key, StringComparer.Ordinal))
        writer.WriteLine($"{project.Project} | {state.Key} | {state.Value}");
    writer.WriteLine($"TOTAL | all | {inventory.Tasks}");
    writer.WriteLine(
        $"Epics {inventory.Epics}; dossiers {inventory.Dossiers}; events {inventory.Events}; artifacts {inventory.Artifacts}; " +
        $"sessions {inventory.OrchestratorSessions}; chats {inventory.ContextChats}/{inventory.ContextChatTurns} turns");
    writer.WriteLine(
        $"Authority {inventory.AttemptAuthorityRecords} attempts/{inventory.Leases} leases; " +
        $"integration {inventory.PendingIntegrationRecords} pending/{inventory.IntegrationRecords} history; " +
        $"Git {inventory.GitCommits} commits/{inventory.DeliveryRefs} delivery refs/{inventory.ResultRefs} result refs");
    var orphans = inventory.OrphanedReferences ?? new LegacyMigrationOrphanCounts();
    writer.WriteLine(
        $"Orphan warnings {orphans.CodingAttempts} coding/{orphans.ReviewAttempts} review/" +
        $"{orphans.Leases} leases/{orphans.FenceCounters} fences/{orphans.IntegrationRecords} integration");
    writer.WriteLine($"Bus references {inventory.BusLogFiles} files/{inventory.BusLogBytes} bytes");
    writer.WriteLine($"Inventory SHA-256: {inventory.InventorySha256}");
}

public partial class Program;
