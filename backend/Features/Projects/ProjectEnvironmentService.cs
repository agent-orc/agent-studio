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
public sealed record GateArtifactRequest(string Project, string Artifact, string Commit);

/// <summary>Repository declarations and a separate, durable host-deploy queue.</summary>
public sealed class ProjectEnvironmentService
{
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex SecretIdentifier = new("^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex Commit = new(@"\A[0-9a-f]{40}\z", RegexOptions.Compiled);
    private static readonly Regex Digest = new(@"\A[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}\z", RegexOptions.Compiled);
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
        if (!_scanner.GetWatchPaths().Any(item => string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase)))
            return null;
        var root = _git.ResolveRepoRootForProject(project);
        if (root is null) return [];
        var directory = Path.Combine(root, "docs", "deployments", "environments");
        if (!Directory.Exists(directory)) return [];
        var result = new List<EnvironmentDeclarationFile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var declaration = ReadDeclaration(file);
                result.Add(new(declaration.Id, declaration, null));
            }
            catch (JsonException ex)
            {
                result.Add(new(Path.GetFileNameWithoutExtension(file), null, $"{Path.GetFileName(file)}: {ex.Message}"));
            }
        }
        var duplicates = result.Where(item => item.Declaration is not null).GroupBy(item => item.EnvironmentId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return result.Select(item => item.Declaration is not null && duplicates.Contains(item.EnvironmentId)
                ? item with { Declaration = null, Error = "Environment ids must be unique within a project." } : item)
            .OrderBy(item => item.Declaration?.Kind switch { null => 3, "stage" => 0, "production" => 1, _ => 2 })
            .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal).ToArray();
    }

    private static EnvironmentDeclaration ReadDeclaration(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
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
        if (!HasBuiltArtifact(project, artifact, commit)) return false;
        var repo = _git.ResolveRepoRootForProject(project);
        if (repo is null || !_git.IsGitRepo(repo)) return false;
        var branch = _git.ResolveIntegrationBranch(repo, _settings.Get(project).IntegrationBranch);
        var tip = _git.GetBranchTip(repo, branch);
        if (tip is null || !_git.IsAncestor(repo, commit, tip))
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
                var project = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file));
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
            return job is null || job.ClaimedAt < _clock.GetUtcNow().UtcDateTime.AddMinutes(-5) ? null : job;
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
        return Path.Combine(StoreRoot(), SafeProjectName(project) + ".json");
    }
    private static string SafeProjectName(string project)
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
