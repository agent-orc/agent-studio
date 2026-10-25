using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AgentStudio.Deploy;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

public sealed record EnvironmentDeclaration(
    string Id, string Name, string Kind, string TargetHostId, string Address,
    string Template, string Destination, string? HealthUrl, string Approval,
    IReadOnlyList<string> SecretRefs, int ContainerPort,
    IReadOnlyList<HealthCheckDefinition>? HealthChecks = null, bool HealthChecksAllowPrivateNetwork = false,
    bool GuidedFailure = false, int InterventionTimeoutMinutes = 60, RunTargetHosting? Hosting = null)
{
    public IReadOnlyDictionary<string, TenantDeployTarget> TenantTargets { get; init; }
        = new Dictionary<string, TenantDeployTarget>();
    public IReadOnlyList<ProjectDeployStep> SshSteps { get; init; } = [];
    public IReadOnlyList<ProjectVariableDefinition> Variables { get; init; } = [];
    public IReadOnlyList<ProjectArtifactDefinition> Artifacts { get; init; } = [];
    public bool? Protected { get; init; }
    public bool? Automatic { get; init; }
    public string? Requires { get; init; }
}

public sealed record TenantDeployTarget(string Destination, string HealthUrl);

/// <summary>One declaration file: the parsed environment, or the error that makes it unusable.</summary>
public sealed record EnvironmentDeclarationFile(string EnvironmentId, EnvironmentDeclaration? Declaration, string? Error);

public sealed record DeploymentHistoryItem(DateTime At, string Status, string Artifact, string? PreviousArtifact,
    string? Evidence);

public sealed record ProjectEnvironmentView(EnvironmentDeclaration Declaration, string? DeployedArtifact,
    string Health, string? PendingArtifact, IReadOnlyList<DeploymentHistoryItem> History,
    RunTargetObservation? Observed = null);

public sealed record DeployJob(string Id, string Project, string EnvironmentId, string HostId,
    string Artifact, string Template, string Destination, string? HealthUrl,
    IReadOnlyList<string> SecretRefs, int ContainerPort, string? PreviousArtifact, string Status,
    DateTime CreatedAt, DateTime? ClaimedAt = null, string? LeaseToken = null, bool IsRollback = false,
    string? RequestedBy = null, DateTime? FinishedAt = null, string? Evidence = null, int Claims = 0,
    string? ReleaseId = null, string? RollbackOfJobId = null,
    bool ReservedSlot = false, bool IsStop = false, bool GuidedFailure = false,
    bool GuidedResolved = false, string? RetryStep = null, string? RetryOfJobId = null,
    DateTime? FirstClaimedAt = null, RunTargetHosting? Hosting = null)
{
    public string? Tenant { get; init; }
    public IReadOnlyDictionary<string, string> ResolvedVariables { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SecretVariableRefs { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ProjectDeployStep> SshSteps { get; init; } = [];
    public IReadOnlyList<ProjectVariableDefinition> Variables { get; init; } = [];
    public IReadOnlyList<ProjectArtifactDefinition> Artifacts { get; init; } = [];
    public IReadOnlyList<DeployStepResult> StepResults { get; init; } = [];
    /// <summary>Preview jobs share the normal claim, lease and report queue.</summary>
    public string Kind { get; init; } = "standard";
    public string? CardKey { get; init; }
    public string? SourceCommit { get; init; }
    public string? SourceRepository { get; init; }
    public string? SourceRef { get; init; }
    public string? ArtifactPath { get; init; }
    public string? Dockerfile { get; init; }
    public string? Address { get; init; }
    public IReadOnlyList<string> BuildCommands { get; init; } = [];
    public string? ScreenshotUrl { get; init; }
}

public sealed record DeployStepResult(string Name, bool Succeeded);

public sealed record PreviewJobSpec(string Project, string CardKey, string SourceCommit,
    string HostId, string Address, string Template, string Destination,
    IReadOnlyList<string> BuildCommands, IReadOnlyList<string> SecretRefs, int ContainerPort)
{
    public string SourceRepository { get; init; } = "";
    public string SourceRef { get; init; } = "";
    public string? ArtifactPath { get; init; }
    public string? Dockerfile { get; init; }
}


public sealed record EnvironmentPromotion(string Artifact, string StageId, string ProductionId,
    DateTime StageHealthyAt, string Status, string? Actor = null, DateTime? ExpiresAt = null);

public sealed record DeploymentFailureCard(string EnvironmentId, string Artifact, DateTime At,
    string Evidence, string? RollbackArtifact);

public sealed record ProjectEnvironmentsResponse(string Project, IReadOnlyList<ProjectEnvironmentView> Environments,
    IReadOnlyList<EnvironmentPromotion> Promotions, IReadOnlyList<DeployJob> Jobs,
    IReadOnlyList<DeploymentFailureCard> FailureCards, IReadOnlyList<RunTargetRemovalRecord>? RunTargetRemovals = null);

public sealed record DeployReportRequest(string LeaseToken, bool Healthy, string? Evidence,
    IReadOnlyList<DeployStepResult>? Steps = null, string? ScreenshotBase64 = null);
public sealed record DeployStepLogRequest(string LeaseToken, string Step, string Message);
public sealed record DeployLeaseRequest(string LeaseToken);
public sealed record DeployClaimRequest(string HostId);
public sealed record ReleaseArtifactRequest(string Artifact, string Commit, string? VersionTag = null,
    string? Branch = null, string? Channel = null);
public sealed record PromotionDecisionRequest(string Artifact);
public sealed record GateArtifactRequest(string Project, string Artifact, string Commit, string? VersionTag = null,
    string? Branch = null, string? Channel = null);

/// <summary>A deploy action refused by workspace or project quota admission.</summary>
public sealed class DeployQuotaRefusedException(string message) : InvalidOperationException(message);

/// <summary>Repository declarations and a separate, durable host-deploy queue.</summary>
public sealed partial class ProjectEnvironmentService
{
    public const string DeclarationDirectory = "docs/deployments/environments";
    /// <summary>After this, the claiming host may claim its job again (it restarted).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    /// <summary>A claim without report or reclaim for this long fails as <c>host-lost</c>.</summary>
    public static readonly TimeSpan HostLostAfter = TimeSpan.FromMinutes(15);
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex SecretIdentifier = new("^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex Commit = new(@"\A[0-9a-f]{40}\z", RegexOptions.Compiled);
    private static readonly Regex Digest = new(@"\A[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}\z", RegexOptions.Compiled);
    private const string HostLostEvidence = "host-lost";
    private const string AbortedEvidence = "deploy-aborted";
    private readonly object _gate = new();
    private readonly IConfiguration _configuration;
    private readonly ReleaseArtifactRegistry _registry;
    private readonly DeployStore _deployStore;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly AgentStudio.Quota.WorkspaceQuotaService? _quota;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ProjectEnvironmentService(IConfiguration configuration, TaskScannerService scanner,
        GitService git, ProjectSettingsService settings, TimeProvider clock, ReleaseArtifactRegistry? registry = null,
        DeployStore? deployStore = null, AgentStudio.Quota.WorkspaceQuotaService? quota = null)
    {
        _configuration = configuration;
        _registry = registry ?? new ReleaseArtifactRegistry(configuration);
        _deployStore = deployStore ?? new DeployStore(configuration);
        _scanner = scanner;
        _git = git;
        _settings = settings;
        _clock = clock;
        _quota = quota;
    }

    public static string SlotKey(string environmentId) => "env:" + environmentId;

    private string? QuotaRefusal(string project, string environmentId)
    {
        if (_quota is null) return null;
        var admission = _quota.Admit(project,
            hosting: !_quota.HasProjectEnvironment(project, SlotKey(environmentId)));
        return admission.Allowed ? null : admission.Message ?? "Quota reached.";
    }

    private void RequireQuota(string project, string environmentId)
    {
        if (QuotaRefusal(project, environmentId) is { } refusal)
            throw new DeployQuotaRefusedException(refusal);
    }

    private bool TryAdmitClaim(string project, DeployJob job, out bool reserved)
    {
        reserved = false;
        if (_quota is null) return true;
        try
        {
            var key = SlotKey(job.EnvironmentId);
            if (_quota.HasProjectEnvironment(project, key)) return _quota.Admit(project).Allowed;
            reserved = _quota.ReserveProjectEnvironment(project, key);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public IReadOnlyList<EnvironmentDeclaration>? Declarations(string project)
    {
        var files = DeclarationFiles(project);
        if (files is null) return null;
        if (files.FirstOrDefault(item => item.Error is not null) is { Error: { } error }) throw new JsonException(error);
        return files.Select(item => item.Declaration!).ToArray();
    }

    /// <summary>
    /// Every declaration file of a project, each parsed on its own: a broken file carries its error
    /// instead of failing the others, so health monitoring (IP-14) keeps watching the valid ones.
    /// </summary>
    public IReadOnlyList<EnvironmentDeclarationFile>? DeclarationFiles(string project)
    {
        if (!IsWatched(project)) return null;
        var root = _git.ResolveRepoRootForProject(project);
        if (root is null) return null;
        var directory = Path.Combine(root, "docs", "deployments", "environments");
        var readable = new List<(string Name, string Json)>();
        var failures = new List<EnvironmentDeclarationFile>();
        foreach (var file in Directory.Exists(directory)
                     ? Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     : Enumerable.Empty<string>())
        {
            try
            {
                readable.Add((Path.GetFileName(file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new(Path.GetFileNameWithoutExtension(file), null, $"{Path.GetFileName(file)}: {ex.Message}"));
            }
        }
        IReadOnlyList<EnvironmentDeclarationFile> parsed = [.. ParseDeclarationFiles(readable), .. failures];
        var yamlPath = Path.Combine(root, ProjectPreparationPaths.Definition);
        if (!File.Exists(yamlPath)) return parsed;
        try { return ApplyDefinition(parsed, File.ReadAllText(yamlPath)); }
        catch (JsonException ex)
        {
            return parsed.Select(file => file with { Declaration = null, Error = ex.Message }).ToArray();
        }
    }

    /// <summary>
    /// BD-1 release snapshot: the declarations as committed at
    /// <paramref name="commit"/>, not as the working tree has them now. Null
    /// when the project, the commit or any declaration file cannot be read:
    /// release ids are deterministic, so a partial snapshot could never be
    /// repaired later. A committed declaration that does not parse throws
    /// <see cref="JsonException"/>.
    /// </summary>
    public IReadOnlyList<EnvironmentDeclaration>? DeclarationsAtCommit(string project, string commit)
    {
        if (!IsWatched(project) || !Commit.IsMatch(commit)) return null;
        var root = _git.ResolveRepoRootForProject(project);
        if (root is null) return null;
        var paths = _git.ListFilesAtCommit(root, commit, DeclarationDirectory);
        if (paths is null) return null;
        var files = new List<(string Name, string Json)>();
        foreach (var path in paths.Where(item => item.EndsWith(".json", StringComparison.Ordinal)))
        {
            if (_git.GetFileAtCommit(root, commit, path) is not { } json) return null;
            files.Add((Path.GetFileName(path), json));
        }
        var parsed = ParseDeclarationFiles(files);
        if (parsed.FirstOrDefault(item => item.Error is not null) is { Error: { } error }) throw new JsonException(error);
        var yaml = _git.GetFileAtCommit(root, commit, ProjectPreparationPaths.Definition);
        var effective = yaml is null ? parsed : ApplyDefinition(parsed, yaml);
        if (effective.FirstOrDefault(item => item.Error is not null) is { Error: { } mismatch }) throw new JsonException(mismatch);
        return effective.Select(item => item.Declaration!).ToArray();
    }

    /// <summary>Schema 2 owns deploy behavior. JSON retains only host registration and legacy compatibility.</summary>
    public static IReadOnlyList<EnvironmentDeclarationFile> ApplyDefinition(
        IReadOnlyList<EnvironmentDeclarationFile> files, string yaml)
    {
        var read = ProjectDefinitionReader.Parse(yaml);
        if (read.Parsed?.SchemaVersion != 2
            && !Regex.IsMatch(yaml, @"(?m)^schemaVersion:[ ]*2[ ]*(?=\r?$)")) return files;
        if (read.Parsed?.Deployment is null || read.Parsed.Deployment.Deploy.Count == 0)
            throw new JsonException("Schema 2 project.yml must declare the deploy steps before a release can run.");
        if (!read.IsValid || read.Definition?.Deployment is not { } definition)
            throw new JsonException("Schema 2 project.yml is invalid: " + string.Join("; ",
                read.Issues.Select(item => $"{item.Path}: {item.Message}")));
        if (definition.Deploy.Any(item => item.Step is not ("static-site" or "container")))
            throw new JsonException("Schema 2 release deployment only supports static-site and container SSH steps.");
        var output = new List<EnvironmentDeclarationFile>();
        foreach (var environment in definition.Environments.Where(item =>
                     !files.Any(file => PipelineMigrationPolicy.EnvironmentFor(file.Declaration?.Kind ?? "") == item.Name)))
        {
            var steps = definition.Deploy.Where(item => item.Environments.Count == 0
                || item.Environments.Contains(environment.Name, StringComparer.Ordinal)).ToArray();
            if (steps.Length == 0 || steps.Select(item => item.Target).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new JsonException($"Schema 2 {environment.Name} requires deploy steps on one host.");
            var first = steps[0];
            var phase = definition.StandardLifecycle.First(item => item.Phase == environment.Name);
            var variables = definition.Variables.Where(item => item.Environments.Count == 0
                || item.Environments.Contains(environment.Name, StringComparer.Ordinal)).ToArray();
            var id = environment.Name == "staging" ? "stage" : environment.Name;
            var declaration = new EnvironmentDeclaration(id, environment.Name, id, first.Target!,
                first.HealthUrl ?? "", first.Step == "static-site" ? "static" : "container",
                first.Directory ?? "1", first.HealthUrl,
                environment.Protected ? "person" : "stage-healthy-10m",
                variables.Where(item => item.Secret is not null).Select(item => item.Secret!).ToArray(), 8080)
            {
                SshSteps = steps, Variables = variables, Artifacts = definition.Artifacts,
                Protected = environment.Protected, Automatic = phase.Automatic, Requires = phase.Requires,
            };
            output.Add(new(id, declaration, null));
        }
        foreach (var file in files)
        {
            if (file.Declaration is not { } legacy) { output.Add(file); continue; }
            var name = PipelineMigrationPolicy.EnvironmentFor(legacy.Kind);
            if (name is null) { output.Add(file); continue; }
            var environment = definition.Environments.FirstOrDefault(item => item.Name == name);
            var phase = definition.StandardLifecycle.FirstOrDefault(item => item.Phase == name);
            var steps = definition.Deploy.Where(item => (item.Step is "static-site" or "container")
                && (item.Environments.Count == 0 || item.Environments.Contains(name, StringComparer.Ordinal))).ToArray();
            var variables = definition.Variables.Where(item => item.Environments.Count == 0
                || item.Environments.Contains(name, StringComparer.Ordinal)).ToArray();
            var secrets = variables.Where(item => item.Secret is not null).Select(item => item.Secret!).Order(StringComparer.Ordinal).ToArray();
            var first = steps.FirstOrDefault();
            if (environment is null || phase is null || first is null || steps.Any(item => item.Target != legacy.TargetHostId)
                || first.Target != legacy.TargetHostId || legacy.Template != (first.Step == "static-site" ? "static" : "container")
                || first.HealthUrl != legacy.HealthUrl
                || first.Step == "static-site" && first.Directory != legacy.Destination
                || !secrets.SequenceEqual(legacy.SecretRefs.Order(StringComparer.Ordinal)))
            {
                output.Add(file with { Declaration = null,
                    Error = $"{file.EnvironmentId}.json diverges from schema 2 project.yml; migrate the declaration before deploying." });
                continue;
            }
            output.Add(file with { Declaration = legacy with
            {
                SshSteps = steps, Variables = variables, Artifacts = definition.Artifacts,
                Protected = environment.Protected,
                Automatic = phase.Automatic, Requires = phase.Requires,
            } });
        }
        return output;
    }

    private bool IsWatched(string project)
        => _scanner.GetWatchPaths().Any(item => string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase));

    private enum DeclarationSource { UnknownProject, CheckoutMissing, FolderMissing, Read }

    /// <summary>A complete deployment read distinguishes missing source from an empty declaration set.</summary>
    private (DeclarationSource Source, IReadOnlyList<EnvironmentDeclaration> Declarations, IReadOnlySet<string> Tombstones)
        ReadDeclarations(string project)
    {
        var tombstones = new HashSet<string>(StringComparer.Ordinal);
        if (!IsWatched(project)) return (DeclarationSource.UnknownProject, [], tombstones);
        var root = _git.ResolveRepoRootForProject(project);
        if (root is null) return (DeclarationSource.CheckoutMissing, [], tombstones);
        var directory = Path.Combine(root, DeclarationDirectory);
        if (!Directory.Exists(directory)) return (DeclarationSource.FolderMissing, [], tombstones);
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => (Name: Path.GetFileName(file), Json: File.ReadAllText(file))).ToArray();
        foreach (var (name, json) in files)
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("removed", out var removed)) continue;
            if (removed.ValueKind != JsonValueKind.True || value.EnumerateObject().Count() != 2
                || !value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !Identifier.IsMatch(id.GetString() ?? ""))
                throw new JsonException($"Environment {name} has an invalid removal tombstone.");
            if (!tombstones.Add(id.GetString()!)) throw new JsonException("Duplicate environment removal tombstone.");
        }
        var parsed = ParseDeclarationFiles(files);
        if (parsed.FirstOrDefault(item => item.Error is not null) is { Error: { } error }) throw new JsonException(error);
        var declarations = parsed.Select(item => item.Declaration!).ToArray();
        if (declarations.Any(item => tombstones.Contains(item.Id)))
            throw new JsonException("An environment id cannot be declared and removed at the same time.");
        return (DeclarationSource.Read, declarations, tombstones);
    }

    private static IReadOnlyList<EnvironmentDeclarationFile> ParseDeclarationFiles(IEnumerable<(string Name, string Json)> files)
    {
        var result = new List<EnvironmentDeclarationFile>();
        foreach (var (file, json) in files)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("removed", out var removed))
                {
                    if (removed.ValueKind != JsonValueKind.True || document.RootElement.EnumerateObject().Count() != 2
                        || !document.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                        || !Identifier.IsMatch(id.GetString() ?? ""))
                        throw new JsonException($"Environment {file} has an invalid removal tombstone.");
                    continue;
                }
                var declaration = ParseDeclaration(file, json);
                result.Add(new(declaration.Id, declaration, null));
            }
            catch (JsonException ex)
            {
                result.Add(new(BrokenDeclarationId(file, json), null, $"{file}: {ex.Message}"));
            }
        }
        var duplicates = result.Where(item => item.Declaration is not null).GroupBy(item => item.EnvironmentId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return result.Select(item => item.Declaration is not null && duplicates.Contains(item.EnvironmentId)
                ? item with { Declaration = null, Error = "Environment ids must be unique within a project." } : item)
            .OrderBy(item => item.Declaration?.Kind switch { null => 3, "stage" => 0, "production" => 1, _ => 2 })
            .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal).ToArray();
    }

    private static string BrokenDeclarationId(string file, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } value && Identifier.IsMatch(value))
                return value;
        }
        catch (JsonException)
        {
            return Path.GetFileNameWithoutExtension(file);
        }
        return Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>
    /// Parses one declaration. Every malformed input surfaces as <see cref="JsonException"/>, so one
    /// broken project never turns a release, the deploy matrix or health monitoring into a 500.
    /// </summary>
    private static EnvironmentDeclaration ParseDeclaration(string file, string json)
    {
        try
        {
            return ParseDeclarationCore(file, json);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException
            or ArgumentException or NotSupportedException)
        {
            throw new JsonException($"Environment {file} is malformed: {ex.Message}", ex);
        }
    }

    private static EnvironmentDeclaration ParseDeclarationCore(string file, string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Environment {Path.GetFileName(file)} must be a JSON object.");
        // A missing or mistyped property is a broken declaration (JsonException), never a server error.
        string Required(string name) => Optional(name)
            ?? throw new JsonException($"Environment {Path.GetFileName(file)} requires {name}.");
        string? Optional(string name) => !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null : property.ValueKind == JsonValueKind.String ? property.GetString()
            : throw new JsonException($"Environment {Path.GetFileName(file)} {name} must be a string.");
        var id = Required("id");
        var kind = Required("kind");
        var host = Required("targetHostId");
        var template = Required("template");
        var secrets = !value.TryGetProperty("secretRefs", out var refs) ? []
            : refs.ValueKind == JsonValueKind.Array && refs.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
                ? refs.EnumerateArray().Select(item => item.GetString()!).ToArray()
                : throw new JsonException($"Environment {Path.GetFileName(file)} secretRefs must be an array of strings.");
        if (!Identifier.IsMatch(id) || !Identifier.IsMatch(host) || kind is not ("stage" or "production" or "preview" or "other")
            || template is not ("container" or "static") || secrets.Any(item => !SecretIdentifier.IsMatch(item)))
            throw new JsonException($"Environment {Path.GetFileName(file)} has invalid identifiers, kind, template or secret refs.");
        var destination = Required("destination");
        if (template == "static" && (!Path.IsPathFullyQualified(destination) || destination.Contains("..", StringComparison.Ordinal)))
            throw new JsonException("Static destination must be an absolute path without traversal.");
        var edgeHosted = template == "container" && destination == RunTargetVocabulary.EdgeDestination;
        if (template == "container" && !edgeHosted && (!int.TryParse(destination, out var port) || port is < 1 or > 65535))
            throw new JsonException("Container destination must be a local proxy port or 'edge'.");
        var healthUrl = Optional("healthUrl");
        if (kind is "stage" or "production" && healthUrl is null)
            throw new JsonException("Stage and production require a health URL.");
        if (healthUrl is not null && (!Uri.TryCreate(healthUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")))
            throw new JsonException("Health URL must use HTTP or HTTPS.");
        var approval = Optional("approval") ?? "person";
        if (approval is not ("person" or "stage-healthy-10m")) throw new JsonException("Unknown approval rule.");
        var containerPort = !value.TryGetProperty("containerPort", out var containerPortValue) ? 8080
            : containerPortValue.ValueKind == JsonValueKind.Number && containerPortValue.TryGetInt32(out var parsedPort)
                ? parsedPort : throw new JsonException("Invalid container port.");
        if (containerPort is < 1 or > 65535) throw new JsonException("Invalid container port.");
        var targets = new Dictionary<string, TenantDeployTarget>(StringComparer.Ordinal);
        if (value.TryGetProperty("tenantTargets", out var tenantTargets))
        {
            if (edgeHosted)
                throw new JsonException("Tenant targets are not supported for edge hosting.");
            if (tenantTargets.ValueKind != JsonValueKind.Object)
                throw new JsonException("tenantTargets must be an object keyed by tenant name.");
            foreach (var entry in tenantTargets.EnumerateObject())
            {
                if (!Identifier.IsMatch(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object
                    || !entry.Value.TryGetProperty("destination", out var tenantDestination)
                    || tenantDestination.ValueKind != JsonValueKind.String
                    || !entry.Value.TryGetProperty("healthUrl", out var tenantHealth)
                    || tenantHealth.ValueKind != JsonValueKind.String
                    || entry.Value.EnumerateObject().Any(property => property.Name is not ("destination" or "healthUrl")))
                    throw new JsonException($"Invalid target for tenant '{entry.Name}'.");
                var route = tenantDestination.GetString()!;
                var check = tenantHealth.GetString()!;
                if (template == "static" && (!Path.IsPathFullyQualified(route)
                    || route.Contains("..", StringComparison.Ordinal)))
                    throw new JsonException($"Invalid static destination for tenant '{entry.Name}'.");
                if (template == "container" && (!int.TryParse(route, out var tenantPort)
                    || tenantPort is < 1 or > 65535))
                    throw new JsonException($"Invalid container destination for tenant '{entry.Name}'.");
                if (!Uri.TryCreate(check, UriKind.Absolute, out var checkUri)
                    || checkUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(checkUri.UserInfo))
                    throw new JsonException($"Invalid health URL for tenant '{entry.Name}'.");
                if (!targets.TryAdd(entry.Name, new(route, check)))
                    throw new JsonException($"Duplicate target for tenant '{entry.Name}'.");
            }
            var destinations = targets.Values.Select(target => target.Destination).Append(destination)
                .Select(route => template == "container" ? int.Parse(route).ToString() : Path.GetFullPath(route))
                .ToArray();
            if (template == "container" && destinations.Distinct(StringComparer.Ordinal).Count() != destinations.Length
                || template == "static" && destinations.Where((path, index) => destinations
                    .Where((_, other) => other != index).Any(other => path == other
                        || path.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal))).Any())
                throw new JsonException("Tenant destinations must be isolated from each other and the project target.");
            var checks = targets.Values.Select(target => target.HealthUrl).Append(healthUrl).ToArray();
            if (checks.Distinct(StringComparer.Ordinal).Count() != checks.Length)
                throw new JsonException("Tenant health URLs must be distinct from each other and the project URL.");
        }
        var guidedFailure = !value.TryGetProperty("guidedFailure", out var guided) ? false
            : guided.ValueKind is JsonValueKind.True or JsonValueKind.False ? guided.GetBoolean()
            : throw new JsonException("guidedFailure must be a boolean.");
        var timeout = !value.TryGetProperty("interventionTimeoutMinutes", out var timeoutValue) ? 60
            : timeoutValue.ValueKind == JsonValueKind.Number && timeoutValue.TryGetInt32(out var parsedTimeout)
                ? parsedTimeout : throw new JsonException("Invalid intervention timeout.");
        if (timeout is < 1 or > 10080) throw new JsonException("Intervention timeout must be 1 to 10080 minutes.");
        return new(id, Required("name"), kind, host, Required("address"), template,
            destination, healthUrl, approval, secrets, containerPort,
            HealthCheckDeclarations.Parse(value, Path.GetFileName(file)),
            HealthCheckDeclarations.AllowsPrivateNetwork(value, Path.GetFileName(file)),
            guidedFailure, timeout,
            edgeHosted ? RunTargetHostingDeclaration.Parse(value, Path.GetFileName(file), healthUrl, secrets) : null)
        { TenantTargets = targets };
    }

    public ProjectEnvironmentsResponse? Read(string project)
    {
        lock (_gate)
        {
            var declarations = Declarations(project);
            if (declarations is null) return null;
            var state = Load(project);
            AdvanceQueue(state);
            Save(project, state);
            return new(project, declarations.Select(item =>
            {
                var history = state.History.Where(row => row.EnvironmentId == item.Id)
                    .Reverse().Take(30).Select(row => row.Item).ToArray();
                var latest = history.FirstOrDefault();
                var pending = state.Jobs.LastOrDefault(job => job.EnvironmentId == item.Id && !job.IsStop
                    && (job.Status is "queued" or "claimed"));
                return new ProjectEnvironmentView(item, latest?.Artifact,
                    latest is null ? "unknown" : latest.Status is "failed" or "stopped" ? latest.Status : "healthy",
                    pending?.Artifact, history,
                    state.Observed.LastOrDefault(row => row.EnvironmentId == item.Id)?.Observation);
            }).ToArray(), state.Promotions.ToArray(), state.Jobs.Select(Sanitize).ToArray(),
                state.History.Where(row => row.Item.Status == "failed").AsEnumerable().Reverse()
                    .Take(20).Select(row => new DeploymentFailureCard(row.EnvironmentId,
                        row.Item.Artifact, row.At, row.Item.Evidence ?? "deploy-step-failed",
                        row.Item.PreviousArtifact)).ToArray(),
                RunTargetDesiredStatePolicy.Removals(state.Placements, state.RunTargetRemovals));
        }
    }

    public bool QueueRelease(string project, string artifact, string commit)
        => CanQueueRelease(project, artifact, commit) && QueueReleaseJob(project, artifact, commit) is not null;

    /// <summary>
    /// The gate-built artifact may start a release: the gate recorded it for
    /// the commit, the commit is on the integration branch, and no promotion of
    /// the artifact is open.
    /// </summary>
    public bool CanQueueRelease(string project, string artifact, string commit)
    {
        if (!Digest.IsMatch(artifact) || !Commit.IsMatch(commit)) return false;
        if (!HasBuiltArtifact(project, artifact, commit)) return false;
        var repo = _git.ResolveRepoRootForProject(project);
        if (repo is null || !_git.IsGitRepo(repo)) return false;
        var branch = _git.ResolveIntegrationBranch(repo, _settings.Get(project).IntegrationBranch);
        var tip = _git.GetBranchTip(repo, branch);
        if (tip is null || !_git.IsAncestor(repo, commit, tip))
            return false;
        lock (_gate)
        {
            return !HasOpenPromotion(Load(project), artifact);
        }
    }

    public bool HasVerifiedArtifact(string project, string artifact, string commit, string template)
        => _registry.Verified(project, commit, artifact, template) is not null;

    /// <summary>
    /// Queues stage for a gate-built artifact and returns the stage job. The
    /// caller has passed <see cref="CanQueueRelease"/> (its git checks stay
    /// outside the caller's lock); only the recorded artifact and the queue
    /// state are checked again here. With <paramref name="snapshot"/> (a BD-1
    /// release) the job and the promotion use the declarations stored in the
    /// release; the environments must still be declared, and the job goes to
    /// the host the current declaration names (<see cref="RouteToCurrentHost"/>).
    /// </summary>
    public DeployJob? QueueReleaseJob(string project, string artifact, string commit,
        IReadOnlyList<EnvironmentDeclaration>? snapshot = null, string? releaseId = null,
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, string>? secretVariableRefs = null)
    {
        if (!Digest.IsMatch(artifact) || !Commit.IsMatch(commit) || !HasBuiltArtifact(project, artifact, commit))
            return null;
        lock (_gate)
        {
            var current = Declarations(project);
            var declarations = snapshot ?? current;
            var stage = declarations?.FirstOrDefault(item => item.Kind == "stage");
            var production = declarations?.FirstOrDefault(item => item.Kind == "production");
            if (stage is null || production is null || !IsDeclared(current, stage.Id) || !IsDeclared(current, production.Id))
                return null;
            if (stage.Template == "static" && (variables?.Count > 0 || secretVariableRefs?.Count > 0)) return null;
            if (!HasVerifiedArtifact(project, artifact, commit, stage.Template)
                || !HasVerifiedArtifact(project, artifact, commit, production.Template)) return null;
            var state = Load(project);
            if (HasOpenPromotion(state, artifact)) return null;
            RequireQuota(project, stage.Id);
            state.Promotions.Add(new(artifact, stage.Id, production.Id, DateTime.MinValue, "stage-queued"));
            var job = Queue(project, RouteToCurrentHost(stage, current!), artifact, state,
                actor: "release-gate", releaseId: releaseId) with
            { Variables = [], ResolvedVariables = variables ?? new Dictionary<string, string>(),
                SecretVariableRefs = secretVariableRefs ?? new Dictionary<string, string>() };
            job = job with { SecretRefs = job.SecretRefs.Concat(job.SecretVariableRefs.Values)
                .Distinct(StringComparer.Ordinal).ToArray() };
            state.Jobs[^1] = job;
            Save(project, state);
            return job;
        }
    }

    /// <summary>
    /// Queues an already released artifact into one environment with the
    /// release's snapshot of its declaration, on the host the current
    /// declaration names. The caller (the BD-1 lifecycle) has decided that the
    /// deployment may start.
    /// </summary>
    public DeployJob? QueueDeployment(string project, EnvironmentDeclaration target, string artifact, string actor,
        bool rollback, string? tenant = null, string? releaseId = null, string? retryStep = null,
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, string>? secretVariableRefs = null)
    {
        if (!Digest.IsMatch(artifact)) return null;
        lock (_gate)
        {
            var current = Declarations(project);
            if (!IsDeclared(current, target.Id)) return null;
            if (tenant is not null && !target.TenantTargets.ContainsKey(tenant)) return null;
            if (target.Template == "static" && (variables?.Count > 0 || secretVariableRefs?.Count > 0)) return null;
            var state = Load(project);
            RequireQuota(project, target.Id);
            var routed = RouteToCurrentHost(target, current!);
            if (tenant is not null)
                routed = RouteTenant(routed, tenant, target.TenantTargets[tenant]);
            var job = Queue(project, routed, artifact, state, rollback, actor, tenant, releaseId,
                retryStep: retryStep) with
            { Variables = [], ResolvedVariables = variables ?? new Dictionary<string, string>(),
                SecretVariableRefs = secretVariableRefs ?? new Dictionary<string, string>() };
            job = job with { SecretRefs = job.SecretRefs.Concat(job.SecretVariableRefs.Values)
                .Distinct(StringComparer.Ordinal).ToArray() };
            state.Jobs[^1] = job;
            Save(project, state);
            return job;
        }
    }

    /// <summary>Retry only while no newer job has entered this environment.</summary>
    public DeployJob? QueueRetry(string project, EnvironmentDeclaration target, string artifact, string actor,
        string failedJobId, string? retryStep)
    {
        if (!Digest.IsMatch(artifact) || retryStep is not (null or "health")) return null;
        lock (_gate)
        {
            var current = Declarations(project);
            if (!IsDeclared(current, target.Id)) return null;
            var state = Load(project);
            var index = state.Jobs.FindIndex(item => item.Id == failedJobId && item.Status == "failed"
                && item.EnvironmentId == target.Id);
            if (index < 0 || state.Jobs.Skip(index + 1).Any(item => item.EnvironmentId == target.Id
                && item.Tenant == state.Jobs[index].Tenant)) return null;
            RequireQuota(project, target.Id);
            var failed = state.Jobs[index];
            var routed = RouteToCurrentHost(target, current!);
            if (failed.Tenant is not null)
            {
                if (!target.TenantTargets.TryGetValue(failed.Tenant, out var tenantTarget)) return null;
                routed = RouteTenant(routed, failed.Tenant, tenantTarget);
            }
            var job = Queue(project, routed, artifact, state,
                rollback: failed.IsRollback, actor: actor, tenant: failed.Tenant,
                releaseId: state.Jobs[index].ReleaseId, retryStep: retryStep,
                retryOfJobId: failedJobId) with { Variables = [], ResolvedVariables = failed.ResolvedVariables,
                    SecretVariableRefs = failed.SecretVariableRefs, SecretRefs = failed.SecretRefs };
            state.Jobs[^1] = job;
            Save(project, state);
            return job;
        }
    }

    /// <summary>Queues a card build and deployment on the same fenced host queue as release deployments.</summary>
    public DeployJob QueuePreview(PreviewJobSpec spec)
    {
        if (!Commit.IsMatch(spec.SourceCommit) || string.IsNullOrWhiteSpace(spec.SourceRepository)
            || string.IsNullOrWhiteSpace(spec.SourceRef) || !Identifier.IsMatch(spec.HostId)
            || !Identifier.IsMatch(spec.CardKey.ToLowerInvariant())
            || spec.Template is not ("static" or "container")
            || spec.SecretRefs.Any(secret => !SecretIdentifier.IsMatch(secret)))
            throw new ArgumentException("Invalid preview job specification.", nameof(spec));
        lock (_gate)
        {
            EnsurePreviewHostIsNotProduction(spec.HostId);
            var state = Load(spec.Project);
            var job = new DeployJob(Guid.NewGuid().ToString("N"), spec.Project,
                "preview-" + spec.CardKey.ToLowerInvariant(), spec.HostId,
                "source:" + spec.SourceCommit, spec.Template, spec.Destination, spec.Address,
                spec.SecretRefs, spec.ContainerPort, null, "queued", _clock.GetUtcNow().UtcDateTime,
                RequestedBy: "card-preview")
            {
                Kind = "preview", CardKey = spec.CardKey, SourceCommit = spec.SourceCommit,
                SourceRepository = spec.SourceRepository, SourceRef = spec.SourceRef,
                ArtifactPath = spec.ArtifactPath, Dockerfile = spec.Dockerfile,
                Address = spec.Address, BuildCommands = spec.BuildCommands,
            };
            state.Jobs.Add(job);
            Save(spec.Project, state);
            return job;
        }
    }

    /// <summary>Removal is fenced and retried by the same host queue.</summary>
    public DeployJob QueuePreviewRemoval(PreviewJobSpec spec)
    {
        lock (_gate)
        {
            EnsurePreviewHostIsNotProduction(spec.HostId);
            var state = Load(spec.Project);
            var job = new DeployJob(Guid.NewGuid().ToString("N"), spec.Project,
                "preview-" + spec.CardKey.ToLowerInvariant(), spec.HostId,
                "source:" + spec.SourceCommit, spec.Template, spec.Destination, null,
                [], spec.ContainerPort, null, "queued", _clock.GetUtcNow().UtcDateTime,
                RequestedBy: "card-preview-cleanup")
            {
                Kind = "preview-remove", CardKey = spec.CardKey,
                SourceCommit = spec.SourceCommit, Address = spec.Address,
                SourceRepository = spec.SourceRepository, SourceRef = spec.SourceRef,
                ArtifactPath = spec.ArtifactPath, Dockerfile = spec.Dockerfile,
            };
            state.Jobs.Add(job);
            Save(spec.Project, state);
            return job;
        }
    }

    /// <summary>
    /// Stops a job before its host reports and closes its promotion. A queued
    /// job never starts. With <paramref name="abortClaimed"/> a claimed job is
    /// aborted: its lease is revoked, so a late report is refused, and the
    /// environment records the interrupted deploy as failed.
    /// </summary>
    public bool CancelJob(string project, string jobId, string actor, bool abortClaimed)
    {
        lock (_gate)
        {
            var state = Load(project);
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId
                && (item.Status == "queued" || abortClaimed && item.Status == "claimed"));
            if (job is null) return false;
            var now = _clock.GetUtcNow().UtcDateTime;
            var aborted = job.Status == "claimed";
            state.Jobs[state.Jobs.IndexOf(job)] = job with
            {
                Status = "cancelled", LeaseToken = null, FinishedAt = now,
                Evidence = aborted ? AbortedEvidence : null,
            };
            if (aborted && !job.IsStop)
                state.History.Add(new(job.EnvironmentId, now,
                    new(now, "failed", job.Artifact, job.PreviousArtifact, AbortedEvidence)));
            var promotion = job.Tenant is null ? state.Promotions.LastOrDefault(item => item.Artifact == job.Artifact
                && (item.Status == "stage-queued" && item.StageId == job.EnvironmentId
                    || item.Status == "production-queued" && item.ProductionId == job.EnvironmentId)) : null;
            if (promotion is not null)
                state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with { Status = "cancelled", Actor = actor };
            Save(project, state);
            return true;
        }
    }

    // The gate endpoint is service-credential only. It records the built
    // artifact's immutable digest against the exact released commit.
    public bool RegisterGateArtifact(string project, string artifact, string commit,
        string builderIdentity = "release-gate")
    {
        if (!Digest.IsMatch(artifact) || !Commit.IsMatch(commit)) return false;
        if (!HasIndependentReleaseEvidence(project, artifact, commit)) return false;
        var repo = _git.ResolveRepoRootForProject(project);
        if (repo is null || !_git.IsGitRepo(repo)) return false;
        var branch = _git.ResolveIntegrationBranch(repo, _settings.Get(project).IntegrationBranch);
        var tip = _git.GetBranchTip(repo, branch);
        if (tip is null || !_git.IsAncestor(repo, commit, tip))
            return false;
        lock (_gate)
        {
            if (!_registry.PushVerified(project, commit, artifact, builderIdentity)) return false;
            var path = ArtifactPath(project, commit, artifact);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
                return string.Equals(File.ReadAllText(path).Trim(), artifact, StringComparison.Ordinal);
            File.WriteAllText(path, artifact + "\n");
            return true;
        }
    }

    private bool HasIndependentReleaseEvidence(string project, string artifact, string commit)
    {
        // This directory is written by the promotion train, not by this API or
        // its release credential. A bearer alone cannot attest a build or gate.
        var root = _configuration["Deploy:ReleaseEvidenceRoot"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
        var directory = Path.Combine(root, commit);
        try
        {
            var archive = Path.Combine(directory, "release.tar.gz");
            var expected = File.ReadAllText(Path.Combine(directory, "release.sha256")).Trim();
            if (!Regex.IsMatch(expected, "^[0-9a-f]{64}$")) return false;
            using var stream = File.OpenRead(archive);
            var measured = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (measured != expected) return false;
            if (!File.ReadLines(Path.Combine(directory, "full-gate.log"))
                    .Any(line => line == "PROMOTION_FULL_GATE=passed")) return false;
            using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "promotion-record.json")));
            var release = record.RootElement;
            if (release.GetProperty("status").GetString() != "promoted"
                || release.GetProperty("gate").GetString() != "passed"
                || release.GetProperty("candidateSha").GetString() != commit
                || !release.GetProperty("atomicPush").GetBoolean()
                || release.GetProperty("releasePackage").GetProperty("status").GetString() != "written"
                || release.GetProperty("releasePackage").GetProperty("sha256").GetString() != expected)
                return false;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "deploy-artifact.json")));
            var value = manifest.RootElement;
            return value.GetProperty("project").GetString() == project
                && value.GetProperty("commit").GetString() == commit
                && value.GetProperty("artifact").GetString() == artifact
                && (artifact == "release-package@sha256:" + expected
                    || value.GetProperty("sourceSha256").GetString() == expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool HasBuiltArtifact(string project, string artifact, string commit)
    {
        var path = ArtifactPath(project, commit, artifact);
        return File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), artifact, StringComparison.Ordinal)
            && _registry.VerifiedForArtifact(project, commit, artifact) is not null;
    }

    private string ArtifactPath(string project, string commit, string artifact)
        => Path.Combine(StoreRoot(), "gate-artifacts", SafeProjectName(project), commit,
            artifact[(artifact.LastIndexOf("@sha256:", StringComparison.Ordinal) + 8)..] + ".txt");

    public bool Decide(string project, string artifact, bool approve, string actor)
        => DecidePromotion(project, artifact, approve, actor).Decided;

    /// <summary>
    /// Decides a pending promotion; an approval returns the production job.
    /// With <paramref name="target"/> (the BD-1 release snapshot of the
    /// production declaration) the job deploys that snapshot on the host the
    /// current declaration names.
    /// </summary>
    public (bool Decided, DeployJob? Job) DecidePromotion(string project, string artifact, bool approve, string actor,
        EnvironmentDeclaration? target = null, string? releaseId = null,
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, string>? secretVariableRefs = null)
    {
        lock (_gate)
        {
            var declarations = Declarations(project);
            if (declarations is null) return (false, null);
            var state = Load(project);
            var promotion = state.Promotions.LastOrDefault(item => item.Artifact == artifact && item.Status == "pending");
            if (promotion is null || promotion.ExpiresAt is null
                || promotion.ExpiresAt <= _clock.GetUtcNow().UtcDateTime) return (false, null);
            var production = declarations.FirstOrDefault(item => item.Id == promotion.ProductionId);
            if (production is null || target is not null && target.Id != production.Id) return (false, null);
            if (approve && (target ?? production).Template == "static"
                && (variables?.Count > 0 || secretVariableRefs?.Count > 0)) return (false, null);
            if (approve) RequireQuota(project, production.Id);
            state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with
            { Status = approve ? "production-queued" : "rejected", Actor = actor };
            var job = approve
                ? Queue(project, target is null ? production : RouteToCurrentHost(target, declarations), artifact, state,
                    actor: actor, releaseId: releaseId) with { Variables = releaseId is null
                        ? (target ?? production).Variables : [],
                        ResolvedVariables = variables ?? new Dictionary<string, string>(),
                        SecretVariableRefs = secretVariableRefs ?? new Dictionary<string, string>() }
                : null;
            if (job is not null)
            {
                job = job with { SecretRefs = job.SecretRefs.Concat(job.SecretVariableRefs.Values)
                    .Distinct(StringComparer.Ordinal).ToArray() };
                state.Jobs[^1] = job;
            }
            Save(project, state);
            return (true, job);
        }
    }

    /// <summary>
    /// Queues a rollback to the last healthy artifact in the environment's
    /// history that differs from the current one. BD-1 passes
    /// <paramref name="acceptTarget"/> so the history rollback never redeploys
    /// an artifact that belongs to a release; the lifecycle owns those.
    /// </summary>
    public bool Rollback(string project, string environmentId, string actor, Func<string, bool>? acceptTarget = null)
    {
        lock (_gate)
        {
            var declaration = Declarations(project)?.FirstOrDefault(item => item.Id == environmentId);
            if (declaration is null) return false;
            var state = Load(project);
            var currentArtifact = state.History.LastOrDefault(row => row.EnvironmentId == environmentId)?.Item.Artifact;
            var previous = state.History.LastOrDefault(row => row.EnvironmentId == environmentId
                && (row.Item.Status is "healthy" or "rolled-back")
                && row.Item.Artifact != currentArtifact);
            if (previous is null || acceptTarget?.Invoke(previous.Item.Artifact) == false) return false;
            RequireQuota(project, environmentId);
            Queue(project, DeployedSpec(state, declaration, previous.Item.Artifact), previous.Item.Artifact, state,
                rollback: true, actor: actor);
            Save(project, state);
            return true;
        }
    }

    private bool QueueRollback(string project, EnvironmentDeclaration declaration, State state, string actor)
    {
        var current = state.History.LastOrDefault(row => row.EnvironmentId == declaration.Id)?.Item.Artifact;
        var previous = state.History.LastOrDefault(row => row.EnvironmentId == declaration.Id
            && (row.Item.Status is "healthy" or "rolled-back") && row.Item.Artifact != current);
        if (previous is null) return false;
        Queue(project, DeployedSpec(state, declaration, previous.Item.Artifact), previous.Item.Artifact, state,
            rollback: true, actor: actor);
        return true;
    }

    /// <summary>
    /// Hands the host its next job. A host whose lease expired (it restarted)
    /// claims its own job again; every claim counts in <see cref="DeployJob.Claims"/>
    /// so the run log records each reclaim. Claims older than
    /// <see cref="HostLostAfter"/> fail as <c>host-lost</c> first.
    /// </summary>
    public DeployJob? Claim(string hostId, bool requireVerifiedRelease = false)
    {
        lock (_gate)
        {
            var productionHost = IsProductionHost(hostId);
            var root = StoreRoot();
            if (!Directory.Exists(root)) return null;
            var now = _clock.GetUtcNow().UtcDateTime;
            var states = new List<(string Project, State State)>();
            foreach (var file in Directory.EnumerateFiles(root, "*.json").Order(StringComparer.Ordinal))
            {
                var project = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file));
                var state = Load(project);
                if (AdvanceQueue(state)) Save(project, state);
                states.Add((project, state));
            }
            if (states.Any(item => item.State.Jobs.Any(job =>
                    job.HostId == hostId && job.Destination != RunTargetVocabulary.EdgeDestination
                    && job.Status == "claimed" && job.ClaimedAt > now - LeaseDuration)))
                return null;
            foreach (var (project, state) in states)
            {
                var retryableEnvironments = new HashSet<string>(StringComparer.Ordinal);
                foreach (var job in state.Jobs.Where(item => item.HostId == hostId &&
                    item.Destination != RunTargetVocabulary.EdgeDestination &&
                    (!productionHost || item.Kind is not ("preview" or "preview-remove")) &&
                    (item.Status == "queued" || item.Status == "claimed" && item.ClaimedAt <= now - LeaseDuration)).ToArray())
                {
                    if (retryableEnvironments.Contains(job.EnvironmentId)) continue;
                    // The queue filename is normalized; provenance keeps the
                    // project's original case. Never use the filename as its identity.
                    var verification = !string.Equals(job.Project, project, StringComparison.OrdinalIgnoreCase)
                        ? new ReleaseVerification(null)
                        : job.IsStop ? new ReleaseVerification(job)
                        : VerifyRelease(job.Project, job, state, requireVerifiedRelease);
                    if (verification.Retryable)
                    {
                        retryableEnvironments.Add(job.EnvironmentId);
                        continue;
                    }
                    if (verification.Job is not { } verified)
                    {
                        RefuseUnverified(state, job, now);
                        Save(project, state);
                        continue;
                    }
                    var reserved = false;
                    if (!verified.IsStop && verified.Kind != "preview-remove"
                        && !TryAdmitClaim(project, verified, out reserved)) continue;
                    var claimed = verified with
                    {
                        Status = "claimed", ClaimedAt = now, LeaseToken = Guid.NewGuid().ToString("N"),
                        Claims = job.Claims + 1, ReservedSlot = job.ReservedSlot || reserved,
                    };
                    state.Jobs[state.Jobs.IndexOf(job)] = claimed;
                    Save(project, state);
                    if (!claimed.IsStop) _quota?.RecordDeployStart(project, claimed.Id, claimed.EnvironmentId);
                    return claimed;
                }
            }
            return null;
        }
    }

    /// <summary>Releases reservations for removed environments and expired deploy leases.</summary>
    public void ReconcileHostingSlots()
    {
        if (_quota is null || !_quota.HasLimits()) return;
        lock (_gate)
        {
            var root = StoreRoot();
            if (!Directory.Exists(root)) return;
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var file in Directory.EnumerateFiles(root, "*.json").ToArray())
            {
                var project = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file));
                var state = Load(project);
                var declarations = Declarations(project);
                // Preview slots use the job lifecycle; removal reports release them explicitly.
                if (declarations is not null)
                    foreach (var environmentId in _quota.ProjectEnvironmentIds(project))
                        if (environmentId.StartsWith("env:", StringComparison.Ordinal)
                            && !environmentId.StartsWith("env:preview-", StringComparison.Ordinal)
                            && declarations.All(item => SlotKey(item.Id) != environmentId))
                            _quota.SetProjectEnvironment(project, environmentId, false);
                // A report settles its reservation (it clears ReservedSlot), so only a claim that
                // expired, was lost or was aborted without a report still holds one here.
                var expired = state.Jobs.Where(job => job.ReservedSlot && job.Status != "healthy"
                    && (job.Status != "claimed" || job.ClaimedAt <= now - LeaseDuration)).ToList();
                foreach (var job in expired)
                {
                    _quota.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), false);
                    _quota.RecordDeployEnd(project, job.Id);
                    state.Jobs[state.Jobs.IndexOf(job)] = job with { ReservedSlot = false };
                }
                if (expired.Count > 0) Save(project, state);
            }
        }
    }

    private void EnsurePreviewHostIsNotProduction(string hostId)
    {
        if (IsProductionHost(hostId))
            throw new InvalidOperationException($"Host {hostId} is assigned to production and cannot run preview jobs.");
    }

    private bool IsProductionHost(string hostId)
    {
        foreach (var project in _scanner.GetWatchPaths())
        {
            var files = DeclarationFiles(project.Name);
            if (files is null) continue;
            if (files.Any(file => file.Error is not null)) return true;
            if (files.Any(file => file.Declaration is { Kind: "production" } declaration
                && string.Equals(declaration.TargetHostId, hostId, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>A claimed job whose lease token matches and has not gone stale, for secret issuance.</summary>
    public DeployJob? FindClaimedJob(string project, string jobId, string leaseToken,
        bool requireVerifiedRelease = false)
        => FindClaimedJobVerification(project, jobId, leaseToken, requireVerifiedRelease).Job;

    public (DeployJob? Job, bool Retryable) FindClaimedJobVerification(string project, string jobId,
        string leaseToken, bool requireVerifiedRelease = false)
    {
        if (string.IsNullOrWhiteSpace(project) || project is "." or ".." || project.IndexOfAny(['/', '\\', '\0']) >= 0)
            return (null, false);
        lock (_gate)
        {
            var state = Load(project);
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId && item.Status == "claimed"
                && item.LeaseToken is not null && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(item.LeaseToken), System.Text.Encoding.UTF8.GetBytes(leaseToken)));
            if (job is null || job.ClaimedAt < _clock.GetUtcNow().UtcDateTime - LeaseDuration)
                return (null, false);
            var verification = job.IsStop ? new ReleaseVerification(job)
                : VerifyRelease(project, job, state, requireVerifiedRelease);
            var verified = verification.Job;
            if (verified is null) return (null, verification.Retryable);
            if (verified != job)
            {
                state.Jobs[state.Jobs.IndexOf(job)] = verified;
                Save(project, state);
            }
            return (verified, false);
        }
    }

    private readonly record struct ReleaseVerification(DeployJob? Job, bool Retryable = false);

    private ReleaseVerification VerifyRelease(string project, DeployJob job, State state, bool requireRelease)
    {
        if (job.Kind is "preview" or "preview-remove") return new(job);
        try
        {
            var releases = _deployStore.ReadReleaseRecords(project);
            if (releases.Count == 0 && job.ReleaseId is null && !requireRelease) return new(job);
            return new(DeployJobReleaseBinding.Verify(project, job, state.Jobs, releases,
                _deployStore.ReadDeployments(project), _registry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, Retryable: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new(null);
        }
    }

    private static void RefuseUnverified(State state, DeployJob job, DateTime now)
    {
        const string evidence = "artifact-unverified";
        var failed = job with { Status = "failed", LeaseToken = null, FinishedAt = now, Evidence = evidence };
        state.Jobs[state.Jobs.IndexOf(job)] = failed;
        state.History.Add(new(job.EnvironmentId, now,
            new(now, "failed", job.Artifact, job.PreviousArtifact, evidence)));
        ClosePromotionForResult(state, failed, success: false, now);
    }

    /// <summary>Extends a live claim without changing its fencing token.</summary>
    public bool RenewClaim(string project, string jobId, string leaseToken)
    {
        if (string.IsNullOrWhiteSpace(leaseToken)) return false;
        lock (_gate)
        {
            var job = FindClaimedJob(project, jobId, leaseToken);
            if (job is null) return false;
            var state = Load(project);
            var index = state.Jobs.FindIndex(item => item.Id == jobId && item.LeaseToken == leaseToken);
            if (index < 0) return false;
            state.Jobs[index] = state.Jobs[index] with { ClaimedAt = _clock.GetUtcNow().UtcDateTime };
            Save(project, state);
            return true;
        }
    }

    public DeployJob? PreviewJob(string project, string jobId)
    {
        lock (_gate)
            return Load(project).Jobs.FirstOrDefault(job => job.Id == jobId && job.Kind.StartsWith("preview", StringComparison.Ordinal))
                is { } job ? Sanitize(job) : null;
    }

    /// <summary>
    /// Queues a stop for the environment's host. The environment stays running and keeps its
    /// hosting slot until that host claims the stop job and reports it done (see <see cref="Report"/>).
    /// </summary>
    public bool Stop(string project, string environmentId, string actor)
    {
        lock (_gate)
        {
            var declaration = Declarations(project)?.FirstOrDefault(item => item.Id == environmentId);
            if (declaration is null) return false;
            var state = Load(project);
            // Settle abandoned deploy claims before queuing the stop. Otherwise an
            // expired claim can keep the host from ever claiming the stop job.
            var advanced = AdvanceQueue(state);
            var current = state.History.LastOrDefault(row => row.EnvironmentId == environmentId)?.Item;
            if (current is null || current.Status == "stopped")
            {
                if (advanced) Save(project, state);
                return false;
            }
            if (state.Jobs.Any(job => job.IsStop && job.EnvironmentId == environmentId
                    && job.Status is "queued" or "claimed"))
            {
                if (advanced) Save(project, state);
                return false;
            }
            var job = Queue(project, declaration, current.Artifact, state, actor: actor);
            state.Jobs[state.Jobs.IndexOf(job)] = job with { IsStop = true };
            Save(project, state);
            return true;
        }
    }

    /// <summary>
    /// Accepts the host's result while its lease token is valid. The token
    /// changes on a reclaim and is cleared when the claim fails as
    /// <c>host-lost</c> or is aborted, so only the current claim reports.
    /// </summary>
    public bool Report(string project, string jobId, DeployReportRequest report, bool requireVerifiedRelease = false)
    {
        lock (_gate)
        {
            var state = Load(project);
            var advanced = AdvanceQueue(state);
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId && item.Status == "claimed"
                && item.LeaseToken == report.LeaseToken);
            if (job is null)
            {
                if (advanced) Save(project, state);
                return false;
            }
            var verification = job.IsStop ? new ReleaseVerification(job)
                : VerifyRelease(project, job, state, requireVerifiedRelease);
            if (verification.Retryable)
            {
                if (advanced) Save(project, state);
                return false;
            }
            if (verification.Job is not { } verified)
            {
                RefuseUnverified(state, job, _clock.GetUtcNow().UtcDateTime);
                Save(project, state);
                return false;
            }
            if (verified != job)
            {
                state.Jobs[state.Jobs.IndexOf(job)] = verified;
                job = verified;
            }
            var now = _clock.GetUtcNow().UtcDateTime;
            if (job.ClaimedAt < now - LeaseDuration) return false;
            if (job.IsStop)
            {
                ReportStop(project, state, job, report.Healthy, now);
                Save(project, state);
                return true;
            }
            var expectedSteps = job.SshSteps.Select((step, index) => $"deploy[{index}]:{step.Step}").ToArray();
            var reportedSteps = report.Steps ?? [];
            if (reportedSteps.Count > expectedSteps.Length || reportedSteps.Where((step, index) =>
                    step.Name != expectedSteps[index]).Any() || reportedSteps.SkipLast(1).Any(step => !step.Succeeded)
                || report.Healthy && (reportedSteps.Count != expectedSteps.Length
                    || reportedSteps.Any(step => !step.Succeeded)))
                return false;
            var success = report.Healthy;
            string? screenshotUrl = null;
            if (success && job.Kind == "preview")
            {
                if (!TrySavePreviewScreenshot(project, job.CardKey!, job.SourceCommit!, report.ScreenshotBase64, out screenshotUrl))
                    return false;
            }
            if (success && job.Kind == "preview-remove")
                DeletePreviewScreenshot(project, job.CardKey!);
            // Host output is deliberately not accepted as an audit string. Keep a bounded status code only.
            var evidence = report.Evidence is "health-http-failed" or "deploy-step-failed" or "health-ok"
                ? report.Evidence : success ? "health-ok" : "deploy-step-failed";
            // The report settles the reservation: a healthy deploy keeps its slot as a running
            // environment, a failed one returns it below. The reconciler only releases unsettled claims.
            state.Jobs[state.Jobs.IndexOf(job)] = job with
            {
                Status = success ? "healthy" : "failed", LeaseToken = null, FinishedAt = now, Evidence = evidence,
                ReservedSlot = false, StepResults = reportedSteps, ScreenshotUrl = screenshotUrl,
            };
            var status = success ? job.IsRollback ? "rolled-back" : "healthy" : "failed";
            state.History.Add(new(job.EnvironmentId, now,
                new(now, status, job.Artifact, job.PreviousArtifact, evidence)));
            _quota?.RecordDeployEnd(project, job.Id);
            if (success) _quota?.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), job.Kind != "preview-remove");
            else if (job.ReservedSlot) _quota?.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), false);
            if (success || !job.GuidedFailure) ClosePromotionForResult(state, job, success, now);
            // A job already queued for the environment replaces the failed
            // one; a rollback queued behind it would replace a newer release.
            if (!success && job.Kind == "standard" && !job.GuidedFailure && !job.IsRollback
                && job.PreviousArtifact is not null
                && !state.Jobs.Any(item => item.EnvironmentId == job.EnvironmentId
                    && item.Tenant == job.Tenant && item.Status == "queued"))
            {
                var declaration = Declarations(project)?.FirstOrDefault(item => item.Id == job.EnvironmentId);
                var previous = state.Jobs.LastOrDefault(item => item.EnvironmentId == job.EnvironmentId
                    && item.Tenant == job.Tenant && item.Artifact == job.PreviousArtifact && item.Status == "healthy");
                if (declaration is not null)
                {
                    var rollbackJob = Queue(project, DeployedSpec(state, declaration, job.PreviousArtifact, job.Tenant),
                        job.PreviousArtifact, state, rollback: true, actor: "automatic-rollback", tenant: job.Tenant,
                        releaseId: previous?.ReleaseId, rollbackOfJobId: job.Id);
                    if (previous is not null)
                        state.Jobs[^1] = rollbackJob with { Variables = [], ResolvedVariables = previous.ResolvedVariables,
                            SecretVariableRefs = previous.SecretVariableRefs, SecretRefs = previous.SecretRefs };
                }
            }
            Save(project, state);
            return true;
        }
    }

    /// <summary>
    /// The host acknowledged a stop job: only a confirmed stop marks the environment stopped and
    /// releases its hosting slot. A failed stop leaves the environment running and counted.
    /// </summary>
    private void ReportStop(string project, State state, DeployJob job, bool stopped, DateTime now)
    {
        state.Jobs[state.Jobs.IndexOf(job)] = job with
        {
            Status = stopped ? "stopped" : "failed", LeaseToken = null, FinishedAt = now,
            Evidence = stopped ? "stop-ok" : "stop-failed",
        };
        _quota?.RecordDeployEnd(project, job.Id);
        if (!stopped) return;
        state.History.Add(new(job.EnvironmentId, now, new(now, "stopped", job.Artifact, job.Artifact,
            "stopped-by:" + job.RequestedBy)));
        _quota?.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), false);
    }

    private void Complete(string project, State state, DeployJob job, bool success, string? reportedEvidence,
        Func<IReadOnlyList<EnvironmentDeclaration>?> declarations)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var evidence = reportedEvidence is "health-http-failed" or "deploy-step-failed" or "health-ok"
            ? reportedEvidence : success ? "health-ok" : "deploy-step-failed";
        state.Jobs[state.Jobs.IndexOf(job)] = job with
        { Status = success ? "healthy" : "failed", LeaseToken = null, FinishedAt = now, Evidence = evidence,
            ReservedSlot = false };
        var status = success ? job.IsRollback ? "rolled-back" : "healthy" : "failed";
        state.History.Add(new(job.EnvironmentId, now,
            new(now, status, job.Artifact, job.PreviousArtifact, evidence)));
        _quota?.RecordDeployEnd(project, job.Id);
        if (success) _quota?.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), true);
        else if (job.ReservedSlot) _quota?.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), false);
        if (success || !job.GuidedFailure) ClosePromotionForResult(state, job, success, now);
        if (!success && !job.GuidedFailure && !job.IsRollback && job.PreviousArtifact is not null
            && !state.Jobs.Any(item => item.EnvironmentId == job.EnvironmentId && item.Tenant == job.Tenant
                && item.Status == "queued"))
        {
            var declaration = declarations()?.FirstOrDefault(item => item.Id == job.EnvironmentId);
            if (declaration is not null)
            {
                var previous = state.Jobs.LastOrDefault(item => item.EnvironmentId == job.EnvironmentId
                    && item.Tenant == job.Tenant && item.Artifact == job.PreviousArtifact && item.Status == "healthy");
                var rollbackJob = Queue(project, DeployedSpec(state, declaration, job.PreviousArtifact, job.Tenant),
                    job.PreviousArtifact, state, rollback: true, actor: "automatic-rollback", tenant: job.Tenant,
                    releaseId: previous?.ReleaseId, rollbackOfJobId: job.Id);
                if (previous is not null)
                    state.Jobs[^1] = rollbackJob with { Variables = [], ResolvedVariables = previous.ResolvedVariables,
                        SecretVariableRefs = previous.SecretVariableRefs, SecretRefs = previous.SecretRefs };
            }
        }
    }

    private static void ClosePromotionForResult(State state, DeployJob job, bool success, DateTime now)
    {
        if (job.Tenant is not null) return;
        var promotion = state.Promotions.LastOrDefault(item => item.Artifact == job.Artifact);
        if (promotion is null) return;
        var index = state.Promotions.IndexOf(promotion);
        if (promotion.StageId == job.EnvironmentId && promotion.Status == "stage-queued")
            state.Promotions[index] = promotion with
            { Status = success ? "pending" : "failed", StageHealthyAt = now,
                ExpiresAt = success ? now.AddHours(24) : null };
        if (promotion.ProductionId == job.EnvironmentId && promotion.Status == "production-queued")
            state.Promotions[index] = promotion with { Status = success ? "deployed" : "failed" };
    }

    /// <summary>
    /// Expires pending promotions and fails claims whose host stopped
    /// reporting. Production always waits for a person: the
    /// <c>stage-healthy-10m</c> rule never queues it (BD-1). A lost host gets
    /// no automatic rollback, because the rollback would go to the same host.
    /// </summary>
    private bool AdvanceQueue(State state)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var changed = false;
        foreach (var promotion in state.Promotions.Where(item => item.Status == "pending" && item.ExpiresAt <= now).ToArray())
        {
            state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with { Status = "expired" };
            changed = true;
        }
        foreach (var job in state.Jobs.Where(item => item.Status == "claimed" && item.ClaimedAt <= now - HostLostAfter).ToArray())
        {
            var lost = job with { Status = "failed", LeaseToken = null, FinishedAt = now, Evidence = HostLostEvidence };
            state.Jobs[state.Jobs.IndexOf(job)] = lost;
            changed = true;
            // A lost stop leaves the environment as it was: running and counted.
            if (job.IsStop) continue;
            state.History.Add(new(job.EnvironmentId, now,
                new(now, "failed", job.Artifact, job.PreviousArtifact, HostLostEvidence)));
            if (!job.GuidedFailure) ClosePromotionForResult(state, lost, success: false, now);
        }
        return changed;
    }

    /// <summary>
    /// The declaration the artifact last deployed healthy with, so a rollback
    /// repeats exactly that deploy, on the host the environment declares now
    /// (a retired host would never claim it). History from before jobs carried
    /// the declaration falls back to the current one.
    /// </summary>
    private static EnvironmentDeclaration DeployedSpec(State state, EnvironmentDeclaration current, string artifact,
        string? tenant = null)
        => state.Jobs.LastOrDefault(job => job.EnvironmentId == current.Id && job.Artifact == artifact
                && job.Tenant == tenant && job.Status == "healthy") is { } deployed
            ? current with
            {
                Template = deployed.Template, Destination = deployed.Destination,
                HealthUrl = deployed.HealthUrl, SecretRefs = deployed.SecretRefs, ContainerPort = deployed.ContainerPort,
                Hosting = deployed.Hosting ?? current.Hosting,
                SshSteps = deployed.SshSteps, Variables = deployed.Variables, Artifacts = deployed.Artifacts,
            }
            : current;

    /// <summary>Resume the usual rollback after a guided failure is aborted or times out.</summary>
    public bool RollbackFailedJob(string project, string jobId, bool rollback = true)
    {
        lock (_gate)
        {
            var state = Load(project);
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId && item.Status == "failed");
            if (job is null) return false;
            if (job.GuidedResolved) return true;
            ClosePromotionForResult(state, job, success: false, now: _clock.GetUtcNow().UtcDateTime);
            var newerJob = state.Jobs.Skip(state.Jobs.IndexOf(job) + 1)
                .Any(item => item.EnvironmentId == job.EnvironmentId && item.Tenant == job.Tenant);
            if (rollback && !newerJob && job.Evidence != HostLostEvidence && job.PreviousArtifact is not null && !job.IsRollback
                && !state.Jobs.Any(item => item.EnvironmentId == job.EnvironmentId && item.Tenant == job.Tenant
                    && item.Status is "queued" or "claimed"))
            {
                var declaration = Declarations(project)?.FirstOrDefault(item => item.Id == job.EnvironmentId);
                if (declaration is not null)
                {
                    var previous = state.Jobs.LastOrDefault(item => item.EnvironmentId == job.EnvironmentId
                        && item.Tenant == job.Tenant && item.Artifact == job.PreviousArtifact && item.Status == "healthy");
                    var rollbackJob = Queue(project, DeployedSpec(state, declaration, job.PreviousArtifact, job.Tenant),
                        job.PreviousArtifact, state, rollback: true, actor: "automatic-rollback", tenant: job.Tenant,
                        releaseId: previous?.ReleaseId, rollbackOfJobId: job.Id);
                    if (previous is not null)
                        state.Jobs[^1] = rollbackJob with { Variables = [], ResolvedVariables = previous.ResolvedVariables,
                            SecretVariableRefs = previous.SecretVariableRefs, SecretRefs = previous.SecretRefs };
                }
            }
            state.Jobs[state.Jobs.IndexOf(job)] = job with { GuidedResolved = true };
            Save(project, state);
            return true;
        }
    }

    /// <summary>
    /// BD-1: the release snapshot decides what is deployed (template,
    /// destination, health check, secrets, SSH targets); the current declaration
    /// decides which host claims the job. After a host move, an older schema 2
    /// release retains its target so the executor refuses to run it on the new
    /// host. Legacy jobs have no SSH target and can use the new host.
    /// </summary>
    private static EnvironmentDeclaration RouteToCurrentHost(EnvironmentDeclaration snapshot,
        IReadOnlyList<EnvironmentDeclaration> current)
        => current.FirstOrDefault(item => item.Id == snapshot.Id) is { } declared
            ? snapshot with
            {
                TargetHostId = declared.TargetHostId,
            }
            : snapshot;

    private static EnvironmentDeclaration RouteTenant(EnvironmentDeclaration declaration, string tenant,
        TenantDeployTarget target)
        => declaration with
        {
            Destination = target.Destination,
            HealthUrl = target.HealthUrl,
            SshSteps = declaration.SshSteps.Select(step => step.Step switch
            {
                "static-site" => step with { Directory = step.Directory!.TrimEnd('/') + "/tenants/" + tenant,
                    HealthUrl = target.HealthUrl },
                "container" => step with { ComposeFile = step.ComposeFile![..step.ComposeFile.LastIndexOf('/')]
                    + "/tenants/" + tenant + "/" + step.ComposeFile[(step.ComposeFile.LastIndexOf('/') + 1)..],
                    HealthUrl = target.HealthUrl },
                _ => step,
            }).ToArray(),
        };

    private static bool HasOpenPromotion(State state, string artifact)
        => state.Promotions.Any(item => item.Artifact == artifact
            && item.Status is not ("rejected" or "failed" or "cancelled"));

    private static bool IsDeclared(IReadOnlyList<EnvironmentDeclaration>? declarations, string environmentId)
        => declarations?.Any(item => item.Id == environmentId) == true;

    private DeployJob Queue(string project, EnvironmentDeclaration declaration, string artifact, State state,
        bool rollback = false, string? actor = null, string? tenant = null, string? releaseId = null,
        string? rollbackOfJobId = null, string? retryStep = null, string? retryOfJobId = null)
    {
        var previous = state.Jobs.LastOrDefault(job => job.EnvironmentId == declaration.Id && job.Tenant == tenant
            && job.Status == "healthy")?.Artifact;
        if (tenant is null && previous is null && !state.Jobs.Any(job => job.EnvironmentId == declaration.Id
                && job.Tenant is not null))
            previous = state.History.Where(row => row.EnvironmentId == declaration.Id
                && (row.Item.Status is "healthy" or "rolled-back")).LastOrDefault()?.Item.Artifact;
        var job = new DeployJob(Guid.NewGuid().ToString("N"), project, declaration.Id,
            declaration.TargetHostId, artifact, declaration.Template, declaration.Destination,
            declaration.HealthUrl, declaration.SecretRefs, declaration.ContainerPort, previous, "queued",
            _clock.GetUtcNow().UtcDateTime,
            IsRollback: rollback, RequestedBy: actor, ReleaseId: releaseId, RollbackOfJobId: rollbackOfJobId,
            GuidedFailure: declaration.GuidedFailure, RetryStep: retryStep, RetryOfJobId: retryOfJobId,
            Hosting: declaration.Hosting)
        { Tenant = tenant, SshSteps = declaration.SshSteps, Variables = declaration.Variables, Artifacts = declaration.Artifacts };
        state.Jobs.Add(job);
        return job;
    }

    public string? PreviewScreenshotPath(string project, string cardKey)
    {
        if (!Identifier.IsMatch(cardKey.ToLowerInvariant())) return null;
        var path = Path.Combine(StoreRoot(), "preview-screenshots", SafeProjectName(project),
            cardKey.ToLowerInvariant() + ".png");
        return File.Exists(path) ? path : null;
    }

    private bool TrySavePreviewScreenshot(string project, string cardKey, string sourceCommit,
        string? encoded, out string? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 3_000_000) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { return false; }
        if (bytes.Length is < 8 or > 2_000_000 || !bytes.AsSpan(0, 8).SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        try
        {
            var image = SixLabors.ImageSharp.Image.Identify(bytes);
            if (image is null || image.Width is < 1 or > 4096 || image.Height is < 1 or > 4096)
                return false;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ProjectEnvironmentService: invalid preview screenshot");
            return false;
        }
        var directory = Path.Combine(StoreRoot(), "preview-screenshots", SafeProjectName(project));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, cardKey.ToLowerInvariant() + ".png");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
        url = $"/api/projects/{Uri.EscapeDataString(project)}/previews/{Uri.EscapeDataString(cardKey)}/screenshot"
            + $"?delivery={Uri.EscapeDataString(sourceCommit)}";
        return true;
    }

    private void DeletePreviewScreenshot(string project, string cardKey)
    {
        var path = PreviewScreenshotPath(project, cardKey);
        if (path is not null) File.Delete(path);
    }

    private string StoreRoot() => Path.Combine(_configuration["TaskRepository"] ??
        throw new InvalidOperationException("TaskRepository is required."), "deployments");
    private string FilePath(string project)
    {
        return Path.Combine(StoreRoot(), SafeProjectName(project) + ".json");
    }
    internal static string SafeProjectName(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || project is "." or ".." ||
            project.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Invalid project.", nameof(project));
        return Uri.EscapeDataString(project.ToLowerInvariant());
    }
    private State Load(string project) => File.Exists(FilePath(project))
        ? JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath(project)), _json) ?? new State() : new State();
    private void Save(string project, State state)
    {
        Directory.CreateDirectory(StoreRoot());
        var path = FilePath(project);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, _json));
        File.Move(temporary, path, true);
    }
    private static DeployJob Sanitize(DeployJob job) => job with { LeaseToken = null };
    public sealed class State
    {
        public List<DeployJob> Jobs { get; set; } = [];
        public List<EnvironmentPromotion> Promotions { get; set; } = [];
        public List<EnvironmentHistoryRow> History { get; set; } = [];
        public List<RunTargetObservedRow> Observed { get; set; } = [];
        public List<RunTargetPlacement> Placements { get; set; } = [];
        public List<RunTargetRemovalRecord> RunTargetRemovals { get; set; } = [];
    }
    public sealed record EnvironmentHistoryRow(string EnvironmentId, DateTime At, DeploymentHistoryItem Item);
    public sealed record RunTargetObservedRow(string EnvironmentId, RunTargetObservation Observation);
}
