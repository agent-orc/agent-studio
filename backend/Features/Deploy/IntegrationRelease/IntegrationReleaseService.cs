using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Deploy;

/// <summary>What creates the next release of a project, and what blocks it now.</summary>
/// <param name="State">One of <see cref="IntegrationReleaseStates"/>.</param>
/// <param name="CanCreate">True when the Releases tab may offer "Create release" for <paramref name="Commit"/>.</param>
public sealed record IntegrationReleaseStatus(string Project, string State, string Message, string? NextStep,
    string? Commit, string? ReleaseId, bool CanCreate);

public static class IntegrationReleaseStates
{
    /// <summary>No Staging and Production declarations: nothing can be released.</summary>
    public const string Unconfigured = "unconfigured";
    /// <summary>The repository carries the promotion train; it creates the releases.</summary>
    public const string PromotionTrain = "promotion-train";
    /// <summary>The integration branch tip has no green gate verdict yet.</summary>
    public const string WaitingForGate = "waiting-for-gate";
    /// <summary>The tip passed its gate and has no release yet.</summary>
    public const string Ready = "ready";
    /// <summary>The tip already has a release.</summary>
    public const string Released = "released";
    /// <summary>A fact or a refusal stops the release; the message names it.</summary>
    public const string Blocked = "blocked";
}

/// <summary>
/// Integration releases (AGT-3891, F1): after a card's
/// delivery is integrated and the project's own gate is green on the
/// integration branch tip, Studio packs that commit's tree into a release
/// package, registers it, and creates the release, which queues the
/// automatic Staging run. No runner.release credential and no files from a
/// shell are involved. A repository that carries the promotion train
/// (<see cref="PromotionTrainScript"/>) keeps releasing through
/// <c>POST /api/deploy/gate-releases</c> unchanged.
/// </summary>
public sealed class IntegrationReleaseService
{
    public const string PromotionTrainScript = "scripts/release/promote-develop-to-main.sh";
    public const string AutomaticActor = "studio-integration";
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly ProjectEnvironmentService _environments;
    private readonly DeployService _deploy;
    private readonly DeployStore _store;
    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntegrationReleaseService> _logger;
    private readonly ProjectRegistry? _registry;
    private readonly PostMergeRunStore? _postMerge;
    private readonly ReleaseCandidateGate? _candidateGate;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, (string Commit, DateTimeOffset At, IntegrationReleaseStatus Status)> _failures =
        new(StringComparer.OrdinalIgnoreCase);

    public IntegrationReleaseService(ProjectEnvironmentService environments, DeployService deploy, DeployStore store,
        GitService git, TaskScannerService scanner, ProjectSettingsService settings, TimeProvider clock,
        ILogger<IntegrationReleaseService> logger, ProjectRegistry? registry = null,
        PostMergeRunStore? postMerge = null, ReleaseCandidateGate? candidateGate = null)
    {
        _environments = environments;
        _deploy = deploy;
        _store = store;
        _git = git;
        _scanner = scanner;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _registry = registry;
        _postMerge = postMerge;
        _candidateGate = candidateGate;
    }

    /// <summary>The Releases tab: what creates the next release and what blocks it. Changes nothing.</summary>
    public async Task<IntegrationReleaseStatus?> StatusAsync(string project, CancellationToken ct)
    {
        var facts = await FactsAsync(project, ct);
        if (facts is null) return null;
        if (facts.Status.State == IntegrationReleaseStates.Ready)
        {
            lock (_failures)
                if (_failures.TryGetValue(project, out var failed) && failed.Commit == facts.Status.Commit)
                    return failed.Status;
        }
        return facts.Status;
    }

    /// <summary>
    /// Creates the release of the gated integration branch tip when nothing
    /// blocks it. <paramref name="automatic"/> is the reconciler: it waits
    /// <see cref="RetryAfterFailure"/> before it repeats a refused attempt
    /// for the same commit; the Releases tab action always tries again.
    /// </summary>
    public async Task<IntegrationReleaseStatus?> CreateAsync(string project, string actor, bool automatic,
        CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            var facts = await FactsAsync(project, ct);
            if (facts is null) return null;
            if (facts.Status.State != IntegrationReleaseStates.Ready) return facts.Status;
            var commit = facts.Status.Commit!;
            lock (_failures)
            {
                if (automatic && _failures.TryGetValue(project, out var failed) && failed.Commit == commit
                    && _clock.GetUtcNow() - failed.At < RetryAfterFailure)
                    return failed.Status;
            }
            var status = Release(facts, actor);
            lock (_failures)
            {
                if (status.State == IntegrationReleaseStates.Blocked)
                    _failures[project] = (commit, _clock.GetUtcNow(), status);
                else _failures.Remove(project);
            }
            return status;
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>One reconciler pass over every registered project.</summary>
    public async Task ReconcileAllAsync(CancellationToken ct)
    {
        if (_registry is null) return;
        foreach (var project in _registry.List().Where(item => !item.Archived))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var status = await CreateAsync(project.DisplayName, AutomaticActor, automatic: true, ct);
                if (status is { State: IntegrationReleaseStates.Blocked })
                    _logger.LogInformation("Integration release of {Project} blocked: {Reason}", project.DisplayName,
                        status.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or HttpRequestException)
            {
                _logger.LogWarning(ex, "Integration release reconciliation failed for {Project}", project.DisplayName);
            }
        }
    }

    private sealed record Facts(IntegrationReleaseStatus Status, string? Branch, string? PackagePath,
        string? GateRunId, ReleaseSuiteVerdict? Verdict);

    private async Task<Facts?> FactsAsync(string project, CancellationToken ct)
    {
        IReadOnlyList<EnvironmentDeclaration>? current;
        try { current = _environments.Declarations(project); }
        catch (JsonException ex)
        {
            return Stop(project, IntegrationReleaseStates.Blocked,
                $"The environment declarations are invalid: {ex.Message}",
                "Fix the declaration under docs/deployments/environments or project.yml; Studio then releases the next green integration.");
        }
        if (current is null) return null;
        if (!HasLifecycle(project, current))
            return Stop(project, IntegrationReleaseStates.Unconfigured,
                "This project declares no Staging and Production environments, so nothing is released.",
                "Declare Staging and Production under docs/deployments/environments or in project.yml. "
                + "Studio then creates a release after each green integration.");
        var repo = _git.ResolveRepoRootForProject(project);
        if (repo is null || !_git.IsGitRepo(repo))
            return Stop(project, IntegrationReleaseStates.Blocked, "Studio cannot read the project repository.",
                "Check the project's server checkout in project settings.");
        var branch = _git.ResolveIntegrationBranch(repo, _settings.Get(project).IntegrationBranch);
        var tip = _git.GetBranchTip(repo, branch);
        if (tip is null || tip.Length != 40)
            return Stop(project, IntegrationReleaseStates.Blocked,
                $"Studio cannot read the tip of the integration branch {branch}.",
                "Check the project's server checkout in project settings.");
        if (_git.FileExistsAtCommit(repo, tip, PromotionTrainScript))
            return Stop(project, IntegrationReleaseStates.PromotionTrain,
                "This repository carries the promotion train. Its releases come from the train after it promotes "
                + $"{branch} and its full gate passes.",
                $"Run {PromotionTrainScript}; it creates the release and starts Staging.", tip);
        var records = _store.ReadReleaseRecords(project);
        if (records.LastOrDefault(item => item.Release.CommitSha == tip) is { } released)
            return Stop(project, IntegrationReleaseStates.Released,
                $"Release {released.Release.Version} is the tip {Short(tip)} of {branch}.",
                "The next card integrated with a green gate creates the next release.", tip, released.Release.Id);
        IReadOnlyList<EnvironmentDeclaration>? atTip;
        try { atTip = _environments.DeclarationsAtCommit(project, tip); }
        catch (JsonException ex)
        {
            return Stop(project, IntegrationReleaseStates.Blocked,
                $"The environment declarations at commit {Short(tip)} are invalid: {ex.Message}",
                "Integrate a fix of the declaration; Studio then releases that commit.", tip);
        }
        if (atTip is null || !HasLifecycle(project, atTip))
            return Stop(project, IntegrationReleaseStates.Blocked,
                $"Commit {Short(tip)} of {branch} does not declare Staging and Production.",
                "Integrate the environment declarations; Studio then releases that commit.", tip);
        var lifecycle = atTip.Where(item => item.Kind is "stage" or "production").ToArray();
        if (lifecycle.FirstOrDefault(item => ProjectEnvironmentService.ArtifactTemplate(item.Template, item.SshSteps)
                != "static") is { } container)
            return Stop(project, IntegrationReleaseStates.Blocked,
                $"{container.Name} deploys a container image. Studio packs static release packages only; "
                + "an image needs a build that pushes it to a registry.",
                $"Release images with the promotion train ({PromotionTrainScript}), or deploy a static artifact.", tip);
        var paths = lifecycle.Select(PackagePath).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length != 1)
            return Stop(project, IntegrationReleaseStates.Blocked,
                "Staging and Production deploy different static artifacts; one release carries one package.",
                "Deploy the same static artifact in Staging and Production.", tip);
        var gate = Gate(project, tip);
        if (gate.Red is { } red)
            return Stop(project, IntegrationReleaseStates.Blocked, red,
                "Repair the integration branch. The next green gate creates the release.", tip);
        if (gate.RunId is null)
            return Stop(project, IntegrationReleaseStates.WaitingForGate,
                $"The tip {Short(tip)} of {branch} has no green gate yet.",
                "Studio creates the release as soon as the gate on this commit passes. Integrating a card runs that gate.",
                tip);
        ReleaseSuiteVerdict? verdict = null;
        if (FullSuiteRequired(repo, tip))
        {
            if (_candidateGate is null)
                return Stop(project, IntegrationReleaseStates.Blocked,
                    "project.yml requires a full-suite verdict, and this server cannot read release suite verdicts.",
                    "Set delivery.fullSuite to optional, or release through the promotion train.", tip);
            ReleaseCandidateGateResult candidate;
            try { candidate = await _candidateGate.InspectAsync(project, tip, ct); }
            catch (HttpRequestException)
            {
                return Stop(project, IntegrationReleaseStates.Blocked, "Task Server verdict storage is unavailable.",
                    "Try again once the Task Server answers.", tip);
            }
            if (!candidate.Allowed)
                return Stop(project, IntegrationReleaseStates.Blocked,
                    candidate.Error ?? "The candidate has no green full-suite verdict.",
                    "Publish a green full-suite verdict for this commit, or set delivery.fullSuite to optional.", tip);
            verdict = candidate.Verdict;
        }
        return new(new(project, IntegrationReleaseStates.Ready,
                $"The tip {Short(tip)} of {branch} passed its gate and has no release yet.",
                "Studio creates its release automatically; Create release does it now.", tip, null, true),
            branch, paths[0], gate.RunId, verdict);
    }

    private IntegrationReleaseStatus Release(Facts facts, string actor)
    {
        var project = facts.Status.Project;
        var commit = facts.Status.Commit!;
        var repo = _git.ResolveRepoRootForProject(project)!;
        var work = Path.Combine(Path.GetTempPath(), "integration-release-" + Guid.NewGuid().ToString("N"));
        var tar = work + ".tar";
        var package = work + ".tar.gz";
        try
        {
            if (!_git.ArchiveCommit(repo, commit, facts.PackagePath, tar))
                return Blocked(project, commit, facts.PackagePath is null
                        ? $"Git could not pack commit {Short(commit)}."
                        : $"Commit {Short(commit)} has no {facts.PackagePath}. Studio packs committed files only; "
                          + "build output that the gate does not commit is not in the package.",
                    facts.PackagePath is null ? "Try again; check the server checkout if it repeats."
                        : $"Commit the static bundle under {facts.PackagePath}, or release through the promotion train.");
            if (Repack(tar, package) is { } unsupported)
                return Blocked(project, commit, unsupported,
                    "Replace links and special files with regular files, then integrate the change.");
            var artifact = _environments.RegisterIntegrationArtifact(project, commit, package,
                facts.GateRunId!);
            if (artifact is null)
                return Blocked(project, commit,
                    $"The package of commit {Short(commit)} could not be registered, or the commit left {facts.Branch}.",
                    "Try again; the next green integration also creates a release.");
            var outcome = _deploy.CreateRelease(project, artifact, commit, actor,
                branch: "refs/heads/" + facts.Branch, preDeliveryVerdict: facts.Verdict);
            if (outcome.Kind != DeployOutcomeKind.Ok)
                return Blocked(project, commit, outcome.Error ?? "The release was refused.",
                    outcome.Reason switch
                    {
                        DeployRefusalReasons.Superseded => "A newer release exists; nothing to do for this commit.",
                        "develop-red" => "Repair the integration branch. The next green gate creates the release.",
                        _ => "Fix the cause above, then choose Create release.",
                    });
            _logger.LogInformation("Integration release {Version} created for {Project} at {Commit} by {Actor}",
                outcome.Value!.Version, project, commit, actor);
            return new(project, IntegrationReleaseStates.Released,
                $"Release {outcome.Value.Version} is the tip {Short(commit)} of {facts.Branch}; Staging is queued.",
                "The next card integrated with a green gate creates the next release.", commit, outcome.Value.Id, false);
        }
        catch (DeployQuotaRefusedException ex)
        {
            return Blocked(project, commit, ex.Message, "Free an environment slot or raise the quota, then choose Create release.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Integration release package for {Project} at {Commit} failed", project, commit);
            return Blocked(project, commit, $"The release package could not be written: {ex.Message}",
                "Try again; check free disk space on the server if it repeats.");
        }
        finally
        {
            foreach (var file in new[] { tar, package })
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _logger.LogDebug(ex, "Integration release work file {Path} stays", file); }
            }
        }
    }

    /// <summary>
    /// Repacks a <c>git archive</c> tar into the gzip release package the
    /// deploy host extracts: directories and regular files only. The pax
    /// header that names the commit is dropped. Returns why the tree cannot
    /// be packed, or null.
    /// </summary>
    internal static string? Repack(string tar, string package)
    {
        var files = 0;
        using (var input = File.OpenRead(tar))
        using (var reader = new TarReader(input))
        using (var output = File.Create(package))
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile
                        or TarEntryType.V7RegularFile))
                    return $"The package cannot hold {entry.Name}: the deploy host extracts directories and regular files only.";
                var directory = entry.EntryType == TarEntryType.Directory;
                var copy = new PaxTarEntry(directory ? TarEntryType.Directory : TarEntryType.RegularFile, entry.Name)
                {
                    ModificationTime = entry.ModificationTime,
                    Mode = entry.Mode,
                };
                if (!directory)
                {
                    copy.DataStream = entry.DataStream;
                    files++;
                }
                writer.WriteEntry(copy);
            }
        }
        return files == 0 ? "The package would be empty." : null;
    }

    private (string? RunId, string? Red) Gate(string project, string tip)
    {
        var record = _registry?.FindByIdOrDisplayName(project) ?? _registry?.FindByShortCode(project);
        if (_postMerge is not null && record is not null)
        {
            var state = _postMerge.Read(record.Id);
            var onTip = state.Runs.LastOrDefault(run => run.Status is "green" or "red"
                && string.Equals(run.TipSha, tip, StringComparison.OrdinalIgnoreCase));
            if (onTip is { Status: "green" }) return ("post-merge/" + onTip.Id, null);
            if (onTip is { Status: "red" })
                return (null, $"The post-merge gate on the tip {Short(tip)} is red.");
        }
        foreach (var task in _scanner.ScanAllJobs().Where(task =>
                     string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)))
        {
            var verification = IntegrationVerificationStore.Read(task.FolderPath);
            if (verification is { State: IntegrationVerificationStates.Verified }
                && string.Equals(verification.Sha, tip, StringComparison.OrdinalIgnoreCase))
                return ("integration/" + (task.Key ?? task.TaskKey), null);
        }
        return (null, null);
    }

    private bool FullSuiteRequired(string repo, string tip)
        => _git.GetFileAtCommit(repo, tip, ProjectPreparationPaths.Definition) is { } yaml
            && ProjectDefinitionReader.Parse(yaml).Definition?.DeliveryFullSuite == "required";

    private static bool HasLifecycle(string project, IReadOnlyList<EnvironmentDeclaration> declarations)
    {
        var environments = DeployService.MapEnvironments(project, declarations);
        return environments.Any(item => item.Kind == EnvironmentKinds.Staging)
            && environments.Any(item => item.Kind == EnvironmentKinds.Production);
    }

    /// <summary>The repository path the deploy host extracts, or null for the whole tree.</summary>
    private static string? PackagePath(EnvironmentDeclaration declaration)
        => declaration.SshSteps.FirstOrDefault() is { Artifact: { } name }
            && declaration.Artifacts.FirstOrDefault(item => item.Name == name)?.Path is { } path and not "."
            ? path : null;

    private IntegrationReleaseStatus Blocked(string project, string commit, string message, string nextStep)
        => new(project, IntegrationReleaseStates.Blocked, message, nextStep, commit, null, true);

    private static Facts Stop(string project, string state, string message, string nextStep,
        string? commit = null, string? releaseId = null)
        => new(new(project, state, message, nextStep, commit, releaseId, false), null, null, null, null);

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}

/// <summary>Runs <see cref="IntegrationReleaseService.ReconcileAllAsync"/> on a short interval.</summary>
public sealed class IntegrationReleaseReconciler(IntegrationReleaseService releases, TimeProvider clock,
    ILogger<IntegrationReleaseReconciler> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, clock);
            do
            {
                try { await releases.ReconcileAllAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning(ex, "Integration release reconciliation failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Integration release reconciler stopped");
        }
    }
}
