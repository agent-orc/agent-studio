using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;

namespace AgentStudio.Projects;

public sealed record EnvironmentDeclaration(
    string Id, string Name, string Kind, string TargetHostId, string Address,
    string Template, string Destination, string? HealthUrl, string Approval,
    IReadOnlyList<string> SecretRefs, int ContainerPort);

public sealed record DeploymentHistoryItem(DateTime At, string Status, string Artifact, string? PreviousArtifact,
    string? Evidence);

public sealed record ProjectEnvironmentView(EnvironmentDeclaration Declaration, string? DeployedArtifact,
    string Health, string? PendingArtifact, IReadOnlyList<DeploymentHistoryItem> History);

public sealed record DeployJob(string Id, string Project, string EnvironmentId, string HostId,
    string Artifact, string Template, string Destination, string? HealthUrl,
    IReadOnlyList<string> SecretRefs, int ContainerPort, string? PreviousArtifact, string Status,
    DateTime CreatedAt, DateTime? ClaimedAt = null, string? LeaseToken = null, bool IsRollback = false,
    string? RequestedBy = null);

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

/// <summary>Repository declarations and a separate, durable host-deploy queue.</summary>
public sealed class ProjectEnvironmentService
{
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex SecretIdentifier = new("^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex Commit = new("^[0-9a-f]{40}$", RegexOptions.Compiled);
    private static readonly Regex Digest = new("^[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}$", RegexOptions.Compiled);
    private readonly object _gate = new();
    private readonly IConfiguration _configuration;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ProjectEnvironmentService(IConfiguration configuration, TaskScannerService scanner,
        GitService git, ProjectSettingsService settings, TimeProvider clock)
    {
        _configuration = configuration;
        _scanner = scanner;
        _git = git;
        _settings = settings;
        _clock = clock;
    }

    public IReadOnlyList<EnvironmentDeclaration>? Declarations(string project)
    {
        if (!_scanner.GetWatchPaths().Any(item => string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase)))
            return null;
        var root = _git.ResolveRepoRootForProject(project);
        if (root is null) return [];
        var directory = Path.Combine(root, "docs", "deployments", "environments");
        if (!Directory.Exists(directory)) return [];
        var result = new List<EnvironmentDeclaration>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var value = document.RootElement;
            string Required(string name) => value.GetProperty(name).GetString()
                ?? throw new JsonException($"Environment {Path.GetFileName(file)} requires {name}.");
            string? Optional(string name) => value.TryGetProperty(name, out var property) ? property.GetString() : null;
            var id = Required("id");
            var kind = Required("kind");
            var host = Required("targetHostId");
            var template = Required("template");
            var secrets = value.TryGetProperty("secretRefs", out var refs)
                ? refs.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
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
            var containerPort = value.TryGetProperty("containerPort", out var containerPortValue)
                ? containerPortValue.GetInt32() : 8080;
            if (containerPort is < 1 or > 65535) throw new JsonException("Invalid container port.");
            result.Add(new(id, Required("name"), kind, host, Required("address"), template,
                destination, healthUrl, approval, secrets, containerPort));
        }
        if (result.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Environment ids must be unique within a project.");
        return result.OrderBy(item => item.Kind == "stage" ? 0 : item.Kind == "production" ? 1 : 2)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    public ProjectEnvironmentsResponse? Read(string project)
    {
        lock (_gate)
        {
            var declarations = Declarations(project);
            if (declarations is null) return null;
            var state = Load(project);
            AdvanceTimedPromotions(project, declarations, state);
            Save(project, state);
            return new(project, declarations.Select(item =>
            {
                var history = state.History.Where(row => row.EnvironmentId == item.Id)
                    .Reverse().Take(30).Select(row => row.Item).ToArray();
                var latest = history.FirstOrDefault();
                var pending = state.Jobs.LastOrDefault(job => job.EnvironmentId == item.Id
                    && (job.Status is "queued" or "claimed"));
                return new ProjectEnvironmentView(item, latest?.Artifact,
                    latest is null ? "unknown" : latest.Status == "failed" ? "failed" : "healthy",
                    pending?.Artifact, history);
            }).ToArray(), state.Promotions.ToArray(), state.Jobs.Select(Sanitize).ToArray(),
                state.History.Where(row => row.Item.Status == "failed").AsEnumerable().Reverse()
                    .Take(20).Select(row => new DeploymentFailureCard(row.EnvironmentId,
                        row.Item.Artifact, row.At, row.Item.Evidence ?? "deploy-step-failed",
                        row.Item.PreviousArtifact)).ToArray());
        }
    }

    public bool QueueRelease(string project, string artifact, string commit)
    {
        if (!Digest.IsMatch(artifact) || !Commit.IsMatch(commit)) return false;
        var repo = _git.ResolveRepoRootForProject(project);
        if (repo is null || !_git.IsGitRepo(repo)) return false;
        var branch = _git.ResolveIntegrationBranch(repo, _settings.Get(project).IntegrationBranch);
        var tip = _git.GetBranchTip(repo, branch);
        if (tip is null || !_git.IsAncestor(repo, commit, tip) || !HasVerifiedGate(project, commit, branch))
            return false;
        lock (_gate)
        {
            var declarations = Declarations(project);
            var stage = declarations?.FirstOrDefault(item => item.Kind == "stage");
            var production = declarations?.FirstOrDefault(item => item.Kind == "production");
            if (stage is null || production is null) return false;
            var state = Load(project);
            if (state.Promotions.Any(item => item.Artifact == artifact && item.Status is not ("rejected" or "failed"))) return false;
            state.Promotions.Add(new(artifact, stage.Id, production.Id, DateTime.MinValue, "stage-queued"));
            Queue(project, stage, artifact, state, actor: "release-gate");
            Save(project, state);
            return true;
        }
    }

    private bool HasVerifiedGate(string project, string commit, string branch)
    {
        var watch = _scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase));
        if (watch is null || !Directory.Exists(watch.Path)) return false;
        foreach (var file in Directory.EnumerateFiles(watch.Path,
                     IntegrationVerificationStore.FileName, SearchOption.AllDirectories))
        {
            var record = IntegrationVerificationStore.Read(Path.GetDirectoryName(file)!);
            if (record?.State == IntegrationVerificationStates.Verified
                && string.Equals(record.Sha, commit, StringComparison.OrdinalIgnoreCase)
                && IntegrationVerificationProjection.SameBranch(record.IntegrationBranch, branch))
                return true;
        }
        return false;
    }

    public bool Decide(string project, string artifact, bool approve, string actor)
    {
        lock (_gate)
        {
            var declarations = Declarations(project);
            if (declarations is null) return false;
            var state = Load(project);
            var promotion = state.Promotions.LastOrDefault(item => item.Artifact == artifact && item.Status == "pending");
            if (promotion is null || promotion.ExpiresAt is null
                || promotion.ExpiresAt <= _clock.GetUtcNow().UtcDateTime) return false;
            var production = declarations.FirstOrDefault(item => item.Id == promotion.ProductionId);
            if (production is null) return false;
            state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with
            { Status = approve ? "production-queued" : "rejected", Actor = actor };
            if (approve) Queue(project, production, artifact, state, actor: actor);
            Save(project, state);
            return true;
        }
    }

    public bool Rollback(string project, string environmentId, string actor)
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
            if (previous is null) return false;
            Queue(project, declaration, previous.Item.Artifact, state, rollback: true, actor: actor);
            Save(project, state);
            return true;
        }
    }

    public DeployJob? Claim(string hostId)
    {
        lock (_gate)
        {
            var root = StoreRoot();
            if (!Directory.Exists(root)) return null;
            var files = Directory.EnumerateFiles(root, "*.json").ToArray();
            var now = _clock.GetUtcNow().UtcDateTime;
            if (files.Any(file => Load(Path.GetFileNameWithoutExtension(file)).Jobs.Any(job =>
                job.HostId == hostId && job.Status == "claimed" && job.ClaimedAt >= now.AddMinutes(-5))))
                return null;
            foreach (var file in files)
            {
                var project = Path.GetFileNameWithoutExtension(file);
                var state = Load(project);
                var declarations = Declarations(project);
                if (declarations is not null) AdvanceTimedPromotions(project, declarations, state);
                var job = state.Jobs.FirstOrDefault(item => item.HostId == hostId &&
                    (item.Status == "queued" || item.Status == "claimed" && item.ClaimedAt < now.AddMinutes(-5)));
                if (job is null) continue;
                var claimed = job with { Status = "claimed", ClaimedAt = now, LeaseToken = Guid.NewGuid().ToString("N") };
                state.Jobs[state.Jobs.IndexOf(job)] = claimed;
                Save(project, state);
                return claimed;
            }
            return null;
        }
    }

    public bool Report(string project, string jobId, DeployReportRequest report)
    {
        lock (_gate)
        {
            var state = Load(project);
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId && item.Status == "claimed"
                && item.LeaseToken == report.LeaseToken);
            if (job is null || job.ClaimedAt < _clock.GetUtcNow().UtcDateTime.AddMinutes(-5)) return false;
            var now = _clock.GetUtcNow().UtcDateTime;
            var success = report.Healthy;
            state.Jobs[state.Jobs.IndexOf(job)] = job with { Status = success ? "healthy" : "failed", LeaseToken = null };
            // Host output is deliberately not accepted as an audit string. Keep a bounded status code only.
            var evidence = report.Evidence is "health-http-failed" or "deploy-step-failed" or "health-ok"
                ? report.Evidence : success ? "health-ok" : "deploy-step-failed";
            var status = success ? job.IsRollback ? "rolled-back" : "healthy" : "failed";
            state.History.Add(new(job.EnvironmentId, now,
                new(now, status, job.Artifact, job.PreviousArtifact, evidence)));
            var promotion = state.Promotions.LastOrDefault(item => item.Artifact == job.Artifact);
            if (promotion is not null)
            {
                var index = state.Promotions.IndexOf(promotion);
                if (promotion.StageId == job.EnvironmentId && promotion.Status == "stage-queued")
                    state.Promotions[index] = promotion with
                    { Status = success ? "pending" : "failed", StageHealthyAt = now,
                        ExpiresAt = success ? now.AddHours(24) : null };
                if (promotion.ProductionId == job.EnvironmentId && promotion.Status == "production-queued")
                    state.Promotions[index] = promotion with { Status = success ? "deployed" : "failed" };
            }
            if (!success && !job.IsRollback && job.PreviousArtifact is not null)
            {
                var declaration = Declarations(project)?.FirstOrDefault(item => item.Id == job.EnvironmentId);
                if (declaration is not null) Queue(project, declaration, job.PreviousArtifact, state,
                    rollback: true, actor: "automatic-rollback");
            }
            Save(project, state);
            return true;
        }
    }

    private void AdvanceTimedPromotions(string project, IReadOnlyList<EnvironmentDeclaration> declarations, State state)
    {
        foreach (var promotion in state.Promotions.Where(item => item.Status == "pending").ToArray())
        {
            if (promotion.ExpiresAt <= _clock.GetUtcNow().UtcDateTime)
            {
                state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with { Status = "expired" };
                continue;
            }
            var production = declarations.FirstOrDefault(item => item.Id == promotion.ProductionId);
            if (production?.Approval != "stage-healthy-10m" ||
                !state.History.Any(row => row.EnvironmentId == production.Id
                    && (row.Item.Status is "healthy" or "rolled-back")) ||
                _clock.GetUtcNow().UtcDateTime - promotion.StageHealthyAt < TimeSpan.FromMinutes(10)) continue;
            state.Promotions[state.Promotions.IndexOf(promotion)] = promotion with
            { Status = "production-queued", Actor = "stage-health-rule" };
            Queue(project, production, promotion.Artifact, state, actor: "stage-health-rule");
        }
    }

    private void Queue(string project, EnvironmentDeclaration declaration, string artifact, State state,
        bool rollback = false, string? actor = null)
    {
        var previous = state.History.Where(row => row.EnvironmentId == declaration.Id
            && (row.Item.Status is "healthy" or "rolled-back"))
            .LastOrDefault()?.Item.Artifact;
        state.Jobs.Add(new(Guid.NewGuid().ToString("N"), project, declaration.Id,
            declaration.TargetHostId, artifact, declaration.Template, declaration.Destination,
            declaration.HealthUrl, declaration.SecretRefs, declaration.ContainerPort, previous, "queued",
            _clock.GetUtcNow().UtcDateTime,
            IsRollback: rollback, RequestedBy: actor));
    }

    private string StoreRoot() => Path.Combine(_configuration["TaskRepository"] ??
        throw new InvalidOperationException("TaskRepository is required."), "deployments");
    private string FilePath(string project)
    {
        if (!Identifier.IsMatch(project.ToLowerInvariant())) throw new ArgumentException("Invalid project.");
        return Path.Combine(StoreRoot(), project.ToLowerInvariant() + ".json");
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
