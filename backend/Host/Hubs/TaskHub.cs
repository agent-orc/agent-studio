using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Host;

public class TaskHub : Hub
{
    internal const string UnscopedSecurityGroup = "security:unscoped";

    private readonly IConfiguration _configuration;
    private readonly AccessSecurityStore _security;
    private readonly TaskScannerService _scanner;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly PublicDemoViewerSessionStore _viewerSessions;

    public TaskHub(
        IConfiguration configuration,
        AccessSecurityStore security,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        PublicDemoViewerSessionStore viewerSessions)
    {
        _configuration = configuration;
        _security = security;
        _scanner = scanner;
        _projects = projects;
        _viewerSessions = viewerSessions;
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
            && (principal is null || ProjectAccessAuthorization.HasUnrestrictedProjectAccess(principal.User)))
            await Groups.AddToGroupAsync(Context.ConnectionId, UnscopedSecurityGroup);
        foreach (var project in _projects.List())
        {
            if (principal is null || ProjectAccessAuthorization.Allows(principal.User, project.Id, _projects))
                await Groups.AddToGroupAsync(Context.ConnectionId, ProjectGroup(project.Id, _projects));
        }
        await base.OnConnectedAsync();
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
        if (principal is not null && !ProjectAccessAuthorization.Allows(principal.User, task.ProjectName, _projects))
            throw new HubException("Project access denied.");
        return Groups.AddToGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));
    }

    public Task UnsubscribeFromConversation(string jobId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationProjector.GroupName(jobId));

    internal static string ProjectGroup(string projectHandle, AgentStudio.Registry.ProjectRegistry projects)
    {
        var project = projects.FindByIdOrDisplayName(projectHandle)
                      ?? projects.FindByShortCode(projectHandle)
                      ?? projects.FindByStorageLocation(projectHandle);
        return "project:" + (project?.Id ?? projectHandle).ToLowerInvariant();
    }

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
