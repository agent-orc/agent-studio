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
    /// example <c>agt-2650b-20260812T064049Z-ca5cbd6ff</c>. It is the only build
    /// timestamp a host can prove without trusting file mtimes, so the drift
    /// comparison reads it when no explicit stamp was configured.
    /// </summary>
    private static readonly Regex ReleaseStamp = new(
        @"(?<!\d)(\d{8})T(\d{6})Z(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static string Current { get; } = Resolve(AppContext.BaseDirectory);

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
        var commit = Trim(configuredCommit)
                     ?? Trim(informationalVersion?.Split('+', 2).ElementAtOrDefault(1)?.Split('.', 2)[0]);
        var builtAt = ParseInstant(configuredBuiltAt) ?? ParseReleaseStamp(releaseId);

        return new Contract.RunnerReleaseIdentityDto(
            Trim(releaseId) ?? "unknown",
            version,
            commit,
            builtAt);
    }

    internal static DateTime? ParseReleaseStamp(string? releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId)) return null;
        var match = ReleaseStamp.Match(releaseId);
        if (!match.Success) return null;
        return DateTime.TryParseExact(
            $"{match.Groups[1].Value}{match.Groups[2].Value}",
            "yyyyMMddHHmmss",
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
