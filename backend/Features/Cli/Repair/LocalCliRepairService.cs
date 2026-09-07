using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Shared;

namespace AgentStudio.Cli;

public enum NpmCliInstallState
{
    Unsupported,
    TrulyUninstalled,
    PackagePresentWithShim,
    MissingShimWithPackagePresent,
    /// <summary>
    /// Package directory and command shim are present, but the package's
    /// launcher executable is still the placeholder the npm postinstall was
    /// supposed to replace with the real platform binary.
    /// </summary>
    LauncherStubWithPackagePresent,
}

public sealed record NpmCliInstallInspection(
    NpmCliInstallState State,
    string CliType,
    string PackageName,
    string PackageDirectory,
    string? PackageVersion,
    DateTimeOffset? PackageModifiedAt,
    string RequiredCommandShim,
    IReadOnlyList<string> ExpectedShims,
    string? LauncherBinary = null,
    long? LauncherBinaryLength = null,
    string? DetectionEvidence = null);

internal enum NpmCliRepairAction
{
    /// <summary>Global npm install or forced relink of the configured package.</summary>
    NpmInstall,
    /// <summary>Re-run of the installed package's own postinstall script.</summary>
    PackageInstallScript,
}

internal sealed record NpmCliRepairPlan(
    NpmGlobalInstallMode InstallMode,
    string Detection,
    string PackageState,
    string RepairAction,
    NpmCliRepairAction Action = NpmCliRepairAction.NpmInstall);

/// <summary>
/// Detects and repairs Windows global-npm failures where the configured package
/// is absent, its required command shim disappears, or its launcher binary is
/// still the postinstall placeholder. Custom CLI paths and present-but-broken
/// command shims remain outside this bounded repair.
/// </summary>
public sealed class LocalCliRepairService
{
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Anything below this is the launcher placeholder, not a platform binary
    /// (the observed Claude Code placeholder is 500 bytes, the real one is
    /// hundreds of megabytes).
    /// </summary>
    public const int LauncherStubMaxBytes = 4096;

    /// <summary>What a launcher package prints instead of its version.</summary>
    internal const string NativeBinaryMissingMarker = "native binary not installed";

    /// <summary>The npm postinstall entry point of a launcher package.</summary>
    internal const string PackageInstallScript = "install.cjs";

    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NpmGlobalInstaller _installer;
    private readonly ILogger<LocalCliRepairService> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<bool> _isWindows;
    private readonly Func<string?> _appData;
    private readonly Func<string?> _localAppData;
    private readonly string _journalPath;
    private readonly object _sync = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalCliRepairStatus> _latest =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalCliRepairService(
        NpmGlobalInstaller installer,
        IConfiguration configuration,
        ILogger<LocalCliRepairService> logger)
        : this(
            installer,
            logger,
            () => DateTimeOffset.UtcNow,
            OperatingSystem.IsWindows,
            () => Environment.GetEnvironmentVariable("APPDATA"),
            () => Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            ResolveJournalPath(configuration))
    {
    }

    internal LocalCliRepairService(
        NpmGlobalInstaller installer,
        ILogger<LocalCliRepairService> logger,
        Func<DateTimeOffset> clock,
        Func<bool> isWindows,
        Func<string?> appData,
        Func<string?> localAppData,
        string journalPath)
    {
        _installer = installer;
        _logger = logger;
        _clock = clock;
        _isWindows = isWindows;
        _appData = appData;
        _localAppData = localAppData;
        _journalPath = journalPath;
        foreach (var entry in ReadJournal())
        {
            if (entry.Outcome is "failed" or "detected")
            {
                _latest[entry.CliType] = ToStatus(entry);
            }
            else if (entry.Outcome is "repaired" or "resolved" or "healthy")
            {
                _latest.Remove(entry.CliType);
            }
        }
    }

    public IReadOnlyList<LocalCliRepairStatus> Current()
    {
        lock (_sync)
        {
            return _latest.Values
                .Where(item => item.Outcome is "failed" or "detected")
                .OrderByDescending(item => item.OccurredAt)
                .ToArray();
        }
    }

    /// <summary>
    /// Runs the ordinary availability probe and performs one bounded global
    /// npm install or relink for a recognized package/shim state. The returned
    /// probe is always the final observed state.
    /// </summary>
    public async Task<(bool Available, string? Version, string Path)> ProbeAndRepairAsync(
        string cliType,
        string? lastObservedVersion,
        Func<(bool Available, string? Version, string Path)> probe,
        CancellationToken ct)
    {
        var before = probe();
        if (before.Available)
        {
            ReconcileHealthy(cliType, before);
            return before;
        }
        if (!_isWindows()) return before;

        var appData = _appData();
        if (string.IsNullOrWhiteSpace(appData)) return before;
        var npmBin = Path.Combine(appData, "npm");
        var inspection = Inspect(cliType, before.Path, npmBin, before.Version);
        var repairPlan = SelectRepairPlan(inspection.State);
        if (inspection.State == NpmCliInstallState.LauncherStubWithPackagePresent)
            NoteLauncherStubDetected(cliType, inspection, before, lastObservedVersion);
        if (repairPlan is null)
        {
            _logger.LogDebug(
                "Local CLI probe unavailable cli={Cli} installState={InstallState}; no automatic repair",
                cliType,
                inspection.State);
            return before;
        }
        var shimStateBefore = File.Exists(inspection.RequiredCommandShim) ? "present" : "absent";

        var detectedAt = _clock();
        if (!TryBeginAttempt(cliType, detectedAt)) return before;

        try
        {
            var evidence = CaptureNpmActivity(inspection.PackageName, detectedAt);
            var beforeVersion = lastObservedVersion ?? inspection.PackageVersion;
            AppendJournal(new LocalCliRepairJournalEntry(
                detectedAt,
                cliType,
                "attempting",
                repairPlan.Detection,
                inspection.PackageName,
                inspection.PackageDirectory,
                inspection.PackageModifiedAt,
                before.Path,
                inspection.ExpectedShims,
                beforeVersion,
                null,
                null,
                $"Starting bounded {cliType} CLI repair: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction}.",
                "",
                "",
                evidence,
                inspection.DetectionEvidence));
            _logger.LogInformation(
                "Starting bounded local CLI repair cli={Cli} package={Package} packageVersion={Version} packageState={PackageState} shimStateBefore={ShimStateBefore} repairAction={RepairAction}",
                cliType,
                inspection.PackageName,
                inspection.PackageVersion ?? lastObservedVersion ?? "unknown",
                repairPlan.PackageState,
                shimStateBefore,
                repairPlan.RepairAction);

            var install = await ExecuteRepairAsync(inspection, repairPlan, ct);
            var after = probe();
            var afterInspection = Inspect(cliType, before.Path, npmBin, after.Version);
            var packagePresentAfter = Directory.Exists(inspection.PackageDirectory);
            var commandShimRestored = File.Exists(inspection.RequiredCommandShim);
            var succeeded = install.Succeeded
                            && packagePresentAfter
                            && commandShimRestored
                            && after.Available;
            var occurredAt = _clock();
            var detail = succeeded
                ? $"{cliType} CLI repaired: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} restored '{inspection.RequiredCommandShim}'; version {beforeVersion ?? "unknown"} -> {after.Version ?? "unknown"}."
                : BuildFailureDetail(
                    cliType,
                    install,
                    after,
                    inspection.RequiredCommandShim,
                    packagePresentAfter,
                    commandShimRestored,
                    repairPlan,
                    shimStateBefore);
            var entry = new LocalCliRepairJournalEntry(
                occurredAt,
                cliType,
                succeeded ? "repaired" : "failed",
                repairPlan.Detection,
                inspection.PackageName,
                inspection.PackageDirectory,
                inspection.PackageModifiedAt,
                before.Path,
                inspection.ExpectedShims,
                beforeVersion,
                after.Version,
                install.ExitCode,
                detail,
                Truncate(LogRedactor.Scrub(install.StandardOutput), 4000),
                Truncate(LogRedactor.Scrub(install.StandardError), 4000),
                evidence,
                inspection.DetectionEvidence);
            AppendJournal(entry);

            if (succeeded)
            {
                lock (_sync) _latest.Remove(cliType);
                _logger.LogInformation(
                    "Local CLI repaired cli={Cli} packageStateBefore={PackageStateBefore} packageStateAfter=present shimStateBefore={ShimStateBefore} shimStateAfter=present repairAction={RepairAction} repairedAt={RepairedAt:o} previousVersion={PreviousVersion} currentVersion={CurrentVersion}",
                    cliType,
                    repairPlan.PackageState,
                    shimStateBefore,
                    repairPlan.RepairAction,
                    occurredAt,
                    beforeVersion ?? "unknown",
                    after.Version ?? "unknown");
            }
            else
            {
                var status = ToStatus(entry);
                lock (_sync) _latest[cliType] = status;
                _logger.LogError(
                    "Local CLI repair failed cli={Cli} packageStateBefore={PackageStateBefore} packageStateAfter={PackageStateAfter} shimStateBefore={ShimStateBefore} shimStateAfter={ShimStateAfter} repairAction={RepairAction} postInstallState={PostInstallState} attemptedAt={AttemptedAt:o} npmExitCode={ExitCode} detail={Detail}",
                    cliType,
                    repairPlan.PackageState,
                    packagePresentAfter ? "present" : "absent",
                    shimStateBefore,
                    commandShimRestored ? "present" : "absent",
                    repairPlan.RepairAction,
                    afterInspection.State,
                    occurredAt,
                    install.ExitCode,
                    detail);
                if (after.Available) ReconcileHealthy(cliType, after);
            }
            return after;
        }
        finally
        {
            lock (_sync) _inFlight.Remove(cliType);
        }
    }

    /// <summary>
    /// Runs the repair the plan selected. A launcher stub is repaired by its own
    /// postinstall; the version-pinned global reinstall is the fallback when that
    /// script is gone or no node can run it.
    /// </summary>
    private async Task<NpmGlobalInstallResult> ExecuteRepairAsync(
        NpmCliInstallInspection inspection,
        NpmCliRepairPlan plan,
        CancellationToken ct)
    {
        if (plan.Action != NpmCliRepairAction.PackageInstallScript)
            return await _installer.InstallAsync(inspection.PackageName, plan.InstallMode, ct);

        if (File.Exists(Path.Combine(inspection.PackageDirectory, PackageInstallScript)))
        {
            var scripted = await _installer.RunPackageInstallScriptAsync(
                inspection.PackageDirectory,
                PackageInstallScript,
                ct);
            if (scripted.Outcome != NpmGlobalInstallOutcome.NodeUnavailable) return scripted;
        }

        var pinned = string.IsNullOrWhiteSpace(inspection.PackageVersion)
            ? inspection.PackageName
            : $"{inspection.PackageName}@{inspection.PackageVersion}";
        return await _installer.InstallAsync(pinned, plan.InstallMode, ct);
    }

    /// <summary>
    /// Publishes the launcher-stub state as an active failure before the repair
    /// runs, so the operator still sees the broken CLI while the one-attempt-per
    /// -hour budget suppresses another repair. A healthy probe clears it through
    /// <see cref="ReconcileHealthy"/>.
    /// </summary>
    private void NoteLauncherStubDetected(
        string cliType,
        NpmCliInstallInspection inspection,
        (bool Available, string? Version, string Path) probe,
        string? lastObservedVersion)
    {
        lock (_sync)
        {
            if (_latest.ContainsKey(cliType)) return;
        }

        var occurredAt = _clock();
        var entry = new LocalCliRepairJournalEntry(
            occurredAt,
            cliType,
            "detected",
            "launcher-stub-with-package-present",
            inspection.PackageName,
            inspection.PackageDirectory,
            inspection.PackageModifiedAt,
            probe.Path,
            inspection.ExpectedShims,
            lastObservedVersion ?? inspection.PackageVersion,
            null,
            null,
            $"{cliType} CLI launcher is still the postinstall placeholder: {inspection.DetectionEvidence}",
            "",
            "",
            [],
            inspection.DetectionEvidence);
        AppendJournal(entry);
        lock (_sync) _latest[cliType] = ToStatus(entry);
        _logger.LogError(
            "Local CLI launcher stub detected cli={Cli} package={Package} packageVersion={Version} launcher={Launcher} launcherBytes={LauncherBytes} detectedAt={DetectedAt:o} evidence={Evidence}",
            cliType,
            inspection.PackageName,
            inspection.PackageVersion ?? lastObservedVersion ?? "unknown",
            inspection.LauncherBinary ?? "unknown",
            inspection.LauncherBinaryLength,
            occurredAt,
            inspection.DetectionEvidence);
    }

    private void ReconcileHealthy(
        string cliType,
        (bool Available, string? Version, string Path) probe)
    {
        LocalCliRepairStatus? staleStatus;
        lock (_sync) _latest.TryGetValue(cliType, out staleStatus);
        if (staleStatus is null) return;

        var occurredAt = _clock();
        AppendJournal(new LocalCliRepairJournalEntry(
            occurredAt,
            cliType,
            "resolved",
            "healthy-probe",
            "",
            "",
            null,
            probe.Path,
            [],
            staleStatus.VersionAfter ?? staleStatus.VersionBefore,
            probe.Version,
            null,
            $"{cliType} CLI probes healthy; cleared stale {staleStatus.Outcome} repair status.",
            "",
            "",
            []));
        lock (_sync)
        {
            if (_latest.TryGetValue(cliType, out var current) && current == staleStatus)
                _latest.Remove(cliType);
        }
        _logger.LogInformation(
            "Local CLI healthy; cleared stale repair status cli={Cli} previousOutcome={PreviousOutcome} healthyAt={HealthyAt:o} version={Version}",
            cliType,
            staleStatus.Outcome,
            occurredAt,
            probe.Version ?? "unknown");
    }

    /// <summary>
    /// Classifies one global npm CLI install. <paramref name="versionOutput"/> is
    /// the text the failing <c>--version</c> probe produced, if the caller
    /// captured any; a launcher package reports its missing native binary there.
    /// </summary>
    public static NpmCliInstallInspection Inspect(
        string cliType,
        string probedPath,
        string npmBin,
        string? versionOutput = null)
    {
        var definition = Definition(cliType);
        if (definition is null || !IsGlobalCommandPath(cliType, probedPath, npmBin))
        {
            return new NpmCliInstallInspection(
                NpmCliInstallState.Unsupported,
                cliType,
                definition?.PackageName ?? "",
                "",
                null,
                null,
                "",
                []);
        }

        var resolvedDefinition = definition.Value;
        var packageDirectory = Path.Combine(
            npmBin,
            "node_modules",
            resolvedDefinition.Scope,
            resolvedDefinition.Package);
        var expectedShims = new[]
        {
            Path.Combine(npmBin, cliType),
            Path.Combine(npmBin, cliType + ".cmd"),
            Path.Combine(npmBin, cliType + ".ps1"),
            Path.Combine(npmBin, cliType + ".exe"),
        };
        var requiredCommandShim = Path.Combine(npmBin, cliType + ".cmd");
        var packagePresent = Directory.Exists(packageDirectory);
        var shimPresent = File.Exists(requiredCommandShim);
        var launcher = packagePresent ? ResolveLauncherBinary(packageDirectory, cliType) : null;
        var launcherLength = launcher is null ? null : SafeLength(launcher);
        var launcherIsStub = LauncherLooksLikeStub(launcherLength, versionOutput);
        var state = !packagePresent
            ? NpmCliInstallState.TrulyUninstalled
            : !shimPresent
                ? NpmCliInstallState.MissingShimWithPackagePresent
                : launcherIsStub
                    ? NpmCliInstallState.LauncherStubWithPackagePresent
                    : NpmCliInstallState.PackagePresentWithShim;
        var packageJson = Path.Combine(packageDirectory, "package.json");
        return new NpmCliInstallInspection(
            state,
            cliType,
            resolvedDefinition.PackageName,
            packageDirectory,
            ReadPackageVersion(packageJson),
            File.Exists(packageJson) ? SafeLastWrite(packageJson) : null,
            requiredCommandShim,
            expectedShims,
            launcher,
            launcherLength,
            state == NpmCliInstallState.LauncherStubWithPackagePresent
                ? DescribeLauncherStub(
                    packageDirectory,
                    resolvedDefinition,
                    launcher,
                    launcherLength,
                    versionOutput)
                : null);
    }

    /// <summary>
    /// A launcher package ships a placeholder of a few hundred bytes and lets its
    /// postinstall replace it with the real platform binary. Only native launcher
    /// targets are size-checked, so an ordinary small JavaScript bin entry can
    /// never be mistaken for a stub.
    /// </summary>
    internal static bool LauncherLooksLikeStub(long? launcherLength, string? versionOutput)
        => (launcherLength is not null && launcherLength < LauncherStubMaxBytes)
           || (versionOutput?.Contains(NativeBinaryMissingMarker, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string? ResolveLauncherBinary(string packageDirectory, string cliType)
    {
        foreach (var relative in LauncherBinaryCandidates(packageDirectory, cliType))
        {
            if (!relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate;
            try { candidate = Path.Combine(packageDirectory, relative); }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "LocalCliRepairService: unusable launcher bin entry");
                continue;
            }
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> LauncherBinaryCandidates(string packageDirectory, string cliType)
    {
        var declared = ReadPackageBinEntry(Path.Combine(packageDirectory, "package.json"), cliType);
        if (!string.IsNullOrWhiteSpace(declared)) yield return declared!;
        yield return Path.Combine("bin", cliType + ".exe");
    }

    private static string DescribeLauncherStub(
        string packageDirectory,
        (string Scope, string Package, string PackageName) definition,
        string? launcher,
        long? launcherLength,
        string? versionOutput)
    {
        var parts = new List<string>();
        if (launcher is not null)
        {
            parts.Add(
                $"launcher '{launcher}' is {launcherLength} bytes (below the {LauncherStubMaxBytes}-byte placeholder threshold)");
        }
        if (versionOutput?.Contains(NativeBinaryMissingMarker, StringComparison.OrdinalIgnoreCase) == true)
            parts.Add($"--version reported '{NativeBinaryMissingMarker}'");
        var nativePackages = NestedNativePackages(packageDirectory, definition);
        parts.Add(nativePackages.Count > 0
            ? $"nested native package(s) present: {string.Join(", ", nativePackages)}"
            : "no nested native package found");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// The optional platform dependency the postinstall copies from, extracted
    /// below the package itself (<c>node_modules/@scope/package-win32-x64</c>).
    /// </summary>
    private static IReadOnlyList<string> NestedNativePackages(
        string packageDirectory,
        (string Scope, string Package, string PackageName) definition)
    {
        var scopeDirectory = Path.Combine(packageDirectory, "node_modules", definition.Scope);
        if (!Directory.Exists(scopeDirectory)) return [];
        try
        {
            return Directory.EnumerateDirectories(scopeDirectory)
                .Select(Path.GetFileName)
                .Where(name => name is not null
                               && name.StartsWith(definition.Package + "-", StringComparison.OrdinalIgnoreCase))
                .Select(name => $"{definition.Scope}/{name}")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalCliRepairService: nested native package enumeration failed");
            return [];
        }
    }

    public static bool AttemptAllowed(
        DateTimeOffset now,
        DateTimeOffset? previousAttempt,
        TimeSpan? window = null)
        => previousAttempt is null || now - previousAttempt.Value >= (window ?? AttemptWindow);

    internal static NpmCliRepairPlan? SelectRepairPlan(NpmCliInstallState state)
        => state switch
        {
            NpmCliInstallState.TrulyUninstalled => new NpmCliRepairPlan(
                NpmGlobalInstallMode.Install,
                "package-missing",
                "absent",
                "install"),
            NpmCliInstallState.MissingShimWithPackagePresent => new NpmCliRepairPlan(
                NpmGlobalInstallMode.ForceRelink,
                "missing-shim-with-package-present",
                "present",
                "force-relink"),
            // The npm postinstall is the only step that placed the platform
            // binary, so re-running it is the smallest repair. InstallMode
            // carries the version-pinned reinstall used when the script is gone.
            NpmCliInstallState.LauncherStubWithPackagePresent => new NpmCliRepairPlan(
                NpmGlobalInstallMode.Install,
                "launcher-stub-with-package-present",
                "present",
                "postinstall-rerun",
                NpmCliRepairAction.PackageInstallScript),
            _ => null,
        };

    private bool TryBeginAttempt(string cliType, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_inFlight.Add(cliType)) return false;
            // Only the "attempting" row marks a spent attempt. A detection or a
            // resolved row must not consume the budget, or a state that is
            // journalled on sight could never be repaired.
            var previous = ReadJournal()
                .Where(entry => string.Equals(entry.CliType, cliType, StringComparison.OrdinalIgnoreCase)
                                && entry.Outcome == "attempting")
                .Select(entry => (DateTimeOffset?)entry.Timestamp)
                .LastOrDefault();
            if (AttemptAllowed(now, previous)) return true;
            _inFlight.Remove(cliType);
            _logger.LogInformation(
                "Local CLI repair suppressed by one-hour attempt budget cli={Cli} previousAttempt={PreviousAttempt:o}",
                cliType,
                previous);
            return false;
        }
    }

    private IReadOnlyList<NpmLogEvidence> CaptureNpmActivity(string packageName, DateTimeOffset detectedAt)
    {
        var roots = new[] { _localAppData(), _appData() }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.Combine(value!, "npm-cache", "_logs"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<NpmLogEvidence>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*-debug-*.log"); }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "LocalCliRepairService: npm debug-log enumeration failed");
                continue;
            }
            foreach (var file in files
                         .Select(path => (Path: path, Modified: SafeLastWrite(path)))
                         .Where(item => item.Modified >= detectedAt - TimeSpan.FromHours(2)
                                        && item.Modified <= detectedAt + TimeSpan.FromMinutes(5))
                         .OrderByDescending(item => item.Modified)
                         .Take(6))
            {
                var matchingLines = ReadNpmEvidenceLines(file.Path, packageName);
                result.Add(new NpmLogEvidence(
                    Path.GetFileName(file.Path),
                    file.Modified,
                    matchingLines));
            }
        }
        return result;
    }

    private static IReadOnlyList<string> ReadNpmEvidenceLines(string path, string packageName)
    {
        try
        {
            return File.ReadLines(path)
                .Where(line => line.Contains(packageName, StringComparison.OrdinalIgnoreCase)
                               || line.Contains(" verbose argv ", StringComparison.OrdinalIgnoreCase)
                               || line.Contains(" command ", StringComparison.OrdinalIgnoreCase)
                               || line.Contains(" update ", StringComparison.OrdinalIgnoreCase))
                .Take(16)
                .Select(line => Truncate(LogRedactor.Scrub(line.Trim()), 1000))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void AppendJournal(LocalCliRepairJournalEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(entry, JournalJson);
            lock (_sync) File.AppendAllText(_journalPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append local CLI repair journal at {Path}", _journalPath);
        }
    }

    private IReadOnlyList<LocalCliRepairJournalEntry> ReadJournal()
    {
        if (!File.Exists(_journalPath)) return [];
        try
        {
            var entries = new List<LocalCliRepairJournalEntry>();
            foreach (var line in File.ReadLines(_journalPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<LocalCliRepairJournalEntry>(line,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry is not null) entries.Add(entry);
                }
                catch (Exception ex)
                {
                    SilentCatch.Note(ex, "LocalCliRepairService: skipping torn CLI repair journal row");
                }
            }
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local CLI repair journal at {Path}", _journalPath);
            return [];
        }
    }

    private static string ResolveJournalPath(IConfiguration configuration)
    {
        var taskRepository = configuration["TaskRepository"];
        return !string.IsNullOrWhiteSpace(taskRepository)
            ? Path.Combine(taskRepository, "logs", "cli-self-heal.jsonl")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "cli-self-heal.jsonl");
    }

    private static string BuildFailureDetail(
        string cliType,
        NpmGlobalInstallResult install,
        (bool Available, string? Version, string Path) after,
        string requiredCommandShim,
        bool packagePresentAfter,
        bool commandShimRestored,
        NpmCliRepairPlan repairPlan,
        string shimStateBefore)
    {
        if (install.Outcome == NpmGlobalInstallOutcome.NpmUnavailable)
            return $"{cliType} CLI repair failed: npm unavailable. {install.StandardError}";
        if (install.Outcome == NpmGlobalInstallOutcome.NodeUnavailable)
            return $"{cliType} CLI repair failed: node unavailable for {repairPlan.RepairAction}. {install.StandardError}";
        if (!install.Succeeded)
            return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} attempted, but npm exited {install.ExitCode?.ToString() ?? "without an exit code"}.";
        if (!packagePresentAfter)
            return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} attempted, but the package is still absent.";
        if (!commandShimRestored)
            return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} attempted, but required shim '{requiredCommandShim}' is still absent.";
        return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, npm action {repairPlan.RepairAction} restored the command shim, but --version still failed at '{after.Path}'.";
    }

    private static LocalCliRepairStatus ToStatus(LocalCliRepairJournalEntry entry)
        => new()
        {
            CliType = entry.CliType,
            Outcome = entry.Outcome,
            OccurredAt = entry.Timestamp,
            VersionBefore = entry.VersionBefore,
            VersionAfter = entry.VersionAfter,
            Detail = entry.Detail,
        };

    private static bool IsGlobalCommandPath(string cliType, string probedPath, string npmBin)
    {
        if (string.IsNullOrWhiteSpace(probedPath)) return false;
        var name = Path.GetFileNameWithoutExtension(probedPath);
        if (!string.Equals(name, cliType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Path.IsPathRooted(probedPath)) return true;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(probedPath) ?? ""),
            Path.TrimEndingDirectorySeparator(npmBin),
            StringComparison.OrdinalIgnoreCase);
    }

    private static (string Scope, string Package, string PackageName)? Definition(string cliType)
        => cliType.ToLowerInvariant() switch
        {
            CliTypes.Claude => ("@anthropic-ai", "claude-code", "@anthropic-ai/claude-code"),
            CliTypes.Codex => ("@openai", "codex", "@openai/codex"),
            _ => null,
        };

    private static string? ReadPackageVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Reads the <c>bin</c> target a package declares for one command.</summary>
    private static string? ReadPackageBinEntry(string packageJsonPath, string cliType)
    {
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("bin", out var bin)) return null;
            return bin.ValueKind switch
            {
                JsonValueKind.String => bin.GetString(),
                JsonValueKind.Object => bin.TryGetProperty(cliType, out var target)
                    ? target.GetString()
                    : null,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static long? SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTimeOffset.MinValue; }
    }

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit] + "...";

}

public sealed record NpmLogEvidence(
    string FileName,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string> RelevantLines);

public sealed record LocalCliRepairJournalEntry(
    DateTimeOffset Timestamp,
    string CliType,
    string Outcome,
    string Detection,
    string PackageName,
    string PackageDirectory,
    DateTimeOffset? PackageModifiedAt,
    string ProbedPath,
    IReadOnlyList<string> ExpectedShims,
    string? VersionBefore,
    string? VersionAfter,
    int? NpmExitCode,
    string Detail,
    string NpmStandardOutput,
    string NpmStandardError,
    IReadOnlyList<NpmLogEvidence> RecentNpmActivity,
    /// <summary>What the state classification was read from, for states that are
    /// not visible from the package and shim paths alone.</summary>
    string? DetectionEvidence = null);
