using System.Text.Json;
using AgentTaskboard.UpdateService;

var builder = WebApplication.CreateBuilder(args);

// --- options binding ---------------------------------------------------------
var options = new UpdateServiceOptions();
builder.Configuration.GetSection("UpdateService").Bind(options);

if (!builder.Environment.IsEnvironment("Testing"))
{
    options.ListenUrl       = Environment.GetEnvironmentVariable("ATP_UPDATE_LISTEN")      ?? options.ListenUrl;
    options.StableCheckoutDir = Environment.GetEnvironmentVariable("ATP_STABLE_CHECKOUT")  ?? options.StableCheckoutDir;
    options.DevspaceDir     = Environment.GetEnvironmentVariable("ATP_DEVSPACE_DIR")        ?? options.DevspaceDir;
    options.UpdateScript    = Environment.GetEnvironmentVariable("ATP_UPDATE_SCRIPT")       ?? options.UpdateScript;
    options.StopScript      = Environment.GetEnvironmentVariable("ATP_STOP_SCRIPT")         ?? options.StopScript;
    options.StartScript     = Environment.GetEnvironmentVariable("ATP_START_SCRIPT")        ?? options.StartScript;
    options.BackendUrl      = Environment.GetEnvironmentVariable("ATP_BACKEND_URL")         ?? options.BackendUrl;
    options.HistoryFile     = Environment.GetEnvironmentVariable("ATP_UPDATE_HISTORY")      ?? options.HistoryFile;
    options.RunsDirectory   = Environment.GetEnvironmentVariable("ATP_UPDATE_RUNS_DIR")     ?? options.RunsDirectory;
    options.TriggerToken    = Environment.GetEnvironmentVariable("ATP_UPDATE_TOKEN")        ?? options.TriggerToken;
    options.BashPath        = Environment.GetEnvironmentVariable("ATP_BASH_PATH")           ?? options.BashPath;
    options.VersionFile     = Environment.GetEnvironmentVariable("ATP_VERSION_FILE")        ?? options.VersionFile;
    options.Mode            = Environment.GetEnvironmentVariable("ATP_UPDATE_MODE")          ?? options.Mode;

    // ADR-0031: opt-in auto-rollback. Env flag (ATP_UPDATE_AUTO_ROLLBACK=1/true)
    // turns it on at runtime. The integration suite drives this through the
    // `UpdateService:AutoRollback` config key bound above, so Testing keeps
    // the config-bound value isolated from the developer machine environment.
    var autoRollbackEnv = Environment.GetEnvironmentVariable("ATP_UPDATE_AUTO_ROLLBACK");
    if (!string.IsNullOrEmpty(autoRollbackEnv))
    {
        options.AutoRollback = string.Equals(autoRollbackEnv, "1", StringComparison.Ordinal)
            || string.Equals(autoRollbackEnv, "true", StringComparison.OrdinalIgnoreCase);
    }
}

builder.Services.AddSingleton(options);
builder.Services.AddHttpClient();

builder.Services.AddSingleton<IGitProbe>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<GitProbe>>();
    return new GitProbe(options.StableCheckoutDir, logger);
});

builder.Services.AddSingleton<IBackendProbe>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var logger = sp.GetRequiredService<ILogger<BackendProbe>>();
    return new BackendProbe(http, options.BackendUrl, options.BackendClientId, logger);
});

builder.Services.AddSingleton<UpdateVerifier>();
builder.Services.AddSingleton<VerificationPreconditionService>();
builder.Services.AddSingleton<ReleasePreflightService>();

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<UpdateStatusStore>>();
    var git = sp.GetRequiredService<IGitProbe>();
    Func<string> readVersion = () =>
    {
        var path = options.VersionFile;
        if (!File.Exists(path)) return "unknown";
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("#")) return trimmed;
        }
        return "unknown";
    };
    return new UpdateStatusStore(options.HistoryFile, git.HeadShort(), readVersion, logger, options.Mode);
});

builder.Services.AddSingleton<UpdateOrchestrator>();
builder.Services.AddHostedService<PeriodicProbeService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.WebHost.UseUrls(options.ListenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

var app = builder.Build();
app.UseCors();

// --- endpoints ---------------------------------------------------------------

app.MapGet("/healthz", () => Results.Text("\"ok\"", "application/json"));
app.MapGet("/update/health", () => Results.Text("\"ok\"", "application/json"));

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
jsonOpts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

app.MapGet("/update/status", (UpdateStatusStore store) =>
    Results.Json(store.Get(), jsonOpts));

app.MapGet("/update/history", (UpdateStatusStore store, int? max) =>
    Results.Json(store.ReadHistory(max.GetValueOrDefault(20)), jsonOpts));

app.MapGet("/update/preflight", async (ReleasePreflightService preflight, CancellationToken ct) =>
    Results.Json(await preflight.EvaluateAsync(allowDowngrade: false, ct), jsonOpts));

app.MapPost("/update/trigger", async (HttpContext ctx, UpdateOrchestrator orch, UpdateServiceOptions opt, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(opt.TriggerToken))
    {
        var sent = ctx.Request.Headers["X-Update-Token"].FirstOrDefault();
        if (sent != opt.TriggerToken)
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    TriggerRequest? body = null;
    try { body = await ctx.Request.ReadFromJsonAsync<TriggerRequest>(ct); } catch { /* empty body OK */ }
    var force = body?.Force ?? false;
    var trigger = string.IsNullOrWhiteSpace(body?.Reason) ? "manual" : body.Reason.Trim();
    var (runId, phase, message) = orch.StartTrigger(trigger: trigger, force: force, lifetime.ApplicationStopping);
    return Results.Json(new TriggerResponse(runId, phase, message), statusCode: 202);
});

// ADR-0031 manual-rollback endpoint. Same auth/token gating as /update/trigger.
app.MapPost("/update/rollback", async (HttpContext ctx, UpdateOrchestrator orch, UpdateServiceOptions opt, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(opt.TriggerToken))
    {
        var sent = ctx.Request.Headers["X-Update-Token"].FirstOrDefault();
        if (sent != opt.TriggerToken)
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    RollbackRequest? body = null;
    try { body = await ctx.Request.ReadFromJsonAsync<RollbackRequest>(ct); } catch { /* fall through */ }
    if (body == null || string.IsNullOrWhiteSpace(body.RunId))
        return Results.Json(new { error = "missing runId" }, statusCode: 400);

    var (runId, phase, message) = orch.StartManualRollback(body.RunId, lifetime.ApplicationStopping);
    return Results.Json(new RollbackResponse(runId, phase, message), statusCode: 202);
});

app.Run();

// Exposed as a public partial so the integration suite can target this
// host via WebApplicationFactory<Program>. Minimal-API top-level
// statements emit an internal Program by default; the partial below
// promotes it to public without changing any runtime behaviour.
public partial class Program { }
