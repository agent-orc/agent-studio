using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal static class RunnerCapabilityProbe
{
    public static IReadOnlyList<AdvertisedCapabilityDto> Advertise(
        RunnerOptions options,
        bool gitPushReady,
        bool gitWorkflowPushReady = true,
        string? gitDetail = null,
        ProviderAuthProbe? providerAuth = null,
        TaskServerConnectivitySnapshot? connectivity = null)
    {
        var list = new List<AdvertisedCapabilityDto>
        {
            Capability(
                options.Role == "review"
                    ? CapabilityProtocol.ReviewExecutor
                    : options.Role == "gate" ? GateCapabilities.Executor : CapabilityProtocol.CodingExecutor,
                "executor",
                typeof(RunnerCapabilityProbe).Assembly.GetName().Version?.ToString(),
                options.Role),
            Capability(CapabilityProtocol.GitFetch, "source", ToolVersion("git"), "git",
                (options.Role == "gate" || options.IsWorkstation) && !OnPath("git")
                    ? "unavailable" : "ready"),
            Capability(CapabilityProtocol.RepositoryAccess, "source", null, options.GitRemote ?? "server-routed"),
            Capability(CapabilityProtocol.Disk, "foundation", null, Path.GetPathRoot(options.WorkDir)),
            Capability(
                CapabilityProtocol.TaskServerConnectivity,
                "foundation",
                null,
                new Uri(options.ServerUrl).Authority,
                connectivity?.Status == TaskServerConnectivityStates.Unreachable ? "unavailable" : "ready",
                ConnectivityDetail(connectivity)),
            Capability($"platform:{Platform()}", "platform", RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString()),
            Capability($"platform:{PlatformClass()}", "platform", RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString()),
        };
        if (OperatingSystem.IsWindows())
        {
            AddToolchain(list, "toolchain:msbuild", "msbuild");
            AddToolchain(list, "toolchain:vswhere", "vswhere");
            AddToolchain(list, "toolchain:pwsh", "pwsh");
            AddToolchain(list, "toolchain:powershell", "powershell");
        }
        if (options.IsWorkstation)
        {
            list.Add(Capability("host-class:workstation", "host-class", null, options.Hostname));
            foreach (var root in options.WorkstationRepositoryRoots)
                list.Add(Capability(root.CapabilityKey, "local-repository", null, root.Name,
                    WorkstationProfile.RootAvailable(root) ? "ready" : "unavailable"));
            foreach (var tool in options.WorkstationRequiredTools)
            {
                if (list.Any(capability => capability.Key == $"toolchain:{tool}")) continue;
                list.Add(Capability($"toolchain:{tool}", "toolchain", ToolVersion(tool), tool,
                    OnPath(tool) ? "ready" : "unavailable"));
            }
            if (options.WorkstationPreview is { } preview)
                list.Add(Capability(preview.CapabilityKey, "preview", null,
                    preview.Origin.GetLeftPart(UriPartial.Authority),
                    detail: $"reachableFrom={preview.Reachability}; maxLifetimeSeconds={preview.MaxLifetimeSeconds}"));
        }
        if (options.Role == "coding")
        {
            // The advertised status is the only lever that keeps a card away from
            // a host whose provider login is gone: the task server admits a claim
            // only while every required capability reads exactly "ready"
            // (task-server/TaskServerCapabilityStore.cs:506). Reporting mere PATH
            // presence as "ready" is what burns cards after an expired token, so
            // this asks the CLI itself - see docs/operations/token-refresh-ohne-tunnel.md.
            AddCodingCliCapabilities(
                list,
                options,
                providerAuth ?? ProviderAuthProbe.Shared);
            list.Add(Capability(
                CapabilityProtocol.GitPush,
                "source",
                ToolVersion("git"),
                options.GitPushRemote ?? options.GitRemote ?? "server-routed",
                gitPushReady ? "ready" : "unavailable",
                gitDetail));
            list.Add(Capability(
                CapabilityProtocol.GitWorkflowPush,
                "source",
                ToolVersion("git"),
                options.GitPushRemote ?? options.GitRemote ?? "server-routed",
                !gitPushReady
                    ? "unavailable"
                    : gitWorkflowPushReady
                        ? GitPushProbe.Ready
                        : GitPushProbe.ReadyNoWorkflowScope,
                gitDetail));
            // T1 canary mechanism (car-migration-plan §4): a CAR-engined host says
            // so, and the canary cards request exactly this key through their
            // RequiredCapabilities - cohorts 1 -> 5 -> default, no special path.
            if (options.ExecEngine == RunnerOptions.ExecEngineCar)
            {
                list.Add(Capability(
                    "exec-engine:car",
                    "executor",
                    typeof(CodingAgentRunner.CliRunner).Assembly.GetName().Version?.ToString(),
                    options.ExecEngine));
            }
        }
        else if (options.Role == "review")
        {
            AddCodingCliCapabilities(
                list,
                options,
                providerAuth ?? ProviderAuthProbe.Shared);
            list.Add(Capability(CapabilityProtocol.Vision, "review", null, "remote-review"));
            list.Add(Capability(ReviewCapabilities.SemanticReview, "review", null, "remote-review"));
            list.Add(Capability(ReviewCapabilities.GitMaterialization, "review", ToolVersion("git"), "git"));
            list.Add(Capability(ReviewCapabilities.SourceBundleMaterialization, "review", null, "artifact"));
            list.Add(Capability(ReviewCapabilities.BaselineComparison, "review", null, "merge-base"));
            list.Add(Capability(ReviewCapabilities.DependencyPreparation, "review", null, "build-profile"));
            list.Add(Capability(ReviewCapabilities.LibraryStepV1, "review", "1", "review-library"));
        }
        else
        {
            list.Add(Capability(GateCapabilities.GitMaterialization, "gate", ToolVersion("git"), "git",
                OnPath("git") ? "ready" : "unavailable"));
            list.Add(Capability(GateCapabilities.BundleMaterialization, "gate", null, "artifact"));
        }
        foreach (var requirement in ReviewLibraryStepPolicy.ToolchainRequirements)
            AddToolchain(list, requirement.Key, requirement.ProbeExecutable);
        AddComposeRender(list, ComposeRenderVersion);
        return list;
    }

    public static IReadOnlyList<string> CodingRequirements(RunnerOptions options)
        => new[]
        {
            CapabilityProtocol.CodingExecutor,
            CapabilityProtocol.CliExecution(AgentCliProcess.ConfiguredCliType(options)),
            CapabilityProtocol.ProviderAuthentication(AgentCliProcess.ConfiguredCliType(options)),
            CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.Disk,
            CapabilityProtocol.TaskServerConnectivity,
        }
        .Concat(options.RequiredCapabilities)
        .Concat(options.IsWorkstation
            ? options.WorkstationRequiredTools.Select(tool => $"toolchain:{tool}")
            : [])
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Host-wide requirements for the legacy card-selecting claim plane. The
    /// server adds the candidate card's CLI execution and authentication keys,
    /// so a mixed host is not accidentally forced through its configured
    /// default provider when it claims a card for its other CLI.
    /// </summary>
    public static IReadOnlyList<string> CodingHostRequirements(RunnerOptions options)
        => new[]
        {
            CapabilityProtocol.CodingExecutor,
            CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.Disk,
            CapabilityProtocol.TaskServerConnectivity,
        }
        .Concat(options.RequiredCapabilities)
        .Concat(options.IsWorkstation
            ? options.WorkstationRequiredTools.Select(tool => $"toolchain:{tool}")
            : [])
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> ReviewRequirements(RunnerOptions options)
        => new[]
        {
            CapabilityProtocol.ReviewExecutor,
            CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.Disk,
            CapabilityProtocol.TaskServerConnectivity,
            ReviewCapabilities.SemanticReview,
            ReviewCapabilities.BaselineComparison,
            ReviewCapabilities.DependencyPreparation,
            ReviewCapabilities.LibraryStepV1,
        }
        .Concat(options.RequiredCapabilities)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// The review-claim identity. Both Task Server implementations match a
    /// sealed library-v1 plan against THIS set, not against the minutely
    /// advertisement, so it carries every toolchain key the review library can
    /// require whose executable the host probe finds (AGT-2987). Before that,
    /// toolchain keys lived only in the advertisement and every dotnet or npm
    /// plan was silently unclaimable. <c>RUNNER_REQUIRED_CAPABILITIES</c> stays
    /// an additive override through <see cref="ReviewRequirements"/>.
    /// </summary>
    public static IReadOnlyList<string> ReviewRegistrationCapabilities(
        RunnerOptions options,
        Func<string, bool>? onPath = null,
        Func<string?>? composeRenderVersion = null)
        => ReviewRequirements(options)
            .Concat(new[]
            {
                ReviewCapabilities.ReviewExecutor,
                ReviewCapabilities.GitMaterialization,
                ReviewCapabilities.SourceBundleMaterialization,
                ReviewCapabilities.VisionReview,
            })
            .Concat(CodingCliBinaries(options).SelectMany(item => new[]
            {
                CapabilityProtocol.CliExecution(item.CliType),
                CapabilityProtocol.ProviderAuthentication(item.CliType),
            }))
            .Concat(ReviewToolchainCapabilities(onPath ?? OnPath))
            .Concat((composeRenderVersion ?? ComposeRenderVersion)() is not null
                ? [CapabilityProtocol.ComposeRender]
                : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Review-library toolchain keys whose probe executable is present on this host.</summary>
    public static IReadOnlyList<string> ReviewToolchainCapabilities(Func<string, bool> onPath)
        => ReviewLibraryStepPolicy.ToolchainRequirements
            .Where(requirement => onPath(requirement.ProbeExecutable))
            .Select(requirement => requirement.Key)
            .ToArray();

    public static IReadOnlyList<string> GateRegistrationCapabilities(RunnerOptions options)
        => new[]
        {
            GateCapabilities.Executor, GateCapabilities.GitMaterialization,
            GateCapabilities.BundleMaterialization, CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess, CapabilityProtocol.Disk,
            CapabilityProtocol.TaskServerConnectivity,
        }.Concat(options.RequiredCapabilities).Distinct(StringComparer.Ordinal).ToArray();

    public static HostTelemetrySnapshotDto? Telemetry(
        HostTelemetrySample? sample,
        ReviewPlaneBudgetDto? reviewPlane = null)
        => sample is null
            ? null
            : new HostTelemetrySnapshotDto(
                sample.Timestamp,
                sample.CpuPercent,
                sample.Load1,
                sample.Load5,
                sample.Load15,
                sample.MemoryUsedBytes,
                sample.MemoryTotalBytes,
                sample.SwapInBytesPerSecond,
                sample.SwapOutBytesPerSecond,
                sample.CpuStealPercent,
                sample.IoWaitPercent,
                sample.CpuCores,
                sample.ActiveSlots,
                DiskFreeBytes(),
                DiskTotalBytes(),
                sample.TaskServerConnectionStatus,
                sample.TaskServerConnectionObservedAt,
                sample.TaskServerConnectionFailureStartedAt,
                sample.TaskServerConnectionConsecutiveFailures,
                sample.TaskServerConnectionEscalatedAt,
                sample.TaskServerConnectionLastError,
                sample.TaskServerConnectionLastRecoveredAt,
                CliProcessReaper.ReapedCount,
                reviewPlane);

    private static string ConnectivityDetail(TaskServerConnectivitySnapshot? connectivity)
    {
        if (connectivity is null || connectivity.Status == TaskServerConnectivityStates.Unknown)
            return "Task Server route has not completed its first observed request yet.";
        if (connectivity.Status == TaskServerConnectivityStates.Reachable)
            return $"Task Server route reachable; observed {connectivity.ObservedAt:o}.";
        return $"Task Server route unavailable since {connectivity.FailureStartedAt:o}; " +
               $"{connectivity.ConsecutiveFailures} consecutive request failures. " +
               (connectivity.LastError ?? "No transport detail was captured.");
    }

    public static string Provider(string cliBinary)
    {
        var name = Path.GetFileNameWithoutExtension(cliBinary).Trim().ToLowerInvariant();
        return name.Length == 0 ? "unknown" : name;
    }

    public static bool IsProviderAuthenticationFailure(ProcessResult result)
    {
        if (result.ExitCode == 0) return false;
        return ProviderAccessClassifier.Classify(
                result.ExitCode,
                result.StdOut,
                result.StdErr)
            .Kind == ProviderAccessEvidenceKind.AuthenticationFailure;
    }

    private static void AddToolchain(
        ICollection<AdvertisedCapabilityDto> capabilities,
        string key,
        string executable)
    {
        if (capabilities.Any(capability => capability.Key == key)) return;
        if (!OnPath(executable)) return;
        capabilities.Add(Capability(key, "toolchain", ToolVersion(executable), executable));
    }

    /// <summary>
    /// AGT-2981: advertise <see cref="CapabilityProtocol.ComposeRender"/> only
    /// when <c>docker compose version</c> answers. A bare <c>docker</c> on PATH
    /// does not prove the compose plugin that <c>docker compose config</c>
    /// needs; no daemon is contacted.
    /// </summary>
    internal static void AddComposeRender(
        ICollection<AdvertisedCapabilityDto> capabilities,
        Func<string?> composeVersion)
    {
        var version = composeVersion();
        if (version is null) return;
        capabilities.Add(Capability(
            CapabilityProtocol.ComposeRender, "toolchain", version, "docker compose"));
    }

    private static readonly TimeSpan ComposeRenderProbeTtl = TimeSpan.FromMinutes(10);
    private static (DateTime ObservedAt, string? Version)? _composeRenderProbe;

    /// <summary>
    /// The compose plugin version, or null when this host cannot render
    /// Compose. Cached for ten minutes so a heartbeat does not spawn Docker.
    /// </summary>
    private static string? ComposeRenderVersion()
    {
        var cached = _composeRenderProbe;
        if (cached is { } hit && DateTime.UtcNow - hit.ObservedAt < ComposeRenderProbeTtl)
            return hit.Version;
        var version = ProbeComposeVersion();
        _composeRenderProbe = (DateTime.UtcNow, version);
        return version;
    }

    private static string? ProbeComposeVersion()
    {
        var docker = ResolveExecutable("docker");
        if (docker is null) return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = docker,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.StartInfo.ArgumentList.Add("compose");
            process.StartInfo.ArgumentList.Add("version");
            process.StartInfo.ArgumentList.Add("--short");
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            Task.WaitAll([stdout, stderr], 1_000);
            if (process.ExitCode != 0) return null;
            var version = stdout.Result.Trim();
            return version.Length == 0 ? "available" : version;
        }
        catch
        {
            return null;
        }
    }

    private static void AddCodingCliCapabilities(
        ICollection<AdvertisedCapabilityDto> capabilities,
        RunnerOptions options,
        ProviderAuthProbe providerAuth)
    {
        foreach (var (cliType, binary) in CodingCliBinaries(options))
        {
            var auth = providerAuth.Current(binary);
            var binaryAvailable = ProviderAuthProbe.ExecutableExists(binary);
            var installation = binaryAvailable ? InspectCli(binary) : null;
            var supportedModels = string.Equals(cliType, AgentCliProcess.CodexCli, StringComparison.OrdinalIgnoreCase)
                && installation is not null
                ? CodexModels(installation)
                : null;
            capabilities.Add(Capability(
                CapabilityProtocol.CliExecution(cliType),
                "cli-execution",
                installation?.Version,
                installation?.Path ?? binary,
                binaryAvailable ? ProviderAuthProbe.Ready : ProviderAuthProbe.Unavailable,
                binaryAvailable
                    ? $"CLI binary '{binary}' is available for {cliType} cards."
                    : $"CLI binary '{binary}' was not found; {cliType} cards cannot execute.",
                supportedModels: supportedModels));
            capabilities.Add(Capability(
                CapabilityProtocol.ProviderAuthentication(cliType),
                "provider-auth",
                binaryAvailable ? "available" : null,
                cliType,
                auth.Status,
                auth.Detail,
                auth.Signal,
                auth.ExpiresAt,
                auth.LimitedUntil,
                auth.CredentialModifiedAt,
                auth.EvidenceId,
                auth.EvidenceExcerpt,
                credentialGeneration: auth.CredentialGeneration,
                credentialObservedAt: auth.ObservedAt,
                lastRealSuccessAt: auth.LastRealSuccessAt,
                expiryProvenance: auth.ExpiryProvenance,
                accessTokenExpiresAt: auth.AccessTokenExpiresAt,
                effectiveSource: auth.EffectiveSource,
                nativeFileShadowed: auth.NativeFileShadowed,
                evidenceRefs: auth.EvidenceId is { } reference &&
                    reference.StartsWith("evidence:", StringComparison.Ordinal)
                        ? [reference] : []));
        }
    }

    internal static IReadOnlyList<(string CliType, string Binary)> CodingCliBinaries(
        RunnerOptions options)
    {
        var configuredType = AgentCliProcess.ConfiguredCliType(options);
        var binaries = new List<(string CliType, string Binary)>
        {
            (configuredType, options.CliBin),
        };
        if (configuredType != AgentCliProcess.ClaudeCli
            && !string.IsNullOrWhiteSpace(options.ClaudeCliBin))
        {
            binaries.Add((AgentCliProcess.ClaudeCli, options.ClaudeCliBin));
        }
        if (configuredType != AgentCliProcess.CodexCli
            && !string.IsNullOrWhiteSpace(options.CodexCliBin))
        {
            binaries.Add((AgentCliProcess.CodexCli, options.CodexCliBin));
        }
        return binaries;
    }

    private static AdvertisedCapabilityDto Capability(
        string key,
        string category,
        string? version,
        string? identity,
        string status = "ready",
        string? detail = null,
        string? signal = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? limitedUntil = null,
        DateTimeOffset? credentialModifiedAt = null,
        string? evidenceId = null,
        string? evidenceExcerpt = null,
        IReadOnlyList<string>? supportedModels = null,
        string? credentialGeneration = null,
        DateTimeOffset? credentialObservedAt = null,
        DateTimeOffset? lastRealSuccessAt = null,
        string? expiryProvenance = null,
        DateTimeOffset? accessTokenExpiresAt = null,
        string? effectiveSource = null,
        bool? nativeFileShadowed = null,
        IReadOnlyList<string>? evidenceRefs = null)
        => new(
            key,
            category,
            status,
            version,
            identity,
            detail,
            signal,
            expiresAt?.UtcDateTime,
            limitedUntil?.UtcDateTime,
            credentialModifiedAt?.UtcDateTime,
            evidenceId,
            evidenceExcerpt,
            supportedModels,
            credentialGeneration,
            credentialObservedAt?.UtcDateTime,
            lastRealSuccessAt?.UtcDateTime,
            expiryProvenance,
            accessTokenExpiresAt?.UtcDateTime,
            effectiveSource,
            nativeFileShadowed,
            evidenceRefs);

    private static string Platform()
        => $"{(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other")}:{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    private static string PlatformClass()
        => OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos" : "other";

    internal static bool OnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;
        var names = OperatingSystem.IsWindows()
            ? new[] { executable, executable + ".exe", executable + ".cmd" }
            : new[] { executable };
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => names.Any(name => File.Exists(Path.Combine(directory, name))));
    }

    private static string? ToolVersion(string executable)
        => OnPath(executable) ? "available" : null;

    private static CliInstallation? InspectCli(string executable)
    {
        var path = ResolveExecutable(executable);
        if (path is null) return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            Task.WaitAll([stdout, stderr], 1_000);
            var output = $"{stdout.Result} {stderr.Result}";
            var match = Regex.Match(output, @"(?<!\d)(\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?)(?!\d)");
            return match.Success ? new CliInstallation(match.Groups[1].Value, path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelCatalogCache>
        CodexModelCatalogs = new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string>? CodexModels(CliInstallation installation)
    {
        var cacheKey = $"{installation.Path}\0{installation.Version}";
        if (CodexModelCatalogs.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.ObservedAt < TimeSpan.FromMinutes(60))
            return cached.Models;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = installation.Path,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.StartInfo.ArgumentList.Add("debug");
            process.StartInfo.ArgumentList.Add("models");
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            Task.WaitAll([stdout, stderr], 1_000);
            if (process.ExitCode != 0) return null;

            var output = stdout.Result;
            var first = output.IndexOf('{');
            var last = output.LastIndexOf('}');
            if (first < 0 || last <= first) return null;
            using var document = JsonDocument.Parse(output[first..(last + 1)]);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return null;
            var result = models.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object
                               && item.TryGetProperty("visibility", out var visibility)
                               && string.Equals(visibility.GetString(), "list", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.TryGetProperty("slug", out var slug) ? slug.GetString()?.Trim() : null)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (result.Length == 0) return null;
            CodexModelCatalogs[cacheKey] = new ModelCatalogCache(DateTime.UtcNow, result);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var names = OperatingSystem.IsWindows()
            ? new[] { executable, executable + ".exe", executable + ".cmd" }
            : [executable];
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists) is { } candidate ? Path.GetFullPath(candidate) : null;
    }

    private sealed record CliInstallation(string Version, string Path);
    private sealed record ModelCatalogCache(DateTime ObservedAt, IReadOnlyList<string> Models);

    private static long? DiskFreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!).AvailableFreeSpace; }
        catch { return null; }
    }

    private static long? DiskTotalBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!).TotalSize; }
        catch { return null; }
    }
}

/// <summary>One observed provider-authentication verdict plus the sentence an operator can act on.</summary>
/// <param name="Status">Exactly what goes on the wire: <c>ready</c>, <c>limited</c>, or <c>unavailable</c>.</param>
/// <param name="Detail">One line, no secrets, safe to show on the capability panel.</param>
/// <param name="ObservedAt">When the verdict was taken - the TTL is measured from here.</param>
/// <param name="ProbeDegraded">Whether the latest probe was indeterminate and this verdict was retained.</param>
public sealed record ProviderAuthStatus(
    string Status,
    string Detail,
    DateTimeOffset ObservedAt,
    bool ProbeDegraded = false,
    string Signal = ProviderAuthProbe.SignalOk,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? LimitedUntil = null,
    DateTimeOffset? CredentialModifiedAt = null,
    string? EvidenceId = null,
    string? EvidenceExcerpt = null,
    string? CredentialGeneration = null,
    DateTimeOffset? LastRealSuccessAt = null,
    string ExpiryProvenance = "unknown",
    DateTimeOffset? AccessTokenExpiresAt = null,
    string EffectiveSource = "unknown",
    bool NativeFileShadowed = false,
    ProviderProbeOutcome Outcome = ProviderProbeOutcome.Indeterminate)
{
    public bool IsReady => Status == ProviderAuthProbe.Ready;
}

/// <summary>
/// Runs one bounded, non-agent status command and returns its exit code and output.
/// This is the seam that keeps the actual child process out of the capability
/// layer: the composition root supplies it (see <see cref="ProviderAuthProbe"/>),
/// tests supply a fake, and this file never starts a coding-agent CLI itself.
/// </summary>
public delegate Task<ProcessResult> ProviderAuthLauncher(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken ct);

/// <summary>
/// Honest <c>provider-auth</c> status for the capability advertisement
/// (docs/operations/token-refresh-ohne-tunnel.md, stage S2 "aktive Probe").
///
/// <para>Until now the runner advertised <c>ready</c> as soon as the CLI binary
/// sat on PATH, so a host with an expired or revoked login kept claiming coding
/// cards and burned every one of them. This asks the CLI whether it still has a
/// session - <c>claude auth status</c> / <c>codex login status</c> - under a
/// bounded timeout, and maps the answer onto the explicit auth states claim
/// admission and operator visibility understand.</para>
///
/// <para><b>Cached, never per claim.</b> The verdict is taken at most once per
/// <see cref="DefaultTtl"/>; the advertisement loop (every 60 s) and every claim
/// read the cached value. An expired entry is refreshed behind the last known
/// verdict, so no daemon loop ever waits on a child process.</para>
///
/// <para><b>Last-good with negative confirmation.</b> Repeated explicit logout
/// output may replace a ready verdict with <c>unavailable</c>. A timeout,
/// empty output, launch failure, unsupported command, or lone 401 is
/// indeterminate. Degraded probes preserve the original successful observation
/// time and mark that evidence indeterminate after ten minutes. The legacy
/// admission status remains unchanged in this slice.</para>
///
/// <para><b>Wiring (open connection point).</b> Without a launcher the probe
/// degrades to the old PATH check - it just says so in the detail instead of
/// claiming proof. The composition root wires the real one in a single line, and
/// it belongs there rather than here because the CLI-invocation guard keeps
/// coding-agent spawns inside the execution layer.</para>
/// </summary>
public sealed class ProviderAuthProbe
{
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";
    public const string Limited = "limited";
    public const string Degraded = "degraded";
    public const string SignalOk = "ok";
    public const string SignalTransient = "transient-auth-error";
    public const string SignalLimited = "rate-limited";
    public const string SignalSignedOut = "signed-out";
    public const string SignalExpiring = "credentials-expiring";
    public const string ConceptPath = "docs/operations/token-refresh-ohne-tunnel.md";

    /// <summary>
    /// Stable clean-context task identity for the idle auth probe (composition
    /// root wiring, see runner/Program.cs). Fixed and distinct from any real
    /// task id so the probe gets its own linked-credential, transcript-free home
    /// instead of ever sharing config or session state with an active run.
    /// </summary>
    public const string CleanContextIdentity = "provider-auth-probe";

    /// <summary>Idle cost is one child process per host per five minutes.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>Node-based CLIs may need this long to start on a saturated review host.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A legacy limited verdict without a reset is never permanent.</summary>
    public static readonly TimeSpan UnknownLimitTtl = TimeSpan.FromMinutes(10);

    /// <summary>Two explicit logout answers are required before claim admission closes.</summary>
    public const int DefaultNegativeConfirmations = 2;

    /// <summary>The instance the advertisement reads from when no probe is passed in.</summary>
    public static ProviderAuthProbe Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Func<string, bool> _executableExists;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _timeout;
    private readonly int _negativeConfirmations;
    private readonly Func<string, ProviderCredentialFreshness> _credentialFreshness;
    private ProviderAuthLauncher? _launcher;
    private Action<string>? _diagnosticLog;
    private readonly Dictionary<string, ProviderAuthCacheEntry> _observed =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshInFlight =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeRuns = new(StringComparer.Ordinal);
    // Bumped whenever a completed run publishes evidence. A probe that started
    // before the bump holds older evidence and must not overwrite the run's.
    // A sequence, not a timestamp: fake or stepped clocks can tie both events.
    private readonly Dictionary<string, long> _runEvidenceVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _singleFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Window, int Used)> _realBudgets = new(StringComparer.Ordinal);
    private ProviderAuthLauncher? _realLauncher;
    private ProviderStatusIncidentAdapter? _incidentAdapter;
    private Func<ProviderComparisonQuery, CancellationToken, Task<ProviderComparisonSnapshot>>? _comparisonAdapter;
    private string _hostId = "";
    public static readonly TimeSpan HealthyRealCheckCeiling = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan LastGoodWindow = TimeSpan.FromMinutes(10);
    public const int DailyRealRequestBudget = 48;

    public void UseRealRequest(ProviderAuthLauncher launcher, ProviderStatusIncidentAdapter incidentAdapter)
    {
        lock (_sync) { _realLauncher = launcher; _incidentAdapter = incidentAdapter; }
    }

    public void UseRealRequest(
        ProviderAuthLauncher launcher, ProviderStatusIncidentAdapter incidentAdapter,
        Func<ProviderComparisonQuery, CancellationToken, Task<ProviderComparisonSnapshot>> comparisonAdapter,
        string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        lock (_sync)
        {
            _realLauncher = launcher;
            _incidentAdapter = incidentAdapter;
            _comparisonAdapter = comparisonAdapter;
            _hostId = hostId;
        }
    }

    public ProviderAuthProbe(
        ProviderAuthLauncher? launcher = null,
        Func<string, bool>? executableExists = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? ttl = null,
        TimeSpan? timeout = null,
        int negativeConfirmations = DefaultNegativeConfirmations,
        Action<string>? diagnosticLog = null,
        Func<string, ProviderCredentialFreshness>? credentialFreshness = null)
    {
        if (negativeConfirmations < 1)
            throw new ArgumentOutOfRangeException(nameof(negativeConfirmations));
        _launcher = launcher;
        _executableExists = executableExists ?? ExecutableExists;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? DefaultTtl;
        _timeout = timeout ?? DefaultTimeout;
        _negativeConfirmations = negativeConfirmations;
        _diagnosticLog = diagnosticLog;
        _credentialFreshness = credentialFreshness ?? (binary => ProviderCredentialMonitor.Inspect(binary));
    }

    /// <summary>
    /// Connection point for the composition root: hand the probe a way to start a
    /// short-lived status command. Any verdict taken before this point was a
    /// presence check only and is dropped.
    /// </summary>
    public void UseLauncher(ProviderAuthLauncher launcher, Action<string>? diagnosticLog = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        lock (_sync)
        {
            _launcher = launcher;
            _diagnosticLog = diagnosticLog;
            _observed.Clear();
            _refreshInFlight.Clear();
        }
    }

    /// <summary>
    /// The status to advertise right now. Never blocks: a fresh verdict is
    /// returned as is, a stale one is served while a refresh runs behind it, and
    /// the very first call answers from what can be decided without a child
    /// process - marked "unverified" so the detail never overstates the evidence.
    /// </summary>
    public ProviderAuthStatus Current(string cliBinary)
    {
        lock (_sync)
        {
            _observed.TryGetValue(cliBinary, out var known);
            if (known is not null)
            {
                var fresh = ExpireStaleHealthy(known.Status, _clock());
                if (fresh != known.Status)
                {
                    known = known with { Status = fresh };
                    _observed[cliBinary] = known;
                }
            }
            var forceRefresh = false;
            if (known?.Status.Status == Limited
                && (known.Status.LimitedUntil ?? known.Status.ObservedAt.Add(UnknownLimitTtl)) <= _clock())
            {
                // Serve a claimable degraded state while the forced probe runs.
                // This prevents an unparseable legacy limit from becoming an
                // in-memory latch that only a service restart can clear.
                known = known with
                {
                    Status = known.Status with
                    {
                        Status = Degraded,
                        Detail = "Provider limit evidence expired; authentication re-probe is in progress.",
                        ProbeDegraded = true,
                    },
                };
                _observed[cliBinary] = known;
                forceRefresh = true;
            }
            if (!forceRefresh && known is not null && _clock() - (known.LastAttemptAt ?? known.Status.ObservedAt) < _ttl)
                return known.Status;

            if (_launcher is not null && _refreshInFlight.Add(cliBinary))
            {
                _ = Task.Run(async () =>
                {
                    // A failed refresh must never take the daemon loop with it;
                    // ObserveAsync already turns every failure into a verdict.
                    try { await RefreshAsync(cliBinary, CancellationToken.None); }
                    catch { /* keep serving the last known verdict */ }
                });
            }
            if (known is not null) return known.Status;

            var bootstrap = PresenceOnly(cliBinary, probeWired: _launcher is not null);
            _observed[cliBinary] = new ProviderAuthCacheEntry(bootstrap, 0);
            return bootstrap;
        }
    }

    /// <summary>
    /// Takes a verdict now and caches it. Awaitable so a host can prove its login
    /// before the first advertisement instead of one refresh interval later.
    /// </summary>
    public async Task<ProviderAuthStatus> RefreshAsync(string cliBinary, CancellationToken ct)
    {
        try
        {
            SemaphoreSlim gate;
            lock (_sync)
            {
                if (!_singleFlights.TryGetValue(cliBinary, out gate!))
                    _singleFlights[cliBinary] = gate = new SemaphoreSlim(1, 1);
            }
            await gate.WaitAsync(ct);
            try
            {
                long runEvidenceVersion;
                lock (_sync) runEvidenceVersion = _runEvidenceVersions.GetValueOrDefault(cliBinary);
                var observation = await ObserveAsync(cliBinary, ct);
                ProviderAuthCacheEntry decision;
                ProviderAuthCacheEntry? previous;
                lock (_sync)
                {
                    _observed.TryGetValue(cliBinary, out previous);
                    if (previous is not null
                        && _runEvidenceVersions.GetValueOrDefault(cliBinary) != runEvidenceVersion
                        && previous.Status.EffectiveSource == observation.EffectiveSource
                        && previous.Status.CredentialGeneration == observation.CredentialGeneration)
                    {
                        // Run evidence for this credential arrived while the status
                        // command was running, so this answer is older. A changed
                        // source or generation is still published as a new binding.
                        _observed[cliBinary] = previous with { LastAttemptAt = _clock() };
                        return previous.Status;
                    }
                    runEvidenceVersion = _runEvidenceVersions.GetValueOrDefault(cliBinary);
                    decision = Decide(previous, observation, _negativeConfirmations, _clock());
                    var sourceDecision = ProviderProbeClassifier.Classify(new ProviderProbeRequest(
                        RunnerCapabilityProbe.Provider(cliBinary), "configured",
                        RunnerCapabilityProbe.Provider(cliBinary) == "codex" ? "codex-exec" : "claude-code",
                        observation.EffectiveSource, observation.CredentialGeneration, _clock(),
                        ExplicitNonRefreshableExpiry: observation.ExpiresAt));
                    if (sourceDecision.Outcome == ProviderProbeOutcome.CredentialInvalid)
                        decision = decision with { Status = decision.Status with
                        {
                            Outcome = ProviderProbeOutcome.CredentialInvalid,
                            Signal = "credential_invalid",
                            Detail = sourceDecision.Detail,
                            EffectiveSource = observation.EffectiveSource,
                            CredentialGeneration = observation.CredentialGeneration,
                            ExpiryProvenance = observation.ExpiryProvenance,
                            ExpiresAt = observation.ExpiresAt,
                        } };
                    _observed[cliBinary] = decision;
                }
                LogTransition(cliBinary, previous, decision, observation);
                return await MaybeRealRequestAsync(cliBinary, decision.Status,
                    observation.Kind == ProviderAuthObservationKind.Unauthorized, runEvidenceVersion, ct);
            }
            finally { gate.Release(); }
        }
        finally
        {
            lock (_sync) _refreshInFlight.Remove(cliBinary);
        }
    }

    private async Task<ProviderAuthStatus> MaybeRealRequestAsync(
        string cliBinary, ProviderAuthStatus status, bool statusUnauthorized, long runEvidenceVersion,
        CancellationToken ct)
    {
        ProviderAuthLauncher? launcher;
        ProviderStatusIncidentAdapter? incidents;
        Func<ProviderComparisonQuery, CancellationToken, Task<ProviderComparisonSnapshot>>? comparisons;
        string hostId;
        lock (_sync)
        {
            launcher = _realLauncher;
            incidents = _incidentAdapter;
            comparisons = _comparisonAdapter;
            hostId = _hostId;
        }
        if (launcher is null || status.Status == Limited
            || (!statusUnauthorized && (status.Status != Ready || status.ProbeDegraded))
            || status.Outcome == ProviderProbeOutcome.CredentialInvalid) return status;
        var now = _clock();
        if (!statusUnauthorized && status.LastRealSuccessAt is { } last
            && now - last < HealthyRealCheckCeiling) return status;
        var provider = RunnerCapabilityProbe.Provider(cliBinary);
        lock (_sync)
        {
            var budget = _realBudgets.GetValueOrDefault(provider);
            if (now - budget.Window >= TimeSpan.FromDays(1)) budget = (now, 0);
            if (budget.Used >= DailyRealRequestBudget)
            {
                if (!_observed.TryGetValue(cliBinary, out var existing)) return status;
                if (existing.Status.CredentialGeneration != status.CredentialGeneration
                    || existing.Status.EffectiveSource != status.EffectiveSource
                    || _runEvidenceVersions.GetValueOrDefault(cliBinary) != runEvidenceVersion)
                    return existing.Status;
                var expired = ExpireStaleHealthy(existing.Status, now);
                if (expired != existing.Status)
                    _observed[cliBinary] = existing with { Status = expired };
                return expired;
            }
            _realBudgets[provider] = (budget.Window, budget.Used + 1);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);
        ProcessResult? result;
        try { result = await launcher(cliBinary, [], bounded.Token); }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = new ProcessResult(1, "", exception.GetType().Name);
        }
        var official = new ProviderIncidentSnapshot([], _clock(), false, "not-retrieved");
        if (result.ExitCode != 0 && incidents is not null)
            official = await incidents.GetAsync(RunnerCapabilityProbe.Provider(cliBinary), ct);
        var unauthorizedSignature = result.ExitCode != 0
            ? ProviderProbeClassifier.Classify(new ProviderProbeRequest(
                RunnerCapabilityProbe.Provider(cliBinary), "configured", RunnerCapabilityProbe.Provider(cliBinary) == "codex" ? "codex-exec" : "claude-code",
                status.EffectiveSource, status.CredentialGeneration, _clock(), result)).FailureSignature
            : null;
        var comparison = new ProviderComparisonSnapshot(null, null);
        var officialDecision = ProviderProbeClassifier.Classify(new ProviderProbeRequest(
            provider, "configured", provider == "codex" ? "codex-exec" : "claude-code",
            status.EffectiveSource, status.CredentialGeneration, _clock(), result,
            Incidents: official.Incidents));
        if (unauthorizedSignature is not null && comparisons is not null && hostId.Length > 0
            && officialDecision.Outcome != ProviderProbeOutcome.ProviderIncident)
        {
            try
            {
                using var comparisonDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                comparisonDeadline.CancelAfter(_timeout);
                comparison = await comparisons(new ProviderComparisonQuery(provider,
                    provider == "codex" ? "codex-exec" : "claude-code",
                    "minimal-text-v1", unauthorizedSignature, status.EffectiveSource,
                    status.CredentialGeneration),
                    comparisonDeadline.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Missing independent evidence leaves the unauthorized result indeterminate.
            }
        }
        var request = new ProviderProbeRequest(
            RunnerCapabilityProbe.Provider(cliBinary), "configured", RunnerCapabilityProbe.Provider(cliBinary) == "codex" ? "codex-exec" : "claude-code",
            status.EffectiveSource, status.CredentialGeneration, _clock(), result,
            Incidents: official.Incidents,
            Comparison: comparison.Comparison, HostId: hostId,
            CredentialIdentity: comparison.CredentialIdentity ?? "");
        var decision = ProviderProbeClassifier.Classify(request);
        var updated = status with
        {
            Outcome = decision.Outcome,
            Detail = decision.Detail,
            Signal = decision.Outcome switch
            {
                ProviderProbeOutcome.CredentialInvalid => "credential_invalid",
                ProviderProbeOutcome.ProviderIncident => "provider_incident",
                ProviderProbeOutcome.QuotaExhausted => "quota_exhausted",
                ProviderProbeOutcome.NetworkFailure => "network_failure",
                ProviderProbeOutcome.Healthy => "healthy",
                _ => "indeterminate",
            },
            EvidenceId = decision.EvidenceId,
            EvidenceExcerpt = decision.FailureSignature,
            LastRealSuccessAt = decision.Outcome == ProviderProbeOutcome.Healthy ? _clock() : status.LastRealSuccessAt,
        };
        lock (_sync)
        {
            if (_observed.TryGetValue(cliBinary, out var existing))
            {
                // A completed run, successful or not, is newer evidence than a
                // request started before it.
                if (existing.Status.CredentialGeneration != status.CredentialGeneration
                    || existing.Status.EffectiveSource != status.EffectiveSource
                    || _runEvidenceVersions.GetValueOrDefault(cliBinary) != runEvidenceVersion)
                    return existing.Status;
                _observed[cliBinary] = existing with { Status = updated };
            }
        }
        return updated;
    }

    private static ProviderAuthStatus ExpireStaleHealthy(ProviderAuthStatus status, DateTimeOffset now)
    {
        if (status.Status != Ready || status.Outcome != ProviderProbeOutcome.Healthy)
            return status;
        if (status.LastRealSuccessAt is { } last
            && now >= last && now - last < HealthyRealCheckCeiling)
            return status;
        return status with
        {
            Outcome = ProviderProbeOutcome.Indeterminate,
            Signal = "indeterminate",
            Detail = "Real provider verification is stale; a fresh request is required.",
            ProbeDegraded = true,
            EvidenceId = null,
            EvidenceExcerpt = null,
        };
    }

    /// <summary>
    /// The status command per provider, or null when this runner has none for it.
    /// Null means "keep the presence check": inventing a command for an unknown
    /// wrapper binary would drain the host on its first advertisement.
    /// </summary>
    public static IReadOnlyList<string>? AuthStatusArguments(string provider) => provider switch
    {
        "claude" => ["auth", "status", "--text"],
        "codex" => ["login", "status"],
        _ => null,
    };

    /// <summary>
    /// Builds the low-contention Linux invocation used by the composition root.
    /// ArgumentList keeps configured paths and arguments out of shell parsing.
    /// </summary>
    internal static ProviderAuthProcessInvocation LowPriorityInvocation(
        string fileName,
        IReadOnlyList<string> arguments,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (!OperatingSystem.IsLinux())
            return new ProviderAuthProcessInvocation(fileName, arguments, false);

        var nice = new[] { "/usr/bin/nice", "/bin/nice" }.FirstOrDefault(fileExists);
        return nice is null
            ? new ProviderAuthProcessInvocation(fileName, arguments, false)
            : new ProviderAuthProcessInvocation(
                nice,
                ["-n", "10", "--", fileName, .. arguments],
                true);
    }

    private async Task<ProviderAuthObservation> ObserveAsync(string cliBinary, CancellationToken ct)
    {
        ProviderAuthLauncher? launcher;
        lock (_sync) launcher = _launcher;
        if (launcher is null)
            return Indeterminate("no auth probe launcher is wired");

        var provider = RunnerCapabilityProbe.Provider(cliBinary);
        if (!_executableExists(cliBinary))
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.BinaryMissing,
                $"CLI binary '{cliBinary}' was not found; provider '{provider}' cannot authenticate a run.");

        var source = _credentialFreshness(cliBinary);
        ProviderAuthObservation WithSource(ProviderAuthObservation value) => value with
        {
            EffectiveSource = source.EffectiveSource,
            CredentialGeneration = source.CredentialGeneration,
            ExpiresAt = source.ExpiryProvenance is "issuer" or "operator" ? source.ExpiresAt : null,
            ExpiryProvenance = source.ExpiryProvenance,
        };

        var arguments = AuthStatusArguments(provider);
        if (arguments is null)
            return WithSource(Indeterminate(
                $"unverified: no auth status command is known for provider '{provider}'; "
                + $"binary presence only. See {ConceptPath}."));

        var command = $"{provider} {string.Join(' ', arguments)}";
        ProcessResult result;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);
        try
        {
            result = await launcher(cliBinary, arguments, bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WithSource(Indeterminate($"'{command}' did not answer within {_timeout.TotalSeconds:0}s."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return WithSource(Indeterminate(
                $"'{command}' could not be started: "
                + Excerpt($"{exception.GetType().Name}: {exception.Message}")));
        }

        var observation = Interpret(command, result);
        var freshness = source;
        // Only a verified login expiry drives the warning; a native
        // access-token hint is refreshed by the CLI and is not login expiry.
        var expiresAt = freshness.ExpiryProvenance is "issuer" or "operator"
            ? freshness.ExpiresAt
            : null;
        var expiring = expiresAt is not null
                       && expiresAt <= _clock().Add(ProviderCredentialMonitor.ExpiryWarningWindow);
        var freshnessDetail = freshness.ModifiedAt is null
            ? freshness.Detail
            : $"{freshness.Detail} Credential file last changed {freshness.ModifiedAt:o}.";
        return observation with
        {
            Detail = observation.Kind == ProviderAuthObservationKind.Authenticated && expiring
                    ? $"{observation.Detail} Credentials expire at {expiresAt:o}; refresh or re-authentication may be needed soon. {freshnessDetail}"
                    : $"{observation.Detail} {freshnessDetail}",
            Signal = observation.Kind == ProviderAuthObservationKind.Authenticated
                ? expiring ? SignalExpiring : SignalOk : observation.Signal,
            ExpiresAt = expiresAt,
            CredentialModifiedAt = freshness.ModifiedAt,
            AccessTokenExpiresAt = freshness.AccessTokenExpiresAt,
            ExpiryProvenance = freshness.ExpiryProvenance,
            EffectiveSource = freshness.EffectiveSource,
            NativeFileShadowed = freshness.NativeFileShadowed,
            CredentialGeneration = freshness.CredentialGeneration,
        };
    }

    private ProviderAuthObservation Interpret(string command, ProcessResult result)
    {
        var text = $"{result.StdOut}\n{result.StdErr}";
        if (LooksLikeAgentStreamOutput(text))
            return Indeterminate(
                $"unverified: '{command}' returned agent session output instead of a status answer "
                + $"(probe isolation gap, content not evaluated); binary presence only. See {ConceptPath}.");
        if (IndicatesUnsupportedCommand(text))
            return Indeterminate(
                $"unverified: '{command}' is not supported by the installed CLI (exit {result.ExitCode}): "
                + $"{Excerpt(text)} Binary presence only. See {ConceptPath}.");
        var evidence = ProviderAccessClassifier.Classify(
            result.ExitCode,
            result.StdOut,
            result.StdErr,
            statusProbe: true,
            observedAt: _clock());
        if (evidence.Kind == ProviderAccessEvidenceKind.AuthenticationFailure)
        {
            if (text.Contains("401", StringComparison.OrdinalIgnoreCase)
                || text.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
                || text.Contains("incorrect api key", StringComparison.OrdinalIgnoreCase))
                return new ProviderAuthObservation(ProviderAuthObservationKind.Unauthorized,
                    $"'{command}' reported an unauthorized response requiring corroboration.");
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.LoggedOut,
                $"'{command}' reports no usable session (exit {result.ExitCode}): {Excerpt(text)}",
                SignalSignedOut);
        }
        if (evidence.Kind == ProviderAccessEvidenceKind.RateLimited)
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.Limited,
                $"'{command}' reports a provider limit until {evidence.LimitedUntil:o}: {Excerpt(text)}",
                SignalLimited,
                LimitedUntil: evidence.LimitedUntil);
        if (evidence.Kind == ProviderAccessEvidenceKind.TransientFailure)
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.Transient,
                $"'{command}' hit a transient provider error (exit {result.ExitCode}): {Excerpt(text)}",
                SignalTransient);
        if (evidence.Kind == ProviderAccessEvidenceKind.Authenticated)
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.Authenticated,
                $"'{command}' confirmed an active session.",
                SignalOk);
        return Indeterminate(string.IsNullOrWhiteSpace(text)
            ? $"'{command}' returned empty output (exit {result.ExitCode})."
            : $"'{command}' failed without a logout signal (exit {result.ExitCode}): {Excerpt(text)}");
    }

    /// <summary>
    /// Feeds a completed coding process into the same classifier as the idle
    /// probe. Ordinary tool failures are ignored, provider limits become a
    /// limited capability, and only explicit login failures advance the
    /// negative-confirmation counter.
    /// </summary>
    public ProviderAuthStatus RecordProcessResult(
        string cliBinary,
        ProcessResult result,
        string? evidenceId = null,
        bool operatorStopped = false,
        int? signal = null,
        bool hostShutdown = false,
        string? credentialGeneration = null)
    {
        // A run that exited 0 reached the provider. Its output is agent content
        // (files and docs it read, rate_limit_event warnings), which can contain
        // "rate-limited" or "usage limit" without any limit being hit.
        if (result.ExitCode == 0)
        {
            lock (_sync)
            {
                if (_observed.TryGetValue(cliBinary, out var last)
                    && (last.Status.CredentialGeneration is null
                        || last.Status.CredentialGeneration == credentialGeneration))
                {
                    var ready = last.Status.Status == Ready;
                    // Working access interrupts any run of explicit logout answers.
                    _observed[cliBinary] = last with { ConsecutiveLogoutSignals = 0, Status = last.Status with
                    {
                        LastRealSuccessAt = _clock(),
                        Outcome = ProviderProbeOutcome.Healthy,
                        Signal = ready ? SignalOk : last.Status.Signal,
                        ProbeDegraded = ready ? false : last.Status.ProbeDegraded,
                        Detail = ready ? "Same-generation work confirmed provider access." : last.Status.Detail,
                    } };
                    _runEvidenceVersions[cliBinary] = _runEvidenceVersions.GetValueOrDefault(cliBinary) + 1;
                }
            }
            // Current() still owns expired-limit and TTL re-probes.
            return Current(cliBinary);
        }
        if (operatorStopped || signal is not null || hostShutdown)
            return Current(cliBinary);

        var evidence = ProviderAccessClassifier.Classify(
            result.ExitCode,
            result.StdOut,
            result.StdErr,
            observedAt: _clock());
        ProviderAuthObservation? observation = evidence.Kind switch
        {
            ProviderAccessEvidenceKind.AuthenticationFailure => new ProviderAuthObservation(
                ProviderAuthObservationKind.Indeterminate,
                $"'{RunnerCapabilityProbe.Provider(cliBinary)}' run reported unauthorized access; incident correlation is pending.",
                SignalTransient),
            ProviderAccessEvidenceKind.RateLimited => new ProviderAuthObservation(
                ProviderAuthObservationKind.Limited,
                $"'{RunnerCapabilityProbe.Provider(cliBinary)}' is rate-limited until {evidence.LimitedUntil:o}: {Excerpt(evidence.Detail)}",
                SignalLimited,
                LimitedUntil: evidence.LimitedUntil,
                EvidenceId: SafeEvidenceId(evidenceId),
                EvidenceExcerpt: Excerpt(evidence.Detail)),
            ProviderAccessEvidenceKind.TransientFailure => new ProviderAuthObservation(
                ProviderAuthObservationKind.Transient,
                $"Transient auth error, retrying: {Excerpt(evidence.Detail)}",
                SignalTransient),
            _ => null,
        };
        if (observation is null) return Current(cliBinary);

        ProviderAuthCacheEntry decision;
        ProviderAuthCacheEntry? previous;
        var hasActiveCounterEvidence = false;
        lock (_sync)
        {
            _observed.TryGetValue(cliBinary, out previous);
            if (observation.Kind == ProviderAuthObservationKind.Limited
                && _activeRuns.GetValueOrDefault(cliBinary) > 0)
            {
                hasActiveCounterEvidence = true;
                observation = observation with
                {
                    Kind = ProviderAuthObservationKind.Transient,
                    Detail = $"counter-evidence: another {RunnerCapabilityProbe.Provider(cliBinary)} run is active; "
                             + $"not applying limit from {evidenceId ?? "unknown run"}. {observation.Detail}",
                    Signal = SignalTransient,
                };
            }
            decision = Decide(previous, observation, _negativeConfirmations, _clock());
            if (hasActiveCounterEvidence)
                decision = decision with
                {
                    Status = decision.Status with
                    {
                        Status = Degraded,
                        ProbeDegraded = true,
                        Signal = SignalTransient,
                    },
                };
            _observed[cliBinary] = decision;
            _runEvidenceVersions[cliBinary] = _runEvidenceVersions.GetValueOrDefault(cliBinary) + 1;
        }
        LogTransition(cliBinary, previous, decision, observation);
        return decision.Status;
    }

    public void RecordRunStarted(string cliBinary)
    {
        lock (_sync) _activeRuns[cliBinary] = _activeRuns.GetValueOrDefault(cliBinary) + 1;
    }

    public void RecordRunCompleted(string cliBinary)
    {
        lock (_sync)
        {
            var remaining = _activeRuns.GetValueOrDefault(cliBinary) - 1;
            if (remaining > 0) _activeRuns[cliBinary] = remaining;
            else _activeRuns.Remove(cliBinary);
        }
    }

    private static ProviderAuthObservation Indeterminate(string detail)
        => new(ProviderAuthObservationKind.Indeterminate, detail);

    private static ProviderAuthCacheEntry Decide(
        ProviderAuthCacheEntry? previous,
        ProviderAuthObservation observation,
        int negativeConfirmations,
        DateTimeOffset observedAt)
    {
        if (observation.Kind == ProviderAuthObservationKind.Authenticated)
        {
            if (previous?.Status.Status == Limited
                && previous.Status.LimitedUntil is { } limitedUntil
                && limitedUntil > observedAt)
            {
                return new ProviderAuthCacheEntry(
                    previous.Status with
                    {
                        Detail = $"Rate-limited until {limitedUntil:o}; the login remains valid and recovery will be checked after reset.",
                        ObservedAt = observedAt,
                        ProbeDegraded = false,
                    },
                    0);
            }
            return new ProviderAuthCacheEntry(
                new ProviderAuthStatus(
                    Ready,
                    observation.Detail,
                    observedAt,
                    Signal: previous?.Status.EffectiveSource == observation.EffectiveSource
                        && previous.Status.CredentialGeneration == observation.CredentialGeneration
                        && previous.Status.Outcome == ProviderProbeOutcome.Indeterminate
                        && previous.Status.Signal == "indeterminate"
                        ? "indeterminate" : observation.Signal,
                    ExpiresAt: observation.ExpiresAt,
                    CredentialModifiedAt: observation.CredentialModifiedAt,
                    AccessTokenExpiresAt: observation.AccessTokenExpiresAt,
                    ExpiryProvenance: observation.ExpiryProvenance,
                    EffectiveSource: observation.EffectiveSource,
                    NativeFileShadowed: observation.NativeFileShadowed,
                    CredentialGeneration: observation.CredentialGeneration,
                    LastRealSuccessAt: previous?.Status.EffectiveSource == observation.EffectiveSource
                        && previous.Status.CredentialGeneration == observation.CredentialGeneration
                        ? previous.Status.LastRealSuccessAt : null,
                    Outcome: previous?.Status.EffectiveSource == observation.EffectiveSource
                        && previous.Status.CredentialGeneration == observation.CredentialGeneration
                        ? previous.Status.Outcome : ProviderProbeOutcome.Indeterminate),
                0, observedAt);
        }
        if (observation.Kind == ProviderAuthObservationKind.BinaryMissing)
            return new ProviderAuthCacheEntry(
                new ProviderAuthStatus(
                    Unavailable,
                    observation.Detail,
                    observedAt,
                    Signal: "binary-missing"),
                0);
        if (observation.Kind == ProviderAuthObservationKind.Limited)
            return new ProviderAuthCacheEntry(
                new ProviderAuthStatus(
                    Limited,
                    observation.Detail,
                    observedAt,
                    Signal: SignalLimited,
                    LimitedUntil: observation.LimitedUntil,
                    EvidenceId: observation.EvidenceId,
                    EvidenceExcerpt: observation.EvidenceExcerpt),
                0);

        var retained = previous?.Status
            ?? new ProviderAuthStatus(
                Ready,
                "unverified: the auth probe has not confirmed a session yet; binary presence only.",
                observedAt);
        if (observation.Kind is ProviderAuthObservationKind.Indeterminate
            or ProviderAuthObservationKind.Unauthorized
            or ProviderAuthObservationKind.Transient)
        {
            var effectiveSource = observation.EffectiveSource == "unknown"
                ? retained.EffectiveSource : observation.EffectiveSource;
            var generation = observation.CredentialGeneration ?? retained.CredentialGeneration;
            var sameBinding = effectiveSource == retained.EffectiveSource
                && generation == retained.CredentialGeneration;
            return new ProviderAuthCacheEntry(
                retained with
                {
                    Detail = $"probe degraded: {observation.Detail} Retaining last status '{retained.Status}'.",
                    ObservedAt = retained.ObservedAt,
                    ProbeDegraded = true,
                    Outcome = ProviderProbeOutcome.Indeterminate,
                    EffectiveSource = effectiveSource,
                    CredentialGeneration = generation,
                    LastRealSuccessAt = sameBinding ? retained.LastRealSuccessAt : null,
                    EvidenceId = null,
                    EvidenceExcerpt = null,
                    Signal = observation.Kind == ProviderAuthObservationKind.Unauthorized
                        || observedAt - retained.ObservedAt > LastGoodWindow
                        ? "indeterminate" : observation.Kind == ProviderAuthObservationKind.Transient
                            ? SignalTransient : retained.Signal,
                },
                0, observedAt);
        }

        var failures = Math.Min(negativeConfirmations, (previous?.ConsecutiveLogoutSignals ?? 0) + 1);
        if (failures < negativeConfirmations)
            return new ProviderAuthCacheEntry(
                retained with
                {
                    Detail = $"probe degraded: explicit logout confirmation {failures}/{negativeConfirmations}; "
                             + $"retaining last status '{retained.Status}'. {observation.Detail}",
                    ObservedAt = retained.ObservedAt,
                    ProbeDegraded = true,
                    Outcome = ProviderProbeOutcome.Indeterminate,
                    Signal = observedAt - retained.ObservedAt > LastGoodWindow
                        ? "indeterminate" : SignalTransient,
                },
                failures, observedAt);
        return new ProviderAuthCacheEntry(
            new ProviderAuthStatus(
                Unavailable,
                observation.Detail,
                observedAt,
                Signal: SignalSignedOut),
            failures);
    }

    private void LogTransition(
        string cliBinary,
        ProviderAuthCacheEntry? previous,
        ProviderAuthCacheEntry decision,
        ProviderAuthObservation observation)
    {
        Action<string>? log;
        lock (_sync) log = _diagnosticLog;
        if (log is null) return;
        if (previous?.Status.Status != decision.Status.Status)
        {
            log(
                $"provider-auth status={decision.Status.Status} binary={cliBinary} "
                + $"until={decision.Status.LimitedUntil?.UtcDateTime:o} "
                + $"evidence={decision.Status.EvidenceId ?? "none"} "
                + $"excerpt={Excerpt(decision.Status.EvidenceExcerpt ?? decision.Status.Detail)}");
        }
        else if (decision.Status.ProbeDegraded)
        {
            log(
                $"runner-provider-auth-probe-degraded binary={cliBinary} "
                + $"outcome={observation.Kind.ToString().ToLowerInvariant()} "
                + $"retainedStatus={decision.Status.Status} "
                + $"logoutConfirmations={decision.ConsecutiveLogoutSignals}/{_negativeConfirmations} "
                + $"detail={observation.Detail}");
        }
    }

    private ProviderAuthStatus PresenceOnly(string cliBinary, bool probeWired)
    {
        var provider = RunnerCapabilityProbe.Provider(cliBinary);
        if (!_executableExists(cliBinary)) return BinaryMissing(cliBinary, provider);
        return new ProviderAuthStatus(
            Ready,
            probeWired
                ? $"unverified: the auth probe for '{provider}' has not answered yet; binary presence only."
                : $"unverified: no auth probe is wired on this host; '{provider}' binary presence only. "
                  + $"See {ConceptPath}.",
            _clock());
    }

    private ProviderAuthStatus BinaryMissing(string cliBinary, string provider)
        => new(
            Unavailable,
            $"CLI binary '{cliBinary}' was not found; provider '{provider}' cannot authenticate a run.",
            _clock());

    /// <summary>
    /// Phrases that mean the question could not be asked, not that the answer was
    /// no - an argument parser rejecting the status subcommand.
    /// </summary>
    private static readonly string[] UnsupportedCommandSignals =
    [
        "unknown command", "unrecognized subcommand", "unrecognised subcommand",
        "unknown option", "unknown flag", "unexpected argument", "invalid choice",
        "no such command", "did you mean", "usage:", "command not found",
    ];

    /// <summary>
    /// Shape of a Claude Code <c>stream-json</c> frame (docs/system/cli/skills/cli-claude.md,
    /// "Stream-json frame catalogue"). A bounded, isolated status command never emits this -
    /// seeing it means the probe's stdout picked up a running agent session instead of an
    /// auth answer (a probe isolation gap), and the content must never be classified or
    /// echoed as if it were the CLI's own status text.
    /// </summary>
    private static readonly Regex AgentStreamFrameSignal = new(
        @"""type""\s*:\s*""(?:system|user|assistant|result|tool_use|tool_result)""",
        RegexOptions.Compiled);

    public static bool LooksLikeAgentStreamOutput(string? text)
        => !string.IsNullOrWhiteSpace(text) && AgentStreamFrameSignal.IsMatch(text);

    public static bool IndicatesNoUsableSession(string? text)
        => ProviderAccessClassifier.Classify(1, text, null).Kind
           == ProviderAccessEvidenceKind.AuthenticationFailure;

    public static bool IndicatesUnsupportedCommand(string? text)
        => Matches(text, UnsupportedCommandSignals);

    private static bool Matches(string? text, string[] signals)
        => !string.IsNullOrWhiteSpace(text)
           && signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    /// <summary>Anything token-shaped is stripped before a detail reaches the board.</summary>
    private static readonly Regex SecretShaped = new(
        @"\b(?:sk-[A-Za-z0-9_\-]{6,}|[A-Za-z0-9_\-]{40,})\b",
        RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string Excerpt(string? value, int maxChars = 200)
    {
        var single = Whitespace.Replace(value ?? string.Empty, " ").Trim();
        var redacted = SecretShaped.Replace(single, "[redacted]");
        return redacted.Length <= maxChars ? redacted : redacted[..maxChars] + "...";
    }

    private static string? SafeEvidenceId(string? id)
        => id is { Length: > 0 and <= 120 } &&
           id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.')
            ? id : null;

    /// <summary>PATH lookup that also accepts a configured absolute binary path.</summary>
    public static bool ExecutableExists(string cliBinary)
    {
        if (string.IsNullOrWhiteSpace(cliBinary)) return false;
        if (cliBinary.Contains(Path.DirectorySeparatorChar)
            || cliBinary.Contains(Path.AltDirectorySeparatorChar))
        {
            var candidates = OperatingSystem.IsWindows()
                ? new[] { cliBinary, cliBinary + ".exe", cliBinary + ".cmd" }
                : [cliBinary];
            return candidates.Any(File.Exists);
        }
        return RunnerCapabilityProbe.OnPath(cliBinary);
    }
}

internal enum ProviderAuthObservationKind
{
    Authenticated,
    LoggedOut,
    Limited,
    Transient,
    Indeterminate,
    Unauthorized,
    BinaryMissing,
}

internal sealed record ProviderAuthObservation(
    ProviderAuthObservationKind Kind,
    string Detail,
    string Signal = ProviderAuthProbe.SignalTransient,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? LimitedUntil = null,
    DateTimeOffset? CredentialModifiedAt = null,
    string? EvidenceId = null,
    string? EvidenceExcerpt = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string ExpiryProvenance = "unknown",
    string EffectiveSource = "unknown",
    bool NativeFileShadowed = false,
    string? CredentialGeneration = null);

internal sealed record ProviderAuthCacheEntry(
    ProviderAuthStatus Status,
    int ConsecutiveLogoutSignals,
    DateTimeOffset? LastAttemptAt = null);

internal sealed record ProviderAuthProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool LowerPriority);
