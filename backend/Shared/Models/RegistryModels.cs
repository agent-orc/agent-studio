namespace AgentStudio.Shared;

/// <summary>
/// A single watchable URL attached to a <see cref="ProjectRecord"/> (a dev
/// server, a preview site, a static page). Ordered by <see cref="SortOrder"/>.
/// The optional <see cref="StartRule"/> is absent for URLs that have nothing to
/// spawn (e.g. a static HTML file that is just opened).
/// </summary>
public record ProjectUrlRecord
{
    /// <summary>Stable per-project identifier (e.g. <c>url-1</c>). Immutable.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-readable label, e.g. "Presentation website".</summary>
    public string Label { get; init; } = "";
    /// <summary>Absolute URL to open, e.g. <c>http://localhost:4202</c>.</summary>
    public string Url { get; init; } = "";
    /// <summary>Render order within the project. Smaller comes first.</summary>
    public int SortOrder { get; init; }
    /// <summary>Optional command that builds/starts this URL's server.</summary>
    public ProjectUrlStartRule? StartRule { get; init; }
}

/// <summary>
/// How to build and start the server that serves a <see cref="ProjectUrlRecord"/>.
/// </summary>
public record ProjectUrlStartRule
{
    /// <summary>Command to run, e.g. <c>npm run website</c>.</summary>
    public string Command { get; init; } = "";
    /// <summary>Working directory; falls back to a valid project repository/root path.</summary>
    public string? Cwd { get; init; }
    /// <summary>Port the server listens on, when known.</summary>
    public int? Port { get; init; }
    /// <summary>Optional HTTP readiness target; defaults to the URL itself.</summary>
    public string? HealthUrl { get; init; }
    /// <summary>
    /// Console-silence window for startup validation. Startup is abandoned only
    /// after this many seconds pass with no new process output <em>and</em> the
    /// URL is still unreachable; while the command keeps producing output the
    /// wait continues (bounded by <see cref="StartupTimeoutSeconds"/>).
    /// Existing persisted values keep their numeric value and are interpreted
    /// as this silence window. Defaults to 30 seconds.
    /// </summary>
    public int ReadinessTimeoutSeconds { get; init; } = 30;
    /// <summary>
    /// Absolute startup limit even while console output remains active.
    /// Defaults to 10 minutes.
    /// </summary>
    public int StartupTimeoutSeconds { get; init; } = 600;
    /// <summary>Where the rule came from: <c>manual</c> | <c>package-json</c> | <c>readme</c>.</summary>
    public string Source { get; init; } = "manual";
}

/// <summary>
/// F45a — top-level grouping for projects on the kanban board. Workspaces
/// are pure metadata: no folder, no on-disk identity beyond an entry in
/// <c>&lt;TaskRepository&gt;/.metadata/workspaces.json</c>. A project belongs
/// to exactly one workspace via its <see cref="ProjectRecord.WorkspaceId"/>.
///
/// <para>This record is the F45 successor to the old "workspace == folder"
/// model embodied by <see cref="AgentStudio.Configuration.WorkspaceManagementService"/>;
/// the legacy service stays in place and continues to manage per-project
/// folders until F45c migrates the consumers over.</para>
/// </summary>
public record WorkspaceRecord
{
    /// <summary>Stable identifier, e.g. <c>ws-default</c>. Never re-used, never renamed.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-readable label. Free to edit, lives only here.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Render order within the sidebar. Smaller comes first.</summary>
    public int SortOrder { get; init; }
    /// <summary>True for the single workspace that owns auto-discovered legacy projects.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Optional accent colour (CSS hex string).</summary>
    public string? Color { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// F45a — per-project registry entry. The project id is the stable identity
/// for jobKey (<c>PROJ-001::job-slug</c>), folder lookups, and cross-system
/// references. Display name and short code are mutable metadata.
///
/// <para>For legacy projects discovered from <c>WatchPaths</c> on first boot,
/// <see cref="StorageLocation"/> points at the existing task folder; F45c
/// will physically relocate those folders to
/// <c>&lt;TaskRepository&gt;/projects/&lt;Id&gt;/tasks/</c> and rewrite the field.</para>
/// </summary>
public record ProjectRecord
{
    /// <summary>
    /// Backward-compatible storage discriminator. Product onboarding currently
    /// supports local folders only; existing records default to local-folder.
    /// </summary>
    public string SourceType { get; init; } = "local-folder";
    /// <summary>Stable identifier in the form <c>PROJ-001</c>. Immutable.</summary>
    public string Id { get; init; } = "";
    /// <summary>Free-text display label. Renamable without touching the filesystem.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Short prefix for task display keys (e.g. <c>ATP</c> in <c>ATP-130</c>). 2-6 chars, A-Z and 0-9.</summary>
    public string ShortCode { get; init; } = "";
    /// <summary>Owning workspace; references <see cref="WorkspaceRecord.Id"/>.</summary>
    public string WorkspaceId { get; init; } = "";
    public string? Color { get; init; }
    public string? CliDefault { get; init; }
    public string? ModelDefault { get; init; }
    public int SortOrder { get; init; }
    /// <summary>Next task key counter (monotonic, per project). Never re-used.</summary>
    public int NextTaskKeySeq { get; init; } = 1;
    /// <summary>Next document reference key counter (monotonic, per project). Never re-used.</summary>
    public int NextWorkbenchKeySeq { get; init; } = 1;
    /// <summary>
    /// Absolute path to the project's task folder on disk. For legacy
    /// auto-discovered projects this is the resolved <c>WatchPathEntry.Path</c>;
    /// for projects created via the new API it points at
    /// <c>&lt;TaskRepository&gt;/projects/&lt;Id&gt;/tasks/</c>.
    /// </summary>
    public string StorageLocation { get; init; } = "";
    /// <summary>
    /// Absolute path of the project's repository checkout, when the project
    /// has one. Durable registry data (API-mutable), NOT an appsettings
    /// override: the docs-backed wiki surface derives its root from
    /// <c>&lt;RepositoryPath&gt;/docs</c> by convention. Null for task-only
    /// projects and for repos where the path is derivable from the storage
    /// layout (<c>&lt;repo&gt;/.orchestrator/jobs</c>).
    /// </summary>
    public string? RepositoryPath { get; init; }
    /// <summary>
    /// Optional git ref used as the read-only source for the complete project
    /// wiki. Null keeps the legacy checkout-backed behaviour. A configured ref
    /// is read through git without switching the working tree.
    /// </summary>
    public string? WikiSourceBranch { get; init; }
    /// <summary>
    /// Absolute path the runner uses as the CLI's working directory, when it
    /// differs from - or simply needs to exist independently of -
    /// <see cref="RepositoryPath"/> (e.g. a monorepo subfolder). Durable
    /// registry data (API-mutable), NOT an appsettings override.
    /// <see cref="AgentStudio.Runner.TaskRunnerService"/> only creates a
    /// <c>ProjectRunner</c> for a project whose effective RootPath (this
    /// field, falling back to the WatchPaths config entry) is non-empty and
    /// exists on disk - a project with neither has no auto-pickup runner and
    /// its manual mode toggle reports the project as unavailable, not
    /// "unknown" (see <c>RunnerEndpoints</c>).
    /// </summary>
    public string? RootPath { get; init; }
    /// <summary>
    /// Ordered list of watchable URLs for this project (dev servers, preview
    /// sites, static pages). Default empty; most projects have none. Managed
    /// via the AddUrl / UpdateUrl / RemoveUrl / ReorderUrls registry mutators.
    /// </summary>
    public IReadOnlyList<ProjectUrlRecord> Urls { get; init; } = [];
    /// <summary>
    /// Versioned component ownership and delivery-chain declarations for
    /// components primarily owned by this project. These are product metadata,
    /// not prompt text. The routing resolver reads the complete project
    /// registry and returns the matching declaration to every consumer.
    /// </summary>
    public IReadOnlyList<ComponentOwnershipMapping> OwnershipMappings { get; init; } = [];
    /// <summary>Append-only audit rows for edits to <see cref="OwnershipMappings"/>.</summary>
    public IReadOnlyList<ComponentOwnershipMappingAudit> OwnershipMappingAudit { get; init; } = [];
    public bool Archived { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// One editable ownership/dependency declaration stored with its primary
/// project. Identifiers and package/repository names are stable and safe to
/// place in an orchestrator prompt; filesystem paths and secrets are excluded.
/// </summary>
public record ComponentOwnershipMapping
{
    public string Id { get; init; } = "";
    public IReadOnlyList<string> ObservedSurfaces { get; init; } = [];
    public string Component { get; init; } = "";
    public string? PackageOrModule { get; init; }
    public string PrimaryProjectId { get; init; } = "";
    public string? Repository { get; init; }
    public IReadOnlyList<string> ConsumerProjectIds { get; init; } = [];
    public IReadOnlyList<string> IntegrationHosts { get; init; } = [];
    public string? ReleaseArtifact { get; init; }
    public string? VersioningMechanism { get; init; }
    public IReadOnlyList<string> DeploymentSteps { get; init; } = [];
    public IReadOnlyList<string> Environments { get; init; } = [];
    public string AllowedTicketPrefix { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public double Confidence { get; init; } = 1;
    public IReadOnlyList<string> UnresolvedAlternatives { get; init; } = [];
    public int Version { get; init; } = 1;
    public DateTime UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "system";
}

public record ComponentOwnershipMappingAudit
{
    public string MappingId { get; init; } = "";
    public int Version { get; init; }
    public DateTime ChangedAt { get; init; }
    public string ChangedBy { get; init; } = "system";
    public string Action { get; init; } = "updated";
    public ComponentOwnershipMapping Snapshot { get; init; } = new();
}

/// <summary>
/// On-disk shape of <c>&lt;TaskRepository&gt;/.metadata/workspaces.json</c>.
/// </summary>
public record WorkspacesFile
{
    public int Version { get; init; } = 1;
    public List<WorkspaceRecord> Workspaces { get; init; } = [];
}

/// <summary>
/// On-disk shape of <c>&lt;TaskRepository&gt;/.metadata/projects.json</c>.
/// <see cref="NextProjectIdSeq"/> is the global counter the registry hands
/// out next; never wound backwards.
/// </summary>
public record ProjectsFile
{
    public int Version { get; init; } = 1;
    public int NextProjectIdSeq { get; init; } = 1;
    public List<ProjectRecord> Projects { get; init; } = [];
    public List<ProjectRepositoryAuditEvent> RepositoryAudit { get; init; } = [];
}

public sealed record ProjectRepositoryAuditEvent(
    DateTime Timestamp,
    string ProjectId,
    string ActorId,
    string Actor,
    string? PreviousRepositoryUrl,
    string? RepositoryUrl);

/// <summary>
/// Well-known constants for the F45 default workspace.
/// </summary>
public static class DefaultWorkspace
{
    public const string Id = "ws-default";
    public const string DisplayName = "Workspace";
    public const string Color = "#a78bfa";
}

/// <summary>
/// F66 — return shape for <c>DELETE /api/workspaces/{id}</c>. The delete is
/// blocked (409) while any project is still assigned, so a successful response
/// only echoes the deleted id.
/// </summary>
public sealed record WorkspaceDeleteResult
{
    public string DeletedId { get; init; } = "";
}
