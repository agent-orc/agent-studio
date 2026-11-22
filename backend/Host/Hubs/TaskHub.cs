using Microsoft.AspNetCore.SignalR;
using AgentStudio.Diagnostics;

namespace AgentStudio.Host;

public class TaskHub : Hub
{
    internal const string UnscopedSecurityGroup = "security:unscoped";

    private readonly IConfiguration _configuration;
    private readonly AccessSecurityStore _security;
    private readonly TaskScannerService _scanner;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly PublicDemoViewerSessionStore _viewerSessions;
    private readonly AgentStudio.Management.StructureSnapshotService? _structure;

    public TaskHub(
        IConfiguration configuration,
        AccessSecurityStore security,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        PublicDemoViewerSessionStore viewerSessions,
        AgentStudio.Management.StructureSnapshotService? structure = null)
    {
        _configuration = configuration;
        _security = security;
        _scanner = scanner;
        _projects = projects;
        _viewerSessions = viewerSessions;
        _structure = structure;
    }

    public override async Task OnConnectedAsync()
    {
        var isPublicDemo = SecurityProfiles.IsPublicDemo(_configuration);
        if (isPublicDemo && !HasLiveDemoViewerSession())
        {
            Context.Abort();
            throw new HubException("A public demo viewer session (issued by the edge) is required.");
        }

        var principal = LivePrincipal();
        // The public demo has no unscoped catch-all group: every visible
        // project is already enumerated and allowlist-checked below, and the
        // demo has no non-public project whose broadcasts an unscoped
        // connection could otherwise pick up. An empty membership list never
        // widens a scoped role into the unscoped group.
        if (!isPublicDemo
            && (principal is null || CanReadWorkspace(principal)))
            await Groups.AddToGroupAsync(Context.ConnectionId, UnscopedSecurityGroup);
        foreach (var project in _projects.List())
        {
            if (principal is null || CanReadProject(principal, project.Id))
                await Groups.AddToGroupAsync(Context.ConnectionId, ProjectGroup(project.Id, _projects));
        }
        if (SecurityProfiles.IsNetworked(_configuration))
            _ = MonitorLiveSessionAsync();
        await base.OnConnectedAsync();
    }

    private async Task MonitorLiveSessionAsync()
    {
        var connection = Context;
        var token = connection.GetHttpContext()?.Request.Cookies[AccessSecurityStore.SessionCookieName];
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(connection.ConnectionAborted))
            {
                if (_security.AuthenticateSession(token, touch: false) is not null) continue;
                connection.Abort();
                return;
            }
        }
        catch (OperationCanceledException exception) when (connection.ConnectionAborted.IsCancellationRequested)
        {
            SilentCatch.Note(exception, "TaskHub session monitor disconnected");
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "TaskHub session monitor failed");
            connection.Abort();
        }
    }

    private bool HasLiveDemoViewerSession()
    {
        var cookie = Context.GetHttpContext()?.Request.Cookies[PublicDemoViewerSessionStore.CookieName];
        return _viewerSessions.Touch(cookie);
    }

    // Client methods:
    // - jobsChanged                                          → board refresh
    // - cliOutput(jobId, line, stream, timestamp)            → live CLI output line
    // - cliStarted(jobId, processId, startedAt)              → CLI process started
    // - cliFinished(jobId, exitCode, duration, status)       → CLI process finished
    // - runnerStatusChanged(projectName, mode, activeJobId)  → runner mode/status change
    // - busMessageAdded(AgentMessage)                        → new bus event appended
    // - orchestratorContextChanged(payload)                 → central Chat History refresh
    // - workbenchCreated/Updated/DecisionRecorded/StatusChanged(WorkbenchHubEvent)
    // F22:
    // - conversationEventsAppended(jobId, ProjectedEvent[])  → live append from a source change
    // - conversationProjectionInvalidated(jobId)             → client should refetch the snapshot

    /// <summary>
    /// Join the per-job group that receives <c>conversationEventsAppended</c>
    /// and <c>conversationProjectionInvalidated</c> pushes. Caller is the
    /// detail-pane component that opened the protocol tab for the job.
    /// </summary>
    public Task SubscribeToConversation(string jobId)
    {
        var principal = LivePrincipal();
        var task = _scanner.FindJob(jobId);
        if (task is null) throw new HubException("Task not found.");
        if (principal is not null && !CanReadProject(principal, task.ProjectName))
            throw new HubException("Project access denied.");
        return Groups.AddToGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));
    }

    public Task UnsubscribeFromConversation(string jobId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));

    // AGT-3954:
    // - structureDelta(StructureDelta)                       → changed nodes and links of one structure scope

    /// <summary>
    /// Join the group that receives <c>structureDelta</c> for one scope of
    /// <c>GET /api/v1/operations/structure</c>. The route needs installation-wide
    /// operate permission, and so does the subscription. Returns the scope as
    /// deltas name it and the revision the server holds, so the caller can
    /// tell whether its copy is current.
    /// </summary>
    public async Task<StructureSubscription> SubscribeToStructure(string? scope, string? environment)
    {
        var (projectId, filter) = StructureScope(scope, environment);
        await Groups.AddToGroupAsync(Context.ConnectionId,
            AgentStudio.Management.StructureSnapshotService.GroupName(projectId, filter));
        // Deltas name the scope by project id; the caller may have asked by display name.
        return new(AgentStudio.Management.StructureSnapshotService.ScopeName(projectId), filter,
            _structure!.Touch(projectId, filter));
    }

    public Task UnsubscribeFromStructure(string? scope, string? environment)
    {
        var (projectId, filter) = StructureScope(scope, environment);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId,
            AgentStudio.Management.StructureSnapshotService.GroupName(projectId, filter));
    }

    private (string? ProjectId, string? Environment) StructureScope(string? scope, string? environment)
    {
        if (_structure is null || SecurityProfiles.IsPublicDemo(_configuration))
            throw new HubException("Structure updates are not available.");
        var principal = LivePrincipal();
        if (principal is not null && !_security.AuthorizeUser(principal.User.Id, IamPermissions.WorkspaceOperate,
                PermissionTarget.WorkspaceWide).Allowed)
            throw new HubException("Structure access denied.");
        string? projectId = null;
        if (scope is not null && scope != "workspace")
        {
            if (!scope.StartsWith("project:", StringComparison.Ordinal)) throw new HubException("Unknown structure scope.");
            projectId = _projects.FindByIdOrDisplayName(scope[8..])?.Id ?? throw new HubException("Project not found.");
        }
        return (projectId, string.IsNullOrWhiteSpace(environment) ? null : environment);
    }

    internal static string ProjectGroup(string projectHandle, AgentStudio.Registry.ProjectRegistry projects)
    {
        var project = projects.FindByIdOrDisplayName(projectHandle)
                      ?? projects.FindByShortCode(projectHandle)
                      ?? projects.FindByStorageLocation(projectHandle);
        return "project:" + (project?.Id ?? projectHandle).ToLowerInvariant();
    }

    private bool CanReadWorkspace(HumanPrincipal principal)
        => _security.AuthorizeUser(principal.User.Id, IamPermissions.ProjectRead,
            PermissionTarget.WorkspaceWide).Allowed;

    private bool CanReadProject(HumanPrincipal principal, string project)
        => _security.AuthorizeUser(principal.User.Id, IamPermissions.ProjectRead,
            PermissionTarget.ForProject(project),
            (bound, requested) => ProjectAccessAuthorization.SameProject(bound, requested, _projects)).Allowed;

    private HumanPrincipal? LivePrincipal()
    {
        if (!SecurityProfiles.IsNetworked(_configuration)) return null;
        var http = Context.GetHttpContext();
        var principal = _security.AuthenticateSession(http?.Request.Cookies[AccessSecurityStore.SessionCookieName], touch: false);
        if (principal is null)
        {
            Context.Abort();
            throw new HubException("Studio session expired.");
        }
        // The HTTP gate already refuses hub requests for a forced password
        // change; the hub re-checks on connect and on every subscription so the
        // rule holds even if the gate is bypassed.
        if (principal.User.MustChangePassword)
        {
            Context.Abort();
            throw new HubException("Change the temporary password before continuing.");
        }
        return principal;
    }
}

/// <summary>Answer of <see cref="TaskHub.SubscribeToStructure"/> (AGT-3954).</summary>
public sealed record StructureSubscription(string Scope, string? Environment, long Revision);
