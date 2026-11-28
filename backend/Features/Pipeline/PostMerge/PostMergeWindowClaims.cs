using System.Net.Http.Json;
using AgentStudio.Shared;
using AgentStudio.Host;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

internal static class PostMergeWindowClaims
{
    public static string? Instance(HttpContext context) => context.Request.Headers[PostMergeClaimProtocol.InstanceHeader].FirstOrDefault();

    private static string? RunnerId(HttpContext context)
        => RunnerProjectBinding.Runner(context)?.RunnerId
            ?? context.Request.Headers["X-Principal-Consumer-Id"].FirstOrDefault();

    public static async Task<RunnerCapabilitySnapshotDto?> HostAsync(HttpContext context)
    {
        if (RunnerId(context) is not { Length: > 0 } runnerId) return null;
        var services = context.RequestServices;
        var configuration = services.GetRequiredService<IConfiguration>();
        var hosts = TaskServerPlaneProxy.IsConfigured(configuration)
            ? await services.GetRequiredService<IHttpClientFactory>().CreateClient(TaskServerPlaneProxy.ClientName)
                .GetFromJsonAsync<RunnerCapabilitySnapshotDto[]>("/api/v1/management/remote-hosts", context.RequestAborted) ?? []
            : services.GetRequiredService<V1ReviewExecutorRegistry>().ListCapabilitySnapshots();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        return hosts.FirstOrDefault(host => host.RunnerId == runnerId && host.Status == "active"
            && host.LastSeenAt > now.AddMinutes(-3) && (Instance(context) is not { } instance || host.InstanceId == instance));
    }

    public static bool Enabled(RunnerCapabilitySnapshotDto host)
        => host.Capabilities.Any(capability => capability.Key == CapabilityProtocol.CodingExecutor && capability.Identity == "coding")
           && host.Capabilities.Any(capability => capability.Key == CapabilityProtocol.CodingPostMerge
               && capability.IsFresh && capability.AdvertisedStatus == "ready");

    public static bool Allows(HttpContext context, ProjectRegistry registry, ProjectRecord project,
        RunnerCapabilitySnapshotDto? host = null)
        => RunnerProjectBinding.Allows(context, project.Id, registry)
           && (host?.ProjectPolicy is not { } policy || policy.AllowAllProjects || policy.AllowedProjectIds.Contains(project.Id));

    public static bool IsLocalManual(HttpContext context)
        => RunnerId(context) is null && Instance(context) is null
           && !SecurityProfiles.IsNetworked(context.RequestServices.GetRequiredService<IConfiguration>())
           && context.Request.Headers["X-Client-Id"].FirstOrDefault() == DefaultClientIdentity.Id;

    public static PostMergeProjectTarget? Target(ProjectRecord project, ProjectSettingsService settings)
    {
        var config = settings.Get(project.DisplayName);
        if (project.Archived || IntegrationTestModes.Resolve(config) != IntegrationTestModes.PostMerge
            || RemoteProjectRepositoryResolver.Resolve(project, config.IntegrationBranch) is not { } repository)
            return null;
        return new(project.Id, project.DisplayName, repository.RepositoryUrl, repository.DefaultBranch);
    }

    public static bool Owns(HttpContext context, PostMergeWindowGrant? grant, DateTimeOffset now)
        => grant is not null && grant.Lease.ExpiresAtUtc > now && grant.RunnerId == RunnerId(context)
           && grant.InstanceId == Instance(context)
           && grant.Lease.Id == context.Request.Headers[PostMergeClaimProtocol.GrantHeader].FirstOrDefault();

    public static async ValueTask<object?> SerializeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var services = context.RequestServices;
        var registry = services.GetRequiredService<ProjectRegistry>();
        var name = context.Request.RouteValues["project"] as string;
        var project = name is null ? null : registry.FindByIdOrDisplayName(name) ?? registry.FindByShortCode(name);
        if (project is null) return Results.NotFound();
        if (!Allows(context, registry, project)) return Results.NotFound();

        var store = services.GetRequiredService<PostMergeRunStore>();
        var guard = store.ClaimLock(project.Id);
        await guard.WaitAsync(context.RequestAborted);
        try { return await next(invocation); }
        finally { guard.Release(); }
    }

    public static ValueTask<object?> AuthorizeWriteAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => AuthorizeWriteAsync(invocation, next, requiresExecution: false);

    public static ValueTask<object?> AuthorizeExecutionAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => AuthorizeWriteAsync(invocation, next, requiresExecution: true);

    private static async ValueTask<object?> AuthorizeWriteAsync(EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next, bool requiresExecution)
    {
        var context = invocation.HttpContext;
        var services = context.RequestServices;
        var registry = services.GetRequiredService<ProjectRegistry>();
        var name = (string)context.Request.RouteValues["project"]!;
        var project = (registry.FindByIdOrDisplayName(name) ?? registry.FindByShortCode(name))!;
        var store = services.GetRequiredService<PostMergeRunStore>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var grant = store.ReadGrant(project.Id);
        if (Owns(context, grant, now))
        {
            if (requiresExecution && grant!.Lease.PlacementOnly) return Results.Conflict();
            context.Items[typeof(PostMergeWindowGrant)] = grant;
            return await next(invocation);
        }

        // Existing gate and trusted local manual commands have their own admission.
        // A coding daemon must use a window grant, including during a rolling upgrade.
        if (Instance(context) is not null) return Results.Conflict();
        if (IsLocalManual(context) && (grant is null || grant.Lease.ExpiresAtUtc <= now))
            return await next(invocation);
        if (store.Read(project.Id).GateSubjectId is { } subject
            && !invocation.Arguments.OfType<PostMergeRunRecord>().Any(run => run.Id != subject)
            && await HostAsync(context) is { } host && Allows(context, registry, project, host)
            && host.Capabilities.Any(cap => cap.Key == GateCapabilities.Executor && cap.IsFresh)
            && await services.GetRequiredService<IBatchGateSubjectAuthority>().GetAsync(subject, context.RequestAborted) is { } gate
            && gate.Attempts.Any(attempt => attempt.ExecutorId == host.RunnerId && attempt.HostId == host.HostId
                && !GateStates.IsTerminal(attempt.State)))
            return await next(invocation);
        return Results.Conflict();
    }

    public static async Task<IResult> ClaimAsync(ProjectRecord project, PostMergeProjectTarget target,
        PostMergeClaimRequest request, RunnerCapabilitySnapshotDto host, PostMergeRunStore store, TimeProvider clock,
        IBatchGateSubjectAuthority gates, CancellationToken ct)
    {
        var occupied = Math.Max(request.OccupiedSlots, host.Telemetry?.ActiveSlots ?? 0);
        var slots = host.EffectiveMaxParallelism ?? host.RoleMaxParallelism ?? 1;
        if (request.OccupiedSlots < 0 || !Enabled(host) || host.HostAdmission.AdmissionState != "open"
            || request.RepositoryUrl != target.RepositoryUrl || request.Branch != target.Branch
            || request.ReadTip is not { Length: 40 } || !request.ReadTip.All(Uri.IsHexDigit)
            || request.LoadPerCore is { } load && (!double.IsFinite(load) || load < 0))
            return Results.NoContent();

        var codingHasCapacity = host.Telemetry?.ClaimLoadGate?.State != "closed"
            && MonolithTransitionAdapter.PostMergeHostHasCapacity(occupied, slots, request.LoadPerCore, PostMergeClaimProtocol.MaxLoadPerCore);
        var gateAvailable = !codingHasCapacity && (await gates.PostMergeHostsAsync(ct)).Gate;
        var now = clock.GetUtcNow();
        var state = store.Read(project.Id);
        var active = state.ActiveRun;
        var custody = active is null ? null : store.ReadCustody(project.Id);
        var previous = store.ReadGrant(project.Id);
        var live = previous is not null && previous.Lease.ExpiresAtUtc > now;
        var sameInstance = live && previous!.RunnerId == host.RunnerId && previous.InstanceId == host.InstanceId;
        var mayPlaceOnGate = gateAvailable && (!sameInstance || previous!.Lease.PlacementOnly);
        var recovering = custody is not null && custody.Owner == request.RecoveryOwner && active?.ExecutionHost == host.HostId;
        var replaceable = active is null || MonolithTransitionAdapter.PostMergeCustodyMayReplace(
            active.StartedAtUtc, active.Kind, custody?.ExpiresAtUtc, now);
        if (!MonolithTransitionAdapter.PostMergeMayClaimWindow(live, sameInstance, recovering, replaceable,
            codingHasCapacity, mayPlaceOnGate))
            return Results.NoContent();

        var lease = new PostMergeGrant(sameInstance ? previous!.Lease.Id : Guid.NewGuid().ToString("N"),
            now + PostMergeClaimProtocol.GrantDuration)
        {
            PlacementOnly = sameInstance ? previous!.Lease.PlacementOnly : !codingHasCapacity,
        };
        store.WriteGrant(project.Id, new(host.RunnerId, host.InstanceId, host.HostId, lease));
        return Results.Ok(lease);
    }
}
