using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace AgentStudio.Projects;

public sealed record EnvironmentDeclaration(
    string Id, string Name, string Kind, string TargetHostId, string Address,
    string Template, string Destination, string? HealthUrl, string Approval,
    IReadOnlyList<string> SecretRefs, int ContainerPort,
    IReadOnlyList<HealthCheckDefinition>? HealthChecks = null, bool HealthChecksAllowPrivateNetwork = false);

/// <summary>One declaration file: the parsed environment, or the error that makes it unusable.</summary>
public sealed record EnvironmentDeclarationFile(string EnvironmentId, EnvironmentDeclaration? Declaration, string? Error);

public sealed record DeploymentHistoryItem(DateTime At, string Status, string Artifact, string? PreviousArtifact,
    string? Evidence);

public sealed record ProjectEnvironmentView(EnvironmentDeclaration Declaration, string? DeployedArtifact,
    string Health, string? PendingArtifact, IReadOnlyList<DeploymentHistoryItem> History);

public sealed record DeployJob(string Id, string Project, string EnvironmentId, string HostId,
    string Artifact, string Template, string Destination, string? HealthUrl,
    IReadOnlyList<string> SecretRefs, int ContainerPort, string? PreviousArtifact, string Status,
    DateTime CreatedAt, DateTime? ClaimedAt = null, string? LeaseToken = null, bool IsRollback = false,
    string? RequestedBy = null, DateTime? FinishedAt = null, string? Evidence = null, int Claims = 0,
    bool ReservedSlot = false, bool IsStop = false);

public sealed record EnvironmentPromotion(string Artifact, string StageId, string ProductionId,
    DateTime StageHealthyAt, string Status, string? Actor = null, DateTime? ExpiresAt = null);

public sealed record DeploymentFailureCard(string EnvironmentId, string Artifact, DateTime At,
    string Evidence, string? RollbackArtifact);

public sealed record ProjectEnvironmentsResponse(string Project, IReadOnlyList<ProjectEnvironmentView> Environments,
    IReadOnlyList<EnvironmentPromotion> Promotions, IReadOnlyList<DeployJob> Jobs,
    IReadOnlyList<DeploymentFailureCard> FailureCards);

public sealed record DeployReportRequest(string LeaseToken, bool Healthy, string? Evidence);
public sealed record DeployClaimRequest(string HostId);
public sealed record ReleaseArtifactRequest(string Artifact, string Commit);
public sealed record PromotionDecisionRequest(string Artifact);
public sealed record GateArtifactRequest(string Project, string Artifact, string Commit);

/// <summary>A deploy action refused by workspace or project quota admission.</summary>
public sealed class DeployQuotaRefusedException(string message) : InvalidOperationException(message);

/// <summary>Repository declarations and a separate, durable host-deploy queue.</summary>
public sealed class ProjectEnvironmentService
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
    /// <summary>
    /// Stop jobs carry this template prefix. A host without stop support
    /// refuses the unknown template, so the environment keeps its slot.
    /// </summary>
    public const string StopTemplatePrefix = "stop:";
    private readonly object _gate = new();
    private readonly IConfiguration _configuration;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly AgentStudio.Quota.WorkspaceQuotaService? _quota;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ProjectEnvironmentService(IConfiguration configuration, TaskScannerService scanner,
        GitService git, ProjectSettingsService settings, TimeProvider clock,
        AgentStudio.Quota.WorkspaceQuotaService? quota = null)
    {
        _configuration = configuration;
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
        if (root is null) return [];
        var directory = Path.Combine(root, "docs", "deployments", "environments");
        if (!Directory.Exists(directory)) return [];
        return ParseDeclarationFiles(Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => (Path.GetFileName(file), File.ReadAllText(file))));
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
        return parsed.Select(item => item.Declaration!).ToArray();
    }

    private bool IsWatched(string project)
        => _scanner.GetWatchPaths().Any(item => string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<EnvironmentDeclarationFile> ParseDeclarationFiles(IEnumerable<(string Name, string Json)> files)
    {
        var result = new List<EnvironmentDeclarationFile>();
        foreach (var (file, json) in files)
        {
            try
            {
                var declaration = ParseDeclaration(file, json);
                result.Add(new(declaration.Id, declaration, null));
            }
            catch (JsonException ex)
            {
                result.Add(new(Path.GetFileNameWithoutExtension(file), null, $"{file}: {ex.Message}"));
            }
        }
        var duplicates = result.Where(item => item.Declaration is not null).GroupBy(item => item.EnvironmentId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return result.Select(item => item.Declaration is not null && duplicates.Contains(item.EnvironmentId)
                ? item with { Declaration = null, Error = "Environment ids must be unique within a project." } : item)
            .OrderBy(item => item.Declaration?.Kind switch { null => 3, "stage" => 0, "production" => 1, _ => 2 })
            .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal).ToArray();
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
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
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
        if (!Identifier.IsMatch(id) || !Identifier.IsMatch(host) || kind is not ("stage" or "production" or "other")
            || template is not ("container" or "static") || secrets.Any(item => !SecretIdentifier.IsMatch(item)))
            throw new JsonException($"Environment {Path.GetFileName(file)} has invalid identifiers, kind, template or secret refs.");
        var destination = Required("destination");
        if (template == "static" && (!Path.IsPathFullyQualified(destination) || destination.Contains("..", StringComparison.Ordinal)))
            throw new JsonException("Static destination must be an absolute path without traversal.");
        if (template == "container" && (!int.TryParse(destination, out var port) || port is < 1 or > 65535))
            throw new JsonException("Container destination must be a local proxy port.");
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
        return new(id, Required("name"), kind, host, Required("address"), template,
            destination, healthUrl, approval, secrets, containerPort,
            HealthCheckDeclarations.Parse(value, Path.GetFileName(file)),
            HealthCheckDeclarations.AllowsPrivateNetwork(value, Path.GetFileName(file)));
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
                var pending = state.Jobs.LastOrDefault(job => job.EnvironmentId == item.Id
                    && (job.Status is "queued" or "claimed"));
                return new ProjectEnvironmentView(item, latest?.Artifact,
                    latest is null ? "unknown" : latest.Status is "failed" or "stopped" ? latest.Status : "healthy",
                    pending?.Artifact, history);
            }).ToArray(), state.Promotions.ToArray(), state.Jobs.Select(Sanitize).ToArray(),
                state.History.Where(row => row.Item.Status == "failed").AsEnumerable().Reverse()
                    .Take(20).Select(row => new DeploymentFailureCard(row.EnvironmentId,
                        row.Item.Artifact, row.At, row.Item.Evidence ?? "deploy-step-failed",
                        row.Item.PreviousArtifact)).ToArray());
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
        IReadOnlyList<EnvironmentDeclaration>? snapshot = null)
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
            var state = Load(project);
            if (HasOpenPromotion(state, artifact)) return null;
            RequireQuota(project, stage.Id);
            state.Promotions.Add(new(artifact, stage.Id, production.Id, DateTime.MinValue, "stage-queued"));
            var job = Queue(project, RouteToCurrentHost(stage, current!), artifact, state, actor: "release-gate");
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
        bool rollback)
    {
        if (!Digest.IsMatch(artifact)) return null;
        lock (_gate)
        {
            var current = Declarations(project);
            if (!IsDeclared(current, target.Id)) return null;
            var state = Load(project);
            RequireQuota(project, target.Id);
            var job = Queue(project, RouteToCurrentHost(target, current!), artifact, state, rollback, actor);
            Save(project, state);
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
            var promotion = job.IsStop ? null : state.Promotions.LastOrDefault(item => item.Artifact == job.Artifact
                && (item.Status == "stage-queued" && item.StageId == job.EnvironmentId
                    || item.Status == "production-queued" && item.ProductionId == job.EnvironmentId));
            if (promotion is not null)
                state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with { Status = "cancelled", Actor = actor };
            Save(project, state);
            return true;
        }
    }

    // The gate endpoint is service-credential only. It records the built
    // artifact's immutable digest against the exact released commit.
    public bool RegisterGateArtifact(string project, string artifact, string commit)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private bool HasBuiltArtifact(string project, string artifact, string commit)
    {
        var path = ArtifactPath(project, commit, artifact);
        return File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), artifact, StringComparison.Ordinal);
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
        EnvironmentDeclaration? target = null)
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
            if (approve) RequireQuota(project, production.Id);
            state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with
            { Status = approve ? "production-queued" : "rejected", Actor = actor };
            var job = approve
                ? Queue(project, target is null ? production : RouteToCurrentHost(target, declarations), artifact, state,
                    actor: actor)
                : null;
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

    /// <summary>
    /// Hands the host its next job. A host whose lease expired (it restarted)
    /// claims its own job again; every claim counts in <see cref="DeployJob.Claims"/>
    /// so the run log records each reclaim. Claims older than
    /// <see cref="HostLostAfter"/> fail as <c>host-lost</c> first.
    /// </summary>
    public DeployJob? Claim(string hostId)
    {
        lock (_gate)
        {
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
                    job.HostId == hostId && job.Status == "claimed" && job.ClaimedAt > now - LeaseDuration)))
                return null;
            foreach (var (project, state) in states)
            {
                var job = state.Jobs.FirstOrDefault(item => item.HostId == hostId &&
                    (item.Status == "queued" || item.Status == "claimed" && item.ClaimedAt <= now - LeaseDuration));
                if (job is null) continue;
                var reserved = false;
                if (!job.IsStop && !TryAdmitClaim(project, job, out reserved)) continue;
                var claimed = job with
                {
                    Status = "claimed", ClaimedAt = now, LeaseToken = Guid.NewGuid().ToString("N"),
                    Claims = job.Claims + 1, ReservedSlot = job.ReservedSlot || reserved,
                };
                state.Jobs[state.Jobs.IndexOf(job)] = claimed;
                Save(project, state);
                _quota?.RecordDeployStart(project, claimed.Id, claimed.EnvironmentId);
                return claimed;
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
                var declarations = Declarations(project);
                if (declarations is not null)
                    foreach (var environmentId in _quota.ProjectEnvironmentIds(project))
                        if (environmentId.StartsWith("env:", StringComparison.Ordinal)
                            && declarations.All(item => SlotKey(item.Id) != environmentId))
                            _quota.SetProjectEnvironment(project, environmentId, false);
                var state = Load(project);
                // A report settles the reservation (it clears ReservedSlot), so a healthy
                // deployment keeps its slot; only unsettled claims give it back.
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

    /// <summary>A claimed job whose lease token matches and has not gone stale, for secret issuance.</summary>
    public DeployJob? FindClaimedJob(string project, string jobId, string leaseToken)
    {
        if (string.IsNullOrWhiteSpace(project) || project is "." or ".." || project.IndexOfAny(['/', '\\', '\0']) >= 0)
            return null;
        lock (_gate)
        {
            var job = Load(project).Jobs.FirstOrDefault(item => item.Id == jobId && item.Status == "claimed"
                && item.LeaseToken is not null && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(item.LeaseToken), System.Text.Encoding.UTF8.GetBytes(leaseToken)));
            return job is null || job.ClaimedAt < _clock.GetUtcNow().UtcDateTime - LeaseDuration ? null : job;
        }
    }

    /// <summary>
    /// Queues a stop for the environment's host. The environment stays running
    /// and keeps its hosting slot until that host claims the stop job and
    /// reports it done (<see cref="Report"/>).
    /// </summary>
    public DeployJob? Stop(string project, string environmentId, string actor)
    {
        lock (_gate)
        {
            var declarations = Declarations(project);
            var declaration = declarations?.FirstOrDefault(item => item.Id == environmentId);
            if (declaration is null) return null;
            var state = Load(project);
            var current = state.History.LastOrDefault(row => row.EnvironmentId == environmentId)?.Item;
            if (current is null || current.Status == "stopped"
                || state.Jobs.Any(job => job.IsStop && job.EnvironmentId == environmentId
                    && job.Status is "queued" or "claimed")) return null;
            var spec = RouteToCurrentHost(DeployedSpec(state, declaration, current.Artifact), declarations!);
            var job = Queue(project, spec with { Template = StopTemplatePrefix + spec.Template }, current.Artifact,
                state, actor: actor) with { IsStop = true };
            state.Jobs[^1] = job;
            Save(project, state);
            return job;
        }
    }

    /// <summary>The host's stop result: only a confirmed stop ends the environment and frees its slot.</summary>
    private void SettleStop(string project, State state, DeployJob job, bool stopped, DateTime now)
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

    /// <summary>
    /// Accepts the host's result while its lease token is valid. The token
    /// changes on a reclaim and is cleared when the claim fails as
    /// <c>host-lost</c> or is aborted, so only the current claim reports.
    /// </summary>
    public bool Report(string project, string jobId, DeployReportRequest report)
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
            var now = _clock.GetUtcNow().UtcDateTime;
            var success = report.Healthy;
            if (job.IsStop)
            {
                SettleStop(project, state, job, success, now);
                Save(project, state);
                return true;
            }
            // Host output is deliberately not accepted as an audit string. Keep a bounded status code only.
            var evidence = report.Evidence is "health-http-failed" or "deploy-step-failed" or "health-ok"
                ? report.Evidence : success ? "health-ok" : "deploy-step-failed";
            // The report settles the claim's reservation: success keeps the slot as running, failure frees it.
            state.Jobs[state.Jobs.IndexOf(job)] = job with
            {
                Status = success ? "healthy" : "failed", LeaseToken = null, FinishedAt = now, Evidence = evidence,
                ReservedSlot = false,
            };
            var status = success ? job.IsRollback ? "rolled-back" : "healthy" : "failed";
            state.History.Add(new(job.EnvironmentId, now,
                new(now, status, job.Artifact, job.PreviousArtifact, evidence)));
            _quota?.RecordDeployEnd(project, job.Id);
            if (success) _quota?.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), true);
            else if (job.ReservedSlot) _quota?.SetProjectEnvironment(project, SlotKey(job.EnvironmentId), false);
            ClosePromotionForResult(state, job, success, now);
            // A job already queued for the environment replaces the failed
            // one; a rollback queued behind it would replace a newer release.
            if (!success && !job.IsRollback && job.PreviousArtifact is not null
                && !state.Jobs.Any(item => item.EnvironmentId == job.EnvironmentId && item.Status == "queued"))
            {
                var declaration = Declarations(project)?.FirstOrDefault(item => item.Id == job.EnvironmentId);
                if (declaration is not null) Queue(project, DeployedSpec(state, declaration, job.PreviousArtifact),
                    job.PreviousArtifact, state, rollback: true, actor: "automatic-rollback");
            }
            Save(project, state);
            return true;
        }
    }

    private static void ClosePromotionForResult(State state, DeployJob job, bool success, DateTime now)
    {
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
            // An unconfirmed stop leaves the environment as it was.
            if (job.IsStop) continue;
            state.History.Add(new(job.EnvironmentId, now,
                new(now, "failed", job.Artifact, job.PreviousArtifact, HostLostEvidence)));
            ClosePromotionForResult(state, lost, success: false, now);
        }
        return changed;
    }

    /// <summary>
    /// The declaration the artifact last deployed healthy with, so a rollback
    /// repeats exactly that deploy, on the host the environment declares now
    /// (a retired host would never claim it). History from before jobs carried
    /// the declaration falls back to the current one.
    /// </summary>
    private static EnvironmentDeclaration DeployedSpec(State state, EnvironmentDeclaration current, string artifact)
        => state.Jobs.LastOrDefault(job => job.EnvironmentId == current.Id && job.Artifact == artifact
                && job.Status == "healthy") is { } deployed
            ? current with
            {
                Template = deployed.Template, Destination = deployed.Destination,
                HealthUrl = deployed.HealthUrl, SecretRefs = deployed.SecretRefs, ContainerPort = deployed.ContainerPort,
            }
            : current;

    /// <summary>
    /// BD-1: the release snapshot decides what is deployed (template,
    /// destination, health check, secrets); the current declaration decides
    /// where. After an environment moves to another host, a rollback or a late
    /// approval of an older release goes to the new host instead of waiting
    /// for a retired host that never claims.
    /// </summary>
    private static EnvironmentDeclaration RouteToCurrentHost(EnvironmentDeclaration snapshot,
        IReadOnlyList<EnvironmentDeclaration> current)
        => current.FirstOrDefault(item => item.Id == snapshot.Id) is { } declared
            ? snapshot with { TargetHostId = declared.TargetHostId }
            : snapshot;

    private static bool HasOpenPromotion(State state, string artifact)
        => state.Promotions.Any(item => item.Artifact == artifact
            && item.Status is not ("rejected" or "failed" or "cancelled"));

    private static bool IsDeclared(IReadOnlyList<EnvironmentDeclaration>? declarations, string environmentId)
        => declarations?.Any(item => item.Id == environmentId) == true;

    private DeployJob Queue(string project, EnvironmentDeclaration declaration, string artifact, State state,
        bool rollback = false, string? actor = null)
    {
        var previous = state.History.Where(row => row.EnvironmentId == declaration.Id
            && (row.Item.Status is "healthy" or "rolled-back"))
            .LastOrDefault()?.Item.Artifact;
        var job = new DeployJob(Guid.NewGuid().ToString("N"), project, declaration.Id,
            declaration.TargetHostId, artifact, declaration.Template, declaration.Destination,
            declaration.HealthUrl, declaration.SecretRefs, declaration.ContainerPort, previous, "queued",
            _clock.GetUtcNow().UtcDateTime,
            IsRollback: rollback, RequestedBy: actor);
        state.Jobs.Add(job);
        return job;
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
    }
    public sealed record EnvironmentHistoryRow(string EnvironmentId, DateTime At, DeploymentHistoryItem Item);
}
