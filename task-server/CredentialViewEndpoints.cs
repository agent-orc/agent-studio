using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// I8 role enforcement (docs/credentials-and-logins/index.html#surface). A human identity is a
/// Task Server Studio session, or the role a trusted Studio edge principal acts for after
/// authenticating the human itself. A session always outranks an acting-role claim. Requests
/// without a human identity keep their machine-principal scope checks unchanged.
/// </summary>
internal static class StudioRoleGate
{
    public static async Task<string?> ResolveAsync(HttpContext context, TaskServerStore store,
        TaskServerBootstrapOptions bootstrap, CancellationToken ct)
    {
        var token = StudioEndpoints.StudioSessionToken(context);
        if (token is not null)
        {
            var status = await store.GetStudioAuthStatusAsync(token, ct);
            if (!status.Authenticated || status.User is null || status.User.Disabled)
                throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            return CredentialActionPolicy.NormalizeRole(status.User.Role);
        }
        var claimed = context.Request.Headers[CredentialViewProtocol.ActingRoleHeader].FirstOrDefault();
        if (claimed is not (StudioUserRoles.Owner or StudioUserRoles.Operator or StudioUserRoles.Viewer)) return null;
        var principal = context.TaskServerPrincipal();
        var trustedEdge = principal?.Kind == TaskServerPrincipalKinds.Studio
            || principal is null && !bootstrap.RequiresAuthentication;
        return trustedEdge ? claimed : null;
    }

    /// <summary>Denies a human identity below <paramref name="minimum"/>; machine callers pass.</summary>
    public static async Task RequireAsync(HttpContext context, TaskServerStore store,
        TaskServerBootstrapOptions bootstrap, string minimum, CancellationToken ct)
    {
        if (await ResolveAsync(context, store, bootstrap, ct) is not { } role || Rank(role) >= Rank(minimum)) return;
        throw minimum == StudioUserRoles.Owner
            ? new StudioAuthorizationException("owner-role-required",
                "Only an installation administrator may change credential scopes or rotate principals.")
            : new StudioAuthorizationException("operator-role-required",
                "Credential locators and evidence require an operator or administrator role.");
    }

    public static RouteHandlerBuilder RequireStudioRole(this RouteHandlerBuilder builder, string minimum)
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            try
            {
                await RequireAsync(http, http.RequestServices.GetRequiredService<TaskServerStore>(),
                    http.RequestServices.GetRequiredService<TaskServerBootstrapOptions>(), minimum, http.RequestAborted);
            }
            catch (Exception exception) when (exception is StudioAuthenticationException or StudioAuthorizationException)
            {
                return TaskServerEndpoints.MapError(exception);
            }
            return await next(context);
        });

    private static int Rank(string role) => role switch
    {
        StudioUserRoles.Owner => 2,
        StudioUserRoles.Operator => 1,
        _ => 0,
    };
}

internal static class CredentialViewEndpoints
{
    public static void MapCredentialViewEndpoints(this RouteGroupBuilder management)
        => management.MapGet("/credential-view", async (
            HttpContext context, TaskServerStore store, TaskServerBootstrapOptions bootstrap, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () => await store.GetCredentialViewAsync(
                await StudioRoleGate.ResolveAsync(context, store, bootstrap, ct) ?? StudioUserRoles.Viewer, ct)));
}

/// <summary>Daily evaluation, run hourly so a threshold is crossed within the hour.</summary>
public sealed class CredentialReminderHostedService(
    TaskServerStore store,
    TimeProvider clock,
    ILogger<CredentialReminderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                if (store.AuthorityReady) await store.ReconcileCredentialRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (TaskServerConflictException exception) when (exception.Code == "server-not-writable")
            {
                // Read-only and maintenance modes evaluate again on the next tick.
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "credential-reminders sweep-failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
