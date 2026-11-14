using AgentStudio.Pipeline;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using TaskServerStore = AgentStudio.TaskServer.TaskServerStore;
using TaskServerOptions = AgentStudio.TaskServer.TaskServerOptions;

namespace AgentStudio.Cli;

/// <summary>
/// The combined host uses the Task Server's durable one-shot store and validation.
/// Its local runner plane mirrors registrations and advertisements into this
/// queue authority so the same capability and instance fences apply.
/// </summary>
public sealed class CombinedOneShotJobs
{
    private readonly TaskServerStore _store;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private volatile bool _ready;

    public CombinedOneShotJobs(IHostEnvironment environment, IConfiguration configuration, TimeProvider clock)
    {
        var directory = BatchGateStorageRoot.ForInstance(environment.ContentRootPath,
            configuration["TaskRepository"], "one-shot-jobs");
        _store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = directory }), clock);
    }

    public CombinedOneShotJobs(TaskServerStore store) => _store = store;

    private async Task<TaskServerStore> ReadyAsync(CancellationToken ct)
    {
        if (_ready) return _store;
        await _initialization.WaitAsync(ct);
        try
        {
            if (!_ready)
            {
                await _store.InitializeAsync(ct);
                _ready = true;
            }
        }
        finally { _initialization.Release(); }
        return _store;
    }

    public async Task RegisterAsync(string runnerId, RegisterRunnerRequest request, CancellationToken ct)
        => await (await ReadyAsync(ct)).RegisterRunnerAsync(runnerId, request, "combined-api", ct);

    public async Task<RunnerCapabilitySnapshotDto> AdvertiseAsync(CapabilityAdvertisementRequest request, CancellationToken ct)
        => await (await ReadyAsync(ct)).AdvertiseCapabilitiesAsync(request, "combined-api", ct);

    public async Task<OneShotJobDto> CreateAsync(OneShotJobRequest request, CancellationToken ct)
        => await (await ReadyAsync(ct)).CreateOneShotJobAsync(request, ct);

    public async Task<OneShotJobDto?> GetAsync(string jobId, CancellationToken ct)
        => await (await ReadyAsync(ct)).GetOneShotJobAsync(jobId, ct);

    public async Task<OneShotJobWork?> ClaimAsync(OneShotJobClaimRequest request, CancellationToken ct)
        => await (await ReadyAsync(ct)).ClaimOneShotJobAsync(request, ct);

    public async Task<bool> CompleteAsync(OneShotJobCompletion request, CancellationToken ct)
        => await (await ReadyAsync(ct)).CompleteOneShotJobAsync(request, ct);
}

public static class CombinedOneShotJobEndpoints
{
    public static void MapCombinedOneShotJobEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/one-shot-jobs");
        api.MapPost("", async (OneShotJobRequest request, CombinedOneShotJobs jobs, CancellationToken ct) =>
        {
            try { return Results.Ok(await jobs.CreateAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new ApiError("invalid-request", ex.Message)); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
        api.MapGet("/{jobId}", async (string jobId, CombinedOneShotJobs jobs, CancellationToken ct) =>
            Results.Json(await jobs.GetAsync(jobId, ct)));
        api.MapPost("/claim", async (HttpContext context, OneShotJobClaimRequest request,
            CombinedOneShotJobs jobs, CancellationToken ct) =>
        {
            if (!RunnerMatches(context, request.RunnerId)) return RunnerIdentityMismatch();
            return Results.Json(await jobs.ClaimAsync(request, ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);
        api.MapPost("/complete", async (HttpContext context, OneShotJobCompletion request,
            CombinedOneShotJobs jobs, CancellationToken ct) =>
        {
            if (!RunnerMatches(context, request.RunnerId)) return RunnerIdentityMismatch();
            return Results.Json(await jobs.CompleteAsync(request, ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }

    private static bool RunnerMatches(HttpContext context, string runnerId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
           || string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal);

    private static IResult RunnerIdentityMismatch()
        => Results.Json(new ApiError("runner-identity-mismatch",
            "A Runner principal may act only for its bound Runner identity."),
            statusCode: StatusCodes.Status403Forbidden);
}
