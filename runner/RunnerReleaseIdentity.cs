using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Resolves the immutable agent-host deployment identity advertised to the
/// Task Server. Production releases are directories below
/// <c>/opt/agent-host/releases</c> selected by the <c>current</c> symlink.
/// </summary>
internal static class RunnerReleaseIdentity
{
    /// <summary>
    /// Release ids minted by the release pipeline embed the build instant, for
    /// example <c>agt-2650b-20260812T064049Z-ca5cbd6ff</c> or the minute-precision
    /// <c>20260928T1211Z-v0.9.4-6759fe303</c>. It is the only build timestamp a
    /// host can prove without trusting file mtimes, so the drift comparison reads
    /// it when no explicit stamp was configured.
    /// </summary>
    private static readonly Regex ReleaseStamp = new(
        @"(?<!\d)(\d{8})T(\d{4}|\d{6})Z(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A commit is only reported when it is a plausible git sha: seven to forty
    /// hex characters. The runner build writes <c>+sha.unknown</c> into the
    /// informational version when no source revision was passed, and reading
    /// that as the commit <c>sha</c> made every host advertise the same bogus
    /// identity (AGT-2826 acceptance, 12.10.2026).
    /// </summary>
    private static readonly Regex CommitShape = new(
        @"^[0-9a-fA-F]{7,40}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static string Current { get; } = Resolve(AppContext.BaseDirectory);

    /// <summary>
    /// Fully resolved path of the executable this process runs, i.e. the real
    /// <c>/opt/agent-host/releases/&lt;id&gt;/agent-host</c> rather than the
    /// <c>current</c> symlink it was started through. A detached review worker
    /// outlives the promotion that moved that symlink, so the resolved path is
    /// the only truthful answer to "which binary graded this attempt".
    /// </summary>
    internal static string CurrentBinaryPath { get; } = ResolveBinaryPath(Environment.ProcessPath);

    internal static string ResolveBinaryPath(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return "unknown";
        var resolved = Path.GetFullPath(processPath);
        try
        {
            if (new FileInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } file)
                resolved = file.FullName;
            // A release reached through a symlinked directory keeps its literal
            // path above, so the directory has to be resolved as well.
            var directoryPath = Path.GetDirectoryName(resolved);
            if (directoryPath is { Length: > 0 }
                && new DirectoryInfo(directoryPath).ResolveLinkTarget(returnFinalTarget: true)
                    is { } directory)
            {
                resolved = Path.Combine(directory.FullName, Path.GetFileName(resolved));
            }
        }
        catch (IOException)
        {
            // Promotion may replace the symlink during this read. The literal
            // path remains a truthful bounded fallback.
        }
        return resolved;
    }

    /// <summary>
    /// The full release identity reported in registration and in every
    /// capability heartbeat (AGT-2826), so Execution Hosts can compare a host
    /// against the Stable release instead of only showing an opaque label.
    /// </summary>
    internal static Contract.RunnerReleaseIdentityDto CurrentIdentity { get; } = Describe(Current);

    internal static string Resolve(string baseDirectory, string? configured = null)
    {
        var explicitId = (configured ?? RunnerOptions.Env("RUNNER_RELEASE_ID")).Trim();
        if (explicitId.Length > 0) return explicitId;

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var directory = new DirectoryInfo(fullPath);
        if (string.Equals(directory.Name, "current", StringComparison.Ordinal))
        {
            try
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null && target.Name.Length > 0) return target.Name;
            }
            catch (IOException)
            {
                // Promotion may replace the symlink during this read. The
                // assembly identity below remains a truthful bounded fallback.
            }
        }

        if (string.Equals(directory.Parent?.Name, "releases", StringComparison.Ordinal)
            && directory.Name.Length > 0)
            return directory.Name;

        var assembly = typeof(RunnerReleaseIdentity).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }

    /// <summary>
    /// Pure projection of a release id plus the runner assembly into the wire
    /// identity. Unknown facts stay null rather than being guessed: the server
    /// side treats a missing commit or build stamp as "not comparable" and says
    /// so, which is more useful to an operator than an invented value.
    /// </summary>
    internal static Contract.RunnerReleaseIdentityDto Describe(
        string releaseId,
        string? informationalVersion = null,
        string? assemblyVersion = null,
        string? configuredCommit = null,
        string? configuredBuiltAt = null)
    {
        var assembly = typeof(RunnerReleaseIdentity).Assembly;
        informationalVersion ??= assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        assemblyVersion ??= assembly.GetName().Version?.ToString(3);
        configuredCommit ??= RunnerOptions.Env("RUNNER_RELEASE_COMMIT");
        configuredBuiltAt ??= RunnerOptions.Env("RUNNER_RELEASE_BUILT_AT");

        var version = Trim(informationalVersion?.Split('+', 2)[0])
                      ?? Trim(assemblyVersion)
                      ?? "unknown";
        var commit = ParseCommit(configuredCommit)
                     ?? ParseInformationalCommit(informationalVersion)
                     ?? ParseReleaseIdCommit(releaseId);
        var builtAt = ParseInstant(configuredBuiltAt) ?? ParseReleaseStamp(releaseId);

        return new Contract.RunnerReleaseIdentityDto(
            Trim(releaseId) ?? "unknown",
            version,
            commit,
            builtAt);
    }

    /// <summary>
    /// Reads the source revision from the informational version. The runner
    /// project writes <c>&lt;version&gt;+sha.&lt;revision&gt;</c>; the SDK default
    /// is <c>&lt;version&gt;+&lt;revision&gt;</c>. Both forms are accepted, and a
    /// revision that is not a sha (for example <c>unknown</c>) yields null.
    /// </summary>
    internal static string? ParseInformationalCommit(string? informationalVersion)
    {
        var metadata = Trim(informationalVersion?.Split('+', 2).ElementAtOrDefault(1));
        if (metadata is null) return null;
        if (metadata.StartsWith("sha.", StringComparison.OrdinalIgnoreCase)) metadata = metadata[4..];
        return ParseCommit(metadata.Split('.', 2)[0]);
    }

    /// <summary>
    /// A pipeline-minted release id ends in the abbreviated commit it was built
    /// from (<c>20260928T1211Z-v0.9.4-6759fe303</c>). Only a stamped id is read
    /// this way, so a free-form label never turns into a commit.
    /// </summary>
    internal static string? ParseReleaseIdCommit(string? releaseId)
    {
        if (ParseReleaseStamp(releaseId) is null) return null;
        return ParseCommit(releaseId!.Trim().Split('-')[^1]);
    }

    private static string? ParseCommit(string? value)
    {
        var commit = Trim(value);
        return commit is not null && CommitShape.IsMatch(commit) ? commit : null;
    }

    internal static DateTime? ParseReleaseStamp(string? releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId)) return null;
        var match = ReleaseStamp.Match(releaseId);
        if (!match.Success) return null;
        var time = match.Groups[2].Value;
        return DateTime.TryParseExact(
            $"{match.Groups[1].Value}{time}",
            time.Length == 4 ? "yyyyMMddHHmm" : "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? ParseInstant(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
