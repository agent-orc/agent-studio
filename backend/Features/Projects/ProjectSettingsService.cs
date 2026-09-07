using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

/// <summary>
/// Per-project preferences that persist across restarts (auto-commit toggle today;
/// future per-project flags fit here too). Stored as a single JSON map next to
/// the task repository; falls back to LocalAppData when the repository path is
/// not configured so the file always lives on a writable disk.
/// </summary>
public class ProjectSettingsService
{
    private readonly ILogger<ProjectSettingsService> _logger;
    private readonly IConfiguration _config;
    private readonly IAtomicJsonFileWriter _fileWriter;
    private readonly object _lock = new();
    private Dictionary<string, ProjectSettings> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ProjectSettingsService(
        ILogger<ProjectSettingsService> logger,
        IConfiguration config,
        IAtomicJsonFileWriter? fileWriter = null)
    {
        _logger = logger;
        _config = config;
        _fileWriter = fileWriter ?? new AtomicJsonFileWriter();
    }

    public ProjectSettings Get(string projectName)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var resolved = ResolveAliasLocked(projectName);
            return _cache.TryGetValue(resolved, out var s)
                ? ProjectExecutionPolicy.Migrate(s)
                : ProjectExecutionPolicy.Migrate(new ProjectSettings());
        }
    }

    public Dictionary<string, ProjectSettings> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _cache.ToDictionary(
                kv => kv.Key,
                kv => ProjectExecutionPolicy.Migrate(kv.Value),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetAutoCommit(string projectName, bool enabled)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { AutoCommit = enabled };
            Persist();
        }
    }

    public void SetCrashRecoveryEnabled(string projectName, bool enabled)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { CrashRecoveryEnabled = enabled };
            Persist();
        }
    }

    public void SetAutoPushStrategy(string projectName, string strategy)
    {
        EnsureLoaded();
        var normalized = AutoPushStrategies.Normalize(strategy);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { AutoPushStrategy = normalized };
            Persist();
        }
    }

    /// <summary>
    /// ADR-0052: sets the max number of tasks the runner may run concurrently
    /// for this project. Clamped to <c>&gt;= 1</c>; <c>1</c> keeps the runner
    /// sequential.
    /// </summary>
    public void SetMaxParallelism(string projectName, int maxParallelism)
    {
        EnsureLoaded();
        var clamped = maxParallelism < 1 ? 1 : maxParallelism;
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { MaxParallelism = clamped };
            Persist();
        }
        _logger.LogInformation("Max parallelism set to {Max} for project {Project}", clamped, projectName);
    }

    public void SetPublishAutomation(string projectName, string targetId, string mode)
    {
        EnsureLoaded();
        var normalized = PublishAutomationModes.Normalize(targetId, mode);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var map = current.PublishAutomation is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.PublishAutomation, StringComparer.OrdinalIgnoreCase);
            map[targetId.Trim()] = normalized;
            _cache[key] = current with { PublishAutomation = map };
            Persist();
        }
        _logger.LogInformation(
            "publish-automation-updated project={Project} target={Target} mode={Mode}",
            projectName, targetId, normalized);
    }

    /// <summary>
    /// Legacy assignment helper. A remote runner id implies automatic pickup on
    /// that runner; blank or disabled remote execution resolves to local while
    /// preserving the current pickup mode.
    /// </summary>
    public void SetExecutionRunner(string projectName, string? executionRunner, bool? remoteExecutionEnabled = null)
        => SetExecutionSettings(
            projectName,
            pickupMode: !string.IsNullOrWhiteSpace(executionRunner)
                        && remoteExecutionEnabled != false
                ? PickupModes.Auto
                : null,
            executionLocation: executionRunner,
            remoteExecutionEnabled: remoteExecutionEnabled);

    /// <summary>
    /// Atomically writes pickup intent and execution placement. Null arguments
    /// preserve that dimension. The legacy mirrors remain populated so older
    /// readers keep working while every write persists the canonical fields.
    /// </summary>
    public void SetExecutionSettings(
        string projectName,
        string? pickupMode,
        string? executionLocation,
        bool? remoteExecutionEnabled = null)
    {
        EnsureLoaded();
        if (pickupMode is not null && !PickupModes.IsValid(pickupMode))
            throw new ArgumentException($"Unsupported pickup mode '{pickupMode}'.", nameof(pickupMode));
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = ProjectExecutionPolicy.Migrate(
                _cache.TryGetValue(key, out var s) ? s : new ProjectSettings());
            var resolvedPickup = pickupMode is null
                ? current.PickupMode!
                : PickupModes.Normalize(pickupMode);
            var resolvedLocation = executionLocation is null
                ? current.ExecutionLocation!
                : ExecutionLocations.Normalize(executionLocation);
            var disabledLegacyRunner = remoteExecutionEnabled == false
                                       && ProjectExecutionPolicy.IsLegacyRemoteRunner(executionLocation)
                ? executionLocation!.Trim()
                : null;
            if (remoteExecutionEnabled == false)
                resolvedLocation = ExecutionLocations.Local;
            _cache[key] = current with
            {
                PickupMode = resolvedPickup,
                ExecutionLocation = resolvedLocation,
                RunnerMode = pickupMode is null ? current.RunnerMode : PickupModes.ToRunnerMode(resolvedPickup),
                DesiredRunnerMode = pickupMode is null ? current.DesiredRunnerMode : PickupModes.ToRunnerMode(resolvedPickup),
                ExecutionRunner = disabledLegacyRunner
                                  ?? (resolvedLocation == ExecutionLocations.Local ? null : resolvedLocation),
                RemoteExecutionEnabled = resolvedLocation != ExecutionLocations.Local,
            };
            Persist();
        }
        _logger.LogInformation(
            "project-execution-settings project={Project} pickupMode={PickupMode} executionLocation={ExecutionLocation}",
            projectName, Get(projectName).PickupMode, Get(projectName).ExecutionLocation);
    }

    /// <summary>
    /// Rekeys all settings when editable registry metadata changes a project's
    /// display name. The optional runner update is folded into the same cache
    /// mutation and store write so the project-basics endpoint cannot orphan
    /// the runner assignment (or any other setting) under the old name.
    /// </summary>
    public ProjectSettings RekeyProject(
        string previousProjectName,
        string currentProjectName,
        bool updateExecutionRunner = false,
        string? executionRunner = null,
        bool? remoteExecutionEnabled = null)
    {
        if (string.IsNullOrWhiteSpace(previousProjectName))
            throw new ArgumentException("previousProjectName is required", nameof(previousProjectName));
        if (string.IsNullOrWhiteSpace(currentProjectName))
            throw new ArgumentException("currentProjectName is required", nameof(currentProjectName));

        var previous = previousProjectName.Trim();
        var current = currentProjectName.Trim();
        var runner = string.IsNullOrWhiteSpace(executionRunner) ? null : executionRunner.Trim();
        EnsureLoaded();
        lock (_lock)
        {
            var cacheBefore = new Dictionary<string, ProjectSettings>(_cache, StringComparer.OrdinalIgnoreCase);
            var renamed = !string.Equals(previous, current, StringComparison.Ordinal);
            var hasPrevious = _cache.TryGetValue(previous, out var previousSettings);
            var hasCurrent = _cache.TryGetValue(current, out var currentSettings);
            var settings = hasPrevious
                ? previousSettings!
                : !renamed && hasCurrent
                    ? currentSettings!
                    : new ProjectSettings();

            if (updateExecutionRunner)
            {
                var location = remoteExecutionEnabled == false
                    ? ExecutionLocations.Local
                    : ExecutionLocations.Normalize(runner);
                var pickupMode = location == ExecutionLocations.Local
                    ? ProjectExecutionPolicy.ResolvePickupMode(settings)
                    : PickupModes.Auto;
                settings = settings with
                {
                    PickupMode = pickupMode,
                    ExecutionLocation = location,
                    RunnerMode = PickupModes.ToRunnerMode(pickupMode),
                    DesiredRunnerMode = PickupModes.ToRunnerMode(pickupMode),
                    ExecutionRunner = location == ExecutionLocations.Local ? null : location,
                    RemoteExecutionEnabled = location != ExecutionLocations.Local,
                };
            }

            if (!renamed && !updateExecutionRunner) return settings;

            if (renamed
                && !string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)
                && hasPrevious
                && hasCurrent)
            {
                _logger.LogWarning(
                    "project-settings-rekey-overwrites-stale-target previous={Previous} current={Current}",
                    previous, current);
            }

            if (renamed) _cache.Remove(previous);
            // Remove first so a case-only rename updates the serialized key's
            // casing in a case-insensitive dictionary.
            _cache.Remove(current);
            _cache[current] = settings;
            try { PersistStrict(); }
            catch
            {
                _cache = cacheBefore;
                throw;
            }

            if (renamed) UpdateAliasesAfterRenameLocked(previous, current);
            _logger.LogInformation(
                "project-settings-rekeyed previous={Previous} current={Current} runnerUpdated={RunnerUpdated}",
                previous, current, updateExecutionRunner);
            return settings;
        }
    }

    /// <summary>
    /// ADR-0052: sets the integration branch parallel task worktrees branch off
    /// and merge back into. Blank reverts to repository <c>origin/HEAD</c>.
    /// </summary>
    public void SetIntegrationBranch(string projectName, string? branch)
    {
        EnsureLoaded();
        var value = string.IsNullOrWhiteSpace(branch) ? string.Empty : branch.Trim();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { IntegrationBranch = value };
            Persist();
        }
        _logger.LogInformation("Integration branch set to {Branch} for project {Project}", value, projectName);
    }

    /// <summary>
    /// ADR-0052: sets how a finished task branch is folded back into the
    /// integration branch. Unknown values normalize to <c>direct-merge</c>.
    /// </summary>
    public void SetIntegrationStrategy(string projectName, string strategy)
    {
        EnsureLoaded();
        var normalized = IntegrationStrategies.Normalize(strategy);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { IntegrationStrategy = normalized };
            Persist();
        }
        _logger.LogInformation("Integration strategy set to {Strategy} for project {Project}", normalized, projectName);
    }

    /// <summary>
    /// Slice P (ASS-1663): declares (or re-declares) the project's build profile.
    /// Normalizes blank command/path entries away. The first declaration starts
    /// blocked. Editing a previously validated profile preserves its last green
    /// status for a bounded three-run grace window and marks revalidation pending.
    /// Pass a null <paramref name="profile"/> to clear the profile entirely.
    /// </summary>
    public void SetBuildProfile(string projectName, BuildProfile? profile)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { BuildProfile = NormalizeProfile(profile, current.BuildProfile) };
            Persist();
        }
        _logger.LogInformation(
            "Build profile {Action} for project {Project}",
            profile is null ? "cleared" : "declared", projectName);
    }

    /// <summary>
    /// Marks the project's build profile as a validation dry-run in progress. No-op
    /// when the project has no declared profile.
    /// </summary>
    public void MarkBuildProfileValidating(string projectName) =>
        TransitionProfileStatus(projectName, BuildProfileStatuses.Validating, validatedAt: null, error: null);

    /// <summary>
    /// Marks the project's build profile pipeline-ready after a green validation
    /// dry-run (install + build succeeded). Stamps <see cref="BuildProfile.LastValidatedAt"/>
    /// and clears any prior error. No-op when the project has no declared profile.
    /// </summary>
    public void MarkBuildProfileValidated(string projectName) =>
        TransitionProfileStatus(projectName, BuildProfileStatuses.PipelineReady, validatedAt: DateTime.UtcNow, error: null);

    /// <summary>
    /// Marks the project's build profile validation as failed and records a short
    /// reason. The project stays blocked from auto-pickup until it re-validates
    /// green. No-op when the project has no declared profile.
    /// </summary>
    public void MarkBuildProfileValidationFailed(string projectName, string? error) =>
        TransitionProfileStatus(projectName, BuildProfileStatuses.ValidationFailed, validatedAt: null,
            error: string.IsNullOrWhiteSpace(error) ? "validation failed" : error.Trim());

    /// <summary>
    /// Records a green remote Review as validation proof only when the immutable
    /// Review plan carried the current profile's exact fingerprint.
    /// </summary>
    public bool MarkBuildProfileRemotelyValidated(
        string projectName,
        string? profileFingerprint,
        string attemptId,
        string runnerId,
        string resultSha,
        DateTime validatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(profileFingerprint)) return false;
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var settings) ? settings : new ProjectSettings();
            if (current.BuildProfile is null) return false;
            var currentFingerprint = BuildProfileValidationFingerprint.Create(current.BuildProfile);
            if (!string.Equals(currentFingerprint, profileFingerprint, StringComparison.Ordinal))
                return false;

            _cache[key] = current with
            {
                BuildProfile = current.BuildProfile with
                {
                    Status = BuildProfileStatuses.PipelineReady,
                    LastValidatedAt = validatedAtUtc.ToUniversalTime(),
                    LastValidationError = null,
                    ValidationFingerprint = currentFingerprint,
                    RevalidationPending = false,
                    RevalidationGraceRunsRemaining = 0,
                    LastRemoteValidationAt = validatedAtUtc.ToUniversalTime(),
                    LastRemoteValidationAttemptId = attemptId,
                    LastRemoteValidationRunnerId = runnerId,
                    LastRemoteValidationResultSha = resultSha,
                },
            };
            Persist();
        }
        _logger.LogInformation(
            "Build profile remotely validated for project {Project} by {Runner} in {AttemptId} at {ResultSha}",
            projectName, runnerId, attemptId, resultSha);
        return true;
    }

    /// <summary>
    /// Charges one successfully admitted coding run against a pending profile's
    /// grace window. Returns the remaining allowance after the charge.
    /// </summary>
    public int? ConsumeBuildProfileRevalidationGraceRun(string projectName)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var settings) ? settings : new ProjectSettings();
            var profile = current.BuildProfile;
            if (profile is not { RevalidationPending: true, RevalidationGraceRunsRemaining: > 0 })
                return null;
            var remaining = profile.RevalidationGraceRunsRemaining - 1;
            _cache[key] = current with
            {
                BuildProfile = profile with { RevalidationGraceRunsRemaining = remaining },
            };
            Persist();
            return remaining;
        }
    }

    private void TransitionProfileStatus(string projectName, string status, DateTime? validatedAt, string? error)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            if (current.BuildProfile is null) return; // nothing to transition
            _cache[key] = current with
            {
                BuildProfile = current.BuildProfile with
                {
                    Status = status,
                    LastValidatedAt = validatedAt ?? current.BuildProfile.LastValidatedAt,
                    LastValidationError = status == BuildProfileStatuses.ValidationFailed ? error : null,
                    ValidationFingerprint = BuildProfileValidationFingerprint.Create(current.BuildProfile),
                    RevalidationPending = false,
                    RevalidationGraceRunsRemaining = 0,
                }
            };
            Persist();
        }
        _logger.LogInformation("Build profile status -> {Status} for project {Project}", status, projectName);
    }

    /// <summary>
    /// Trims blank command/path entries, clamps a non-positive pool size to null,
    /// and applies the bounded revalidation rule.
    /// Returns null when the input is null.
    /// </summary>
    private static BuildProfile? NormalizeProfile(BuildProfile? profile, BuildProfile? previous)
    {
        if (profile is null) return null;

        static IReadOnlyList<string>? Clean(IReadOnlyList<string>? items)
        {
            var list = (items ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            return list.Count == 0 ? null : list;
        }

        var normalized = new BuildProfile
        {
            Stack = string.IsNullOrWhiteSpace(profile.Stack) ? null : profile.Stack.Trim(),
            InstallCmd = string.IsNullOrWhiteSpace(profile.InstallCmd) ? null : profile.InstallCmd.Trim(),
            BuildCmds = Clean(profile.BuildCmds),
            TestCmds = Clean(profile.TestCmds),
            Lockfiles = Clean(profile.Lockfiles),
            PreserveGlobs = Clean(profile.PreserveGlobs),
            PoolSize = profile.PoolSize is > 0 ? profile.PoolSize : null,
            Status = BuildProfileStatuses.Declared,
        };
        var fingerprint = BuildProfileValidationFingerprint.Create(normalized);
        if (previous is null)
            return normalized with { ValidationFingerprint = fingerprint };

        var previousFingerprint = BuildProfileValidationFingerprint.Create(previous);
        if (string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return normalized with
            {
                Status = previous.Status,
                LastValidatedAt = previous.LastValidatedAt,
                LastValidationError = previous.LastValidationError,
                ValidationFingerprint = fingerprint,
                RevalidationPending = previous.RevalidationPending,
                RevalidationGraceRunsRemaining = previous.RevalidationGraceRunsRemaining,
                LastRemoteValidationAt = previous.LastRemoteValidationAt,
                LastRemoteValidationAttemptId = previous.LastRemoteValidationAttemptId,
                LastRemoteValidationRunnerId = previous.LastRemoteValidationRunnerId,
                LastRemoteValidationResultSha = previous.LastRemoteValidationResultSha,
            };
        }

        var previouslyValidated = BuildProfileStatuses.Normalize(previous.Status)
                                  == BuildProfileStatuses.PipelineReady;
        return normalized with
        {
            Status = previouslyValidated
                ? BuildProfileStatuses.PipelineReady
                : BuildProfileStatuses.Declared,
            LastValidatedAt = previous.LastValidatedAt,
            ValidationFingerprint = fingerprint,
            RevalidationPending = previouslyValidated,
            RevalidationGraceRunsRemaining = previouslyValidated
                ? previous.RevalidationPending
                    ? Math.Max(0, previous.RevalidationGraceRunsRemaining)
                    : BuildProfile.DefaultRevalidationGraceRuns
                : 0,
            LastRemoteValidationAt = previous.LastRemoteValidationAt,
            LastRemoteValidationAttemptId = previous.LastRemoteValidationAttemptId,
            LastRemoteValidationRunnerId = previous.LastRemoteValidationRunnerId,
            LastRemoteValidationResultSha = previous.LastRemoteValidationResultSha,
        };
    }

    /// <summary>
    /// Persists the runner mode for a project so the auto-pickup toggle survives
    /// a backend restart. Null clears the persisted value (revert to default).
    /// <para>
    /// <paramref name="source"/> is the <c>ClassifyModeSource</c> bucket of the
    /// change (<c>user</c> / <c>circuit-breaker</c> / <c>supervisor</c> /
    /// <c>system</c>). <see cref="ProjectSettings.RunnerMode"/> always mirrors
    /// the live mode (the supervisor meta-cycle reads it for drift detection),
    /// but <see cref="ProjectSettings.DesiredRunnerMode"/> - the value restored
    /// at boot - is only advanced for a <c>user</c>-sourced change. A
    /// system-driven flip (update-quiesce, circuit-breaker pause) therefore
    /// records the manual it imposed without erasing the operator's durable
    /// auto-continuous intent, which is what kept getting clobbered across a
    /// deploy-restart (ASS-1753). Default <c>user</c> preserves the legacy
    /// behaviour for any caller that does not classify its source.
    /// </para>
    /// </summary>
    public void SetRunnerMode(string projectName, string? mode, string source = "user")
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var isUser = string.Equals(source, "user", StringComparison.OrdinalIgnoreCase);
            // Legacy-record backfill: entries written before DesiredRunnerMode
            // existed carry the operator's intent only in RunnerMode. A
            // system-sourced flip is about to overwrite that mirror — and the
            // boot restore falls back to RunnerMode when DesiredRunnerMode is
            // empty — so without this backfill one transient system flip
            // (CLI-unspawnable pause, circuit breaker) would permanently
            // downgrade the boot mode. Preserve the pre-flip value as the
            // durable intent before the mirror moves.
            var desired = current.DesiredRunnerMode;
            if (!isUser && string.IsNullOrWhiteSpace(desired) && !string.IsNullOrWhiteSpace(current.RunnerMode))
                desired = current.RunnerMode;
            _cache[key] = current with
            {
                RunnerMode = mode,
                DesiredRunnerMode = isUser ? mode : desired,
                PickupMode = PickupModes.FromRunnerMode(mode),
            };
            Persist();
        }
    }

    /// <summary>
    /// Sets the model the orchestrator uses when deciding on the user's
    /// behalf in auto mode. Null clears (revert to default Opus).
    /// </summary>
    public void SetOrchestratorModel(string projectName, string? model, string? thinkingLevel = null)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            _cache[key] = current with
            {
                OrchestratorModel = normalizedModel,
                OrchestratorThinkingLevel = thinkingLevel is null
                    ? current.OrchestratorThinkingLevel
                    : (string.IsNullOrWhiteSpace(thinkingLevel)
                        ? null
                        : ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Claude, normalizedModel, thinkingLevel))
            };
            Persist();
        }
    }

    /// <summary>
    /// Sets or clears the per-project local CLI execution-engine override.
    /// Blank clears the override; unknown non-blank values are rejected.
    /// </summary>
    public void SetCliExecutionEngine(string projectName, string? executionEngine)
    {
        var normalized = CliExecutionEngines.NormalizeOverride(executionEngine);
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { CliExecutionEngine = normalized };
            Persist();
        }
        _logger.LogInformation(
            "CLI execution-engine override set to {ExecutionEngine} for project {Project}",
            normalized ?? "(workspace/default)", projectName);
    }

    /// <summary>
    /// Tunes the epic decomposition (planning) run for a project. A null
    /// argument leaves that knob untouched, so the caller can set the model
    /// and the backlog/ready target independently. An empty model string
    /// clears the override (revert to the epic card's own model).
    /// </summary>
    public void SetEpicPlanning(string projectName, string? model, string? thinkingLevel, bool? subTasksToReady)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var normalizedModel = model is null
                ? current.EpicPlanningModel
                : (string.IsNullOrWhiteSpace(model) ? null : model.Trim());
            _cache[key] = current with
            {
                EpicPlanningModel = normalizedModel,
                EpicPlanningThinkingLevel = thinkingLevel is null
                    ? current.EpicPlanningThinkingLevel
                    : (string.IsNullOrWhiteSpace(thinkingLevel)
                        ? null
                        : ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Claude, normalizedModel, thinkingLevel)),
                EpicSubTasksToReady = subTasksToReady ?? current.EpicSubTasksToReady,
            };
            Persist();
        }
        _logger.LogInformation(
            "Epic planning settings updated for project {Project} (model={Model}, subTasksToReady={ToReady})",
            projectName, model, subTasksToReady);
    }

    /// <summary>
    /// ADR-0026: sets the per-project autonomy level for the
    /// orchestrator-prep loop. Accepts <c>0..4</c>; out-of-range values are
    /// clamped to the nearest valid stop. Null clears (revert to the default
    /// balanced level when the setting is read).
    /// </summary>
    /// <summary>
    /// Per-project toggle for the orchestrator-intake loop. When enabled, the
    /// coding runner stops picking up 2-ready cards until intake has marked
    /// them as <c>phase == intake-passed</c>. Default is disabled (null is
    /// treated as false at the read site).
    /// </summary>
    public void SetIntakeEnabled(string projectName, bool? enabled)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { IntakeEnabled = enabled };
            Persist();
        }
        _logger.LogInformation("Intake enabled set to {Enabled} for project {Project}", enabled, projectName);
    }

    public void SetAutonomyLevel(string projectName, int? level)
    {
        EnsureLoaded();
        int? clamped = level is null ? null : Math.Clamp(level.Value, 0, 4);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { AutonomyLevel = clamped };
            Persist();
        }
        _logger.LogInformation("Autonomy level set to {Level} for project {Project}", clamped, projectName);
    }

    /// <summary>
    /// Sets or clears the per-project wait-on-quota overrides. Null fields
    /// inherit the global CLI/quota policy. Thresholds are clamped to the same
    /// bounds as the global setting.
    /// </summary>
    public void SetQuotaWaitPolicy(string projectName, bool? enabled, int? thresholdMinutes)
    {
        EnsureLoaded();
        int? threshold = thresholdMinutes is null
            ? null
            : CliQuotaWaitPolicyService.Clamp(thresholdMinutes.Value);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with
            {
                WaitOnQuotaEnabled = enabled,
                WaitOnQuotaThresholdMinutes = threshold,
            };
            Persist();
        }
        _logger.LogInformation(
            "Project wait-on-quota override set: project={Project} enabled={Enabled} thresholdMinutes={Threshold}",
            projectName, enabled, threshold);
    }

    /// <summary>
    /// Sets the cadence for one analysis-report topic on this project.
    /// Cadences are validated by the caller; null or empty value clears the
    /// entry (revert to "disabled" default). Every project starts with no
    /// schedules so reports never auto-run without an explicit opt-in.
    /// </summary>
    /// <summary>
    /// F35: writes the sort strategy override for a single lane. A null or
    /// empty <paramref name="strategy"/> clears the override (the lane
    /// reverts to <see cref="LaneSortStrategies.GetDefaultForLane"/>).
    /// Invalid strategy ids are rejected by the caller; this method
    /// normalises to ensure only canonical ids land on disk.
    /// </summary>
    public void SetLaneSortStrategy(string projectName, string lane, string? strategy)
    {
        if (string.IsNullOrWhiteSpace(lane)) return;
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var map = current.LaneSortStrategyOverrides is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.LaneSortStrategyOverrides, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(strategy))
            {
                map.Remove(lane.Trim());
            }
            else
            {
                map[lane.Trim()] = LaneSortStrategies.Normalize(strategy);
            }
            _cache[key] = current with { LaneSortStrategyOverrides = map.Count == 0 ? null : map };
            Persist();
        }
    }

    /// <summary>
    /// Upsert the per-project override for one pipeline step
    /// (<see cref="ProjectSettings.PipelineSteps"/>). Null fields inside the
    /// supplied <paramref name="setting"/> stay null (no override on that
    /// dimension). Passing a null <paramref name="setting"/>, or one whose
    /// every field is null, removes the entry so the step reverts to its
    /// built-in defaults.
    /// </summary>
    public void SetPipelineStep(string projectName, string stepId, PipelineStepSetting? setting)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return;
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var map = current.PipelineSteps is null
                ? new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, PipelineStepSetting>(current.PipelineSteps, StringComparer.OrdinalIgnoreCase);

            var normalizedCliType = string.IsNullOrWhiteSpace(setting?.CliType)
                ? null
                : setting!.CliType!.Trim().ToLowerInvariant();
            var normalizedModel = string.IsNullOrWhiteSpace(setting?.Model) ? null : setting!.Model!.Trim();
            var normalizedThinkingLevel = string.IsNullOrWhiteSpace(setting?.ThinkingLevel)
                ? null
                : setting!.ThinkingLevel!.Trim().ToLowerInvariant();
            var normalizedMode = string.IsNullOrWhiteSpace(setting?.Mode) ? null : setting!.Mode!.Trim().ToLowerInvariant();
            var normalizedPrompt = string.IsNullOrWhiteSpace(setting?.Prompt) ? null : setting!.Prompt!.Trim();
            var normalizedCondition = NormalizeCondition(setting?.Condition);
            var isEmpty = setting is null
                || (setting.Enabled is null && setting.EconomyModel is null && setting.MaxIterations is null && normalizedMode is null && normalizedCliType is null && normalizedModel is null && normalizedThinkingLevel is null && normalizedPrompt is null && normalizedCondition is null);

            if (isEmpty)
            {
                map.Remove(stepId.Trim());
            }
            else
            {
                map[stepId.Trim()] = new PipelineStepSetting
                {
                    Enabled = setting!.Enabled,
                    EconomyModel = setting.EconomyModel,
                    MaxIterations = setting.MaxIterations,
                    Mode = normalizedMode,
                    CliType = normalizedCliType,
                    Model = normalizedModel,
                    ThinkingLevel = normalizedThinkingLevel,
                    Prompt = normalizedPrompt,
                    PromptBaseDefaultSha = normalizedPrompt is null
                        ? null
                        : setting.PromptBaseDefaultSha,
                    PromptBaseDefaultContent = normalizedPrompt is null
                        ? null
                        : setting.PromptBaseDefaultContent,
                    Condition = normalizedCondition,
                };
            }
            _cache[key] = current with { PipelineSteps = map.Count == 0 ? null : map };
            Persist();
        }
        _logger.LogInformation("Pipeline step '{StepId}' config updated for project {Project}", stepId, projectName);
    }

    /// <summary>
    /// Upsert one step override inside an explicit pipeline type. This is the
    /// canonical write path for pipeline administration. The flat overload is
    /// retained only for compatibility with older callers and persisted files.
    /// </summary>
    public void SetPipelineStep(
        string projectName,
        string pipelineType,
        string stepId,
        PipelineStepSetting? setting)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return;
        var type = PipelineTypes.Normalize(pipelineType);
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var byType = ClonePipelineStepsByType(current.PipelineStepsByType);
            var map = byType.TryGetValue(type, out var existing)
                ? new Dictionary<string, PipelineStepSetting>(existing, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase);

            var normalized = NormalizePipelineStepSetting(setting);
            if (normalized is null)
                map.Remove(stepId.Trim());
            else
                map[stepId.Trim()] = normalized;

            if (map.Count == 0)
                byType.Remove(type);
            else
                byType[type] = map;

            _cache[key] = current with
            {
                PipelineStepsByType = byType.Count == 0 ? null : byType,
            };
            Persist();
        }
        _logger.LogInformation(
            "Pipeline step '{StepId}' config updated for project {Project}, type {PipelineType}",
            stepId, projectName, type);
    }

    /// <summary>
    /// Stores the operator-selected order for configurable pre/post pipeline
    /// steps. Unknown-step validation is done by the endpoint because only the
    /// endpoint has the current catalogue in hand; this method normalizes the
    /// payload to distinct, trimmed ids and clears the setting when empty.
    /// </summary>
    public void SetPipelineStepOrder(string projectName, IReadOnlyList<string>? stepIds)
    {
        EnsureLoaded();
        var normalized = NormalizePipelineStepOrder(stepIds);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            _cache[key] = current with { PipelineStepOrder = normalized.Count == 0 ? null : normalized };
            Persist();
        }
        _logger.LogInformation(
            "Pipeline step order updated for project {Project} ({Count} configured ids)",
            projectName, normalized.Count);
    }

    /// <summary>Store the pre/post order for one explicit pipeline type.</summary>
    public void SetPipelineStepOrder(
        string projectName,
        string pipelineType,
        IReadOnlyList<string>? stepIds)
    {
        var type = PipelineTypes.Normalize(pipelineType);
        EnsureLoaded();
        var normalized = NormalizePipelineStepOrder(stepIds);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var byType = current.PipelineStepOrderByType is null
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, IReadOnlyList<string>>(
                    current.PipelineStepOrderByType,
                    StringComparer.OrdinalIgnoreCase);
            if (normalized.Count == 0)
                byType.Remove(type);
            else
                byType[type] = normalized;
            _cache[key] = current with
            {
                PipelineStepOrderByType = byType.Count == 0 ? null : byType,
            };
            Persist();
        }
        _logger.LogInformation(
            "Pipeline step order updated for project {Project}, type {PipelineType} ({Count} configured ids)",
            projectName, type, normalized.Count);
    }

    private static Dictionary<string, Dictionary<string, PipelineStepSetting>> ClonePipelineStepsByType(
        IReadOnlyDictionary<string, Dictionary<string, PipelineStepSetting>>? source)
    {
        var result = new Dictionary<string, Dictionary<string, PipelineStepSetting>>(
            StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var type in source)
        {
            result[type.Key] = new Dictionary<string, PipelineStepSetting>(
                type.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    private static PipelineStepSetting? NormalizePipelineStepSetting(PipelineStepSetting? setting)
    {
        var normalizedCliType = string.IsNullOrWhiteSpace(setting?.CliType)
            ? null
            : setting!.CliType!.Trim().ToLowerInvariant();
        var normalizedModel = string.IsNullOrWhiteSpace(setting?.Model) ? null : setting!.Model!.Trim();
        var normalizedThinkingLevel = string.IsNullOrWhiteSpace(setting?.ThinkingLevel)
            ? null
            : setting!.ThinkingLevel!.Trim().ToLowerInvariant();
        var normalizedMode = string.IsNullOrWhiteSpace(setting?.Mode)
            ? null
            : setting!.Mode!.Trim().ToLowerInvariant();
        var normalizedPrompt = string.IsNullOrWhiteSpace(setting?.Prompt) ? null : setting!.Prompt!.Trim();
        var normalizedCondition = NormalizeCondition(setting?.Condition);
        var isEmpty = setting is null
            || (setting.Enabled is null
                && setting.EconomyModel is null
                && setting.MaxIterations is null
                && normalizedMode is null
                && normalizedCliType is null
                && normalizedModel is null
                && normalizedThinkingLevel is null
                && normalizedPrompt is null
                && normalizedCondition is null);
        if (isEmpty) return null;
        return new PipelineStepSetting
        {
            Enabled = setting!.Enabled,
            EconomyModel = setting.EconomyModel,
            MaxIterations = setting.MaxIterations,
            Mode = normalizedMode,
            CliType = normalizedCliType,
            Model = normalizedModel,
            ThinkingLevel = normalizedThinkingLevel,
            Prompt = normalizedPrompt,
            PromptBaseDefaultSha = normalizedPrompt is null ? null : setting.PromptBaseDefaultSha,
            PromptBaseDefaultContent = normalizedPrompt is null ? null : setting.PromptBaseDefaultContent,
            Condition = normalizedCondition,
        };
    }

    private static IReadOnlyList<string> NormalizePipelineStepOrder(IReadOnlyList<string>? stepIds)
    {
        if (stepIds == null || stepIds.Count == 0) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var id in stepIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var trimmed = id.Trim();
            if (seen.Add(trimmed)) normalized.Add(trimmed);
        }
        return normalized;
    }

    /// <summary>
    /// Canonicalize a step condition for storage. A null/blank/unknown token,
    /// or an explicit <see cref="PipelineStepConditions.Always"/>, collapses to
    /// null ("no override, always run"). Value-bearing tokens keep a trimmed
    /// value; a value-bearing token with no value also collapses to null since
    /// it can never match.
    /// </summary>
    private static PipelineStepCondition? NormalizeCondition(PipelineStepCondition? condition)
    {
        var when = PipelineStepConditions.Normalize(condition?.When);
        if (when is null || when == PipelineStepConditions.Always) return null;

        var value = string.IsNullOrWhiteSpace(condition?.Value) ? null : condition!.Value!.Trim();
        if (PipelineStepConditions.RequiresValue(when) && value is null) return null;

        return new PipelineStepCondition { When = when, Value = PipelineStepConditions.RequiresValue(when) ? value : null };
    }

    /// <summary>
    /// Sets the per-project permission mode for one CLI
    /// (<see cref="ProjectSettings.CliModes"/>). A null / empty / unknown
    /// <paramref name="mode"/> clears the override so the CLI reverts to the
    /// platform default (YOLO) / global config. Invalid CLI ids are ignored.
    /// </summary>
    public void SetCliMode(string projectName, string cliType, string? mode)
    {
        if (!CliTypes.IsValid(cliType)) return;
        EnsureLoaded();
        var cli = CliTypes.Normalize(cliType);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var map = current.CliModes is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.CliModes, StringComparer.OrdinalIgnoreCase);

            // Empty/unknown mode clears the override (revert to default). A valid
            // mode is stored canonically so only known ids ever land on disk.
            if (string.IsNullOrWhiteSpace(mode) || !CliPermissionModes.IsValid(mode))
                map.Remove(cli);
            else
                map[cli] = CliPermissionModes.Normalize(mode);

            _cache[key] = current with { CliModes = map.Count == 0 ? null : map };
            Persist();
        }
        _logger.LogInformation("CLI permission mode for {Cli} set to {Mode} for project {Project}",
            cli, string.IsNullOrWhiteSpace(mode) ? "(default)" : CliPermissionModes.Normalize(mode), projectName);
    }

    /// <summary>
    /// Resolves the effective permission mode for one CLI in one project.
    /// Order: explicit per-project override → detected global CLI config →
    /// platform default (YOLO). The returned resolution carries the concrete
    /// flags the driver will inject, so callers (probe endpoint, UI) can show
    /// exactly what a spawn would do.
    /// </summary>
    public CliPermissionResolution ResolveCliMode(string projectName, string? cliType)
    {
        var cli = CliTypes.Normalize(cliType);
        var settings = Get(projectName);

        if (settings.CliModes != null
            && settings.CliModes.TryGetValue(cli, out var configured)
            && CliPermissionModes.IsValid(configured))
        {
            var mode = CliPermissionModes.Normalize(configured);
            return new CliPermissionResolution
            {
                CliType = cli,
                Mode = mode,
                Source = CliPermissionSources.Project,
                Args = CliPermissionFlags.For(cli, mode),
            };
        }

        var global = TryDetectGlobalMode(cli);
        if (global != null)
        {
            return new CliPermissionResolution
            {
                CliType = cli,
                Mode = global,
                Source = CliPermissionSources.Global,
                Args = CliPermissionFlags.For(cli, global),
            };
        }

        return new CliPermissionResolution
        {
            CliType = cli,
            Mode = CliPermissionModes.Yolo,
            Source = CliPermissionSources.Default,
            Args = CliPermissionFlags.For(cli, CliPermissionModes.Yolo),
        };
    }

    /// <summary>
    /// Sets the per-project context mode for one CLI
    /// (<see cref="ProjectSettings.CliContextModes"/>). A null / empty / unknown
    /// <paramref name="mode"/> clears the override so the CLI reverts to the
    /// platform default (CLEAN). Invalid CLI ids are ignored. T1b / ASS-1742.
    /// </summary>
    public void SetCliContextMode(string projectName, string cliType, string? mode)
    {
        if (!CliTypes.IsValid(cliType)) return;
        EnsureLoaded();
        var cli = CliTypes.Normalize(cliType);
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var map = current.CliContextModes is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.CliContextModes, StringComparer.OrdinalIgnoreCase);

            // Empty/unknown mode clears the override (revert to default CLEAN). A
            // valid mode is stored canonically so only known ids land on disk.
            if (string.IsNullOrWhiteSpace(mode) || !CliContextModes.IsValid(mode))
                map.Remove(cli);
            else
                map[cli] = CliContextModes.Normalize(mode);

            _cache[key] = current with { CliContextModes = map.Count == 0 ? null : map };
            Persist();
        }
        _logger.LogInformation("CLI context mode for {Cli} set to {Mode} for project {Project}",
            cli, string.IsNullOrWhiteSpace(mode) ? "(default)" : CliContextModes.Normalize(mode), projectName);
    }

    /// <summary>
    /// Resolves the effective context mode for one CLI in one project / task.
    /// Order: explicit per-task override → per-project override → platform
    /// default (CLEAN). The resolution carries whether the CLI can actually
    /// isolate persistent state (<see cref="CliContextModes.SupportsClean"/>);
    /// a clean selection on a shared-only CLI still resolves to clean here but
    /// the adapter runs it shared (and says so in the T1a panel). T1b / ASS-1742.
    /// </summary>
    public CliContextModeResolution ResolveContextMode(string projectName, string? cliType, string? taskOverride = null)
    {
        var cli = CliTypes.Normalize(cliType);
        var supported = CliContextModes.SupportsClean(cli);

        if (!string.IsNullOrWhiteSpace(taskOverride) && CliContextModes.IsValid(taskOverride))
            return new CliContextModeResolution
            {
                CliType = cli,
                Mode = CliContextModes.Normalize(taskOverride),
                Source = CliContextModeSources.Task,
                Supported = supported,
            };

        var settings = Get(projectName);
        if (settings.CliContextModes != null
            && settings.CliContextModes.TryGetValue(cli, out var configured)
            && CliContextModes.IsValid(configured))
            return new CliContextModeResolution
            {
                CliType = cli,
                Mode = CliContextModes.Normalize(configured),
                Source = CliContextModeSources.Project,
                Supported = supported,
            };

        return new CliContextModeResolution
        {
            CliType = cli,
            Mode = CliContextModes.Clean,
            Source = CliContextModeSources.Default,
            Supported = supported,
        };
    }

    private static readonly Regex CodexSandboxModeRegex = new(
        "sandbox_mode\\s*=\\s*\"(?<mode>[a-z-]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Best-effort detection of a CLI's persisted global permission mode.
    /// Only Codex stores a parseable mode (<c>sandbox_mode</c> in
    /// <c>~/.codex/config.toml</c>); the other CLIs keep no comparable
    /// file-based posture, so they return null and resolve to the default.
    /// Returns null when nothing is detected.
    /// </summary>
    private static string? TryDetectGlobalMode(string cli)
    {
        if (!string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            var configPath = Path.Combine(home, ".codex", "config.toml");
            if (!File.Exists(configPath)) return null;

            var match = CodexSandboxModeRegex.Match(File.ReadAllText(configPath));
            if (!match.Success) return null;

            return match.Groups["mode"].Value.ToLowerInvariant() switch
            {
                "danger-full-access" => CliPermissionModes.Yolo,
                "workspace-write" => CliPermissionModes.WorkspaceWrite,
                "read-only" => CliPermissionModes.ReadOnly,
                _ => null,
            };
        }
        catch
        {
            // A missing / unreadable / malformed global config is not an error:
            // we simply fall through to the platform default.
            return null;
        }
    }

    public void SetAnalysisSchedule(string projectName, string topic, string? cadence)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var s) ? s : new ProjectSettings();
            var map = current.AnalysisSchedules is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.AnalysisSchedules, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cadence))
            {
                map.Remove(topic.Trim());
            }
            else
            {
                map[topic.Trim()] = cadence.Trim();
            }
            _cache[key] = current with { AnalysisSchedules = map };
            Persist();
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            var path = ResolveStorePath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<Dictionary<string, ProjectSettings>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (doc != null)
                {
                    _cache = doc.ToDictionary(
                        kv => kv.Key,
                        kv => ProjectExecutionPolicy.Migrate(kv.Value),
                        StringComparer.OrdinalIgnoreCase);
                    if (MigrateLegacyPipelineSettings())
                    {
                        Persist();
                        _logger.LogInformation(
                            "Migrated legacy flat pipeline settings to task, bug, and feature types");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read project-settings.json — starting with defaults");
            }
        }
    }

    /// <summary>
    /// Move the former flat pipeline configuration into all three coding types.
    /// Planning is intentionally not populated. Typed values win when a
    /// partially migrated file contains both shapes.
    /// </summary>
    private bool MigrateLegacyPipelineSettings()
    {
        var changed = false;
        foreach (var project in _cache.Keys.ToList())
        {
            var current = _cache[project];
            if (current.PipelineSteps is null && current.PipelineStepOrder is null) continue;

            var stepsByType = ClonePipelineStepsByType(current.PipelineStepsByType);
            if (current.PipelineSteps is { Count: > 0 } legacySteps)
            {
                foreach (var type in PipelineTypes.LegacyCodingTypes)
                {
                    var merged = new Dictionary<string, PipelineStepSetting>(
                        legacySteps,
                        StringComparer.OrdinalIgnoreCase);
                    if (stepsByType.TryGetValue(type, out var typed))
                    {
                        foreach (var entry in typed)
                            merged[entry.Key] = entry.Value;
                    }
                    stepsByType[type] = merged;
                }
            }

            var orderByType = current.PipelineStepOrderByType is null
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, IReadOnlyList<string>>(
                    current.PipelineStepOrderByType,
                    StringComparer.OrdinalIgnoreCase);
            if (current.PipelineStepOrder is { Count: > 0 } legacyOrder)
            {
                foreach (var type in PipelineTypes.LegacyCodingTypes)
                    orderByType.TryAdd(type, legacyOrder.ToArray());
            }

            _cache[project] = current with
            {
                PipelineSteps = null,
                PipelineStepOrder = null,
                PipelineStepsByType = stepsByType.Count == 0 ? null : stepsByType,
                PipelineStepOrderByType = orderByType.Count == 0 ? null : orderByType,
            };
            changed = true;
        }
        return changed;
    }

    private void Persist()
    {
        var path = ResolveStorePath();
        if (path == null) return;
        try
        {
            _fileWriter.Write(path, SerializeCache());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write project-settings.json at {Path}", path);
        }
    }

    public void SetTestExecution(string projectName, TestExecutionPolicy? policy)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = ResolveAliasLocked(projectName);
            var current = _cache.TryGetValue(key, out var settings) ? settings : new ProjectSettings();
            _cache[key] = current with { TestExecution = NormalizeTestExecution(policy) };
            Persist();
        }
        _logger.LogInformation("Staged test policy {Action} for project {Project}",
            policy is null ? "cleared" : "updated", projectName);
    }

    private static TestExecutionPolicy? NormalizeTestExecution(TestExecutionPolicy? policy)
    {
        if (policy is null) return null;
        static IReadOnlyList<string>? Clean(IReadOnlyList<string>? values)
        {
            var cleaned = (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return cleaned.Count == 0 ? null : cleaned;
        }

        var laneLevels = (policy.LaneLevels ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key.Trim(), pair => TestExecutionLevels.Normalize(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        var rules = (policy.ImpactRules ?? [])
            .Select(rule => rule with
            {
                PathPrefixes = Clean(rule.PathPrefixes) ?? [],
                TestCommands = Clean(rule.TestCommands) ?? [],
                Reason = string.IsNullOrWhiteSpace(rule.Reason) ? null : rule.Reason.Trim(),
            })
            .Where(rule => rule.PathPrefixes.Count > 0 && rule.TestCommands.Count > 0)
            .ToList();
        return policy with
        {
            LaneLevels = laneLevels.Count == 0 ? null : laneLevels,
            ContinuousCommands = Clean(policy.ContinuousCommands),
            ImpactRules = rules.Count == 0 ? null : rules,
            TestHubHistoryPath = string.IsNullOrWhiteSpace(policy.TestHubHistoryPath)
                ? null : policy.TestHubHistoryPath.Trim(),
            LlmCliType = string.IsNullOrWhiteSpace(policy.LlmCliType) ? null : policy.LlmCliType.Trim(),
            LlmModel = string.IsNullOrWhiteSpace(policy.LlmModel) ? null : policy.LlmModel.Trim(),
            LlmThinkingLevel = string.IsNullOrWhiteSpace(policy.LlmThinkingLevel)
                ? null : policy.LlmThinkingLevel.Trim(),
        };
    }

    private void PersistStrict()
    {
        var path = ResolveStorePath();
        if (path == null) return;
        try
        {
            _fileWriter.Write(path, SerializeCache());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write project-settings.json at {Path}", path);
            throw new ProjectPersistenceException(
                $"Could not persist project settings at '{path}'.", ex);
        }
    }

    private string SerializeCache() =>
        JsonSerializer.Serialize(
            _cache.ToDictionary(
                kv => kv.Key,
                kv => ProjectExecutionPolicy.Migrate(kv.Value),
                StringComparer.OrdinalIgnoreCase),
            new JsonSerializerOptions { WriteIndented = true });

    private string ResolveAliasLocked(string projectName)
    {
        var current = projectName;
        for (var i = 0; i < 16 && _aliases.TryGetValue(current, out var target); i++)
        {
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) break;
            current = target;
        }
        return current;
    }

    private void UpdateAliasesAfterRenameLocked(string previous, string current)
    {
        if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return;

        _aliases.Remove(current);
        foreach (var alias in _aliases
                     .Where(pair => string.Equals(pair.Value, previous, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _aliases[alias] = current;
        }
        _aliases.Remove(previous);
        _aliases[previous] = current;
    }

    private string? ResolveStorePath()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo))
            return Path.Combine(taskRepo, "project-settings.json");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "agent-taskboard", "project-settings.json");
    }
}
