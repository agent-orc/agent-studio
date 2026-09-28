namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Pure trigger and routing contract for the Compose-render gate step
/// (AGT-2981). A card whose diff can change the Compose stack renders it with
/// the repository's shell contract tests, which need a Docker CLI with the
/// compose plugin (and node) but no Docker daemon. The step carries the
/// <see cref="Requirement"/> host requirement, so every claim plane routes it
/// to a host that advertises it, and a host without it reports
/// <see cref="HostCannotRender"/> instead of passing.
///
/// <para>
/// Before this, the release shell contract tests were the first place that
/// rendered base file plus scenario overlay. AGT-2736 changed
/// <c>docker-compose.yml</c>, passed its Windows card gate, and broke the
/// promotion train on an overlay that still named removed services.
/// </para>
/// </summary>
public static class ComposeRenderGatePolicy
{
    /// <summary>Short requirement name used in verdicts and documentation.</summary>
    public const string RequirementName = "compose-render";

    /// <summary>Capability key a host advertises when it can render Compose.</summary>
    public const string Requirement = CapabilityProtocol.ComposeRender;

    /// <summary>Stable verdict prefix for a gate host that cannot run the step.</summary>
    public const string HostCannotRender = "gate host cannot render Compose; route the gate to a Linux host";

    /// <summary>
    /// Repository-relative render scripts, in execution order. A repository
    /// that does not carry a script does not get its command.
    /// </summary>
    public static IReadOnlyList<string> Scripts { get; } =
    [
        "scripts/scenario.test.sh",
        "scripts/compose-smoke-version.test.sh",
    ];

    /// <summary>The shell command that runs one render script from the repository root.</summary>
    public static string Command(string script) => "bash " + script;

    /// <summary>
    /// True when <paramref name="path"/> can change the rendered Compose stack:
    /// <c>docker-compose.yml</c>, anything under <c>deploy/compose/</c>, any
    /// Dockerfile, <c>scripts/compose-*.sh</c>, <c>scripts/scenario*.sh</c>,
    /// or anything under <c>testsupport/scenario/</c>.
    /// </summary>
    public static bool IsTrigger(string? path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0) return false;
        if (string.Equals(normalized, "docker-compose.yml", StringComparison.OrdinalIgnoreCase)) return true;
        if (HasPrefix(normalized, "deploy/compose/") || HasPrefix(normalized, "testsupport/scenario/")) return true;

        var slash = normalized.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : normalized[..slash];
        var name = normalized[(slash + 1)..];
        if (string.Equals(name, "Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(directory, "scripts", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
               && (name.StartsWith("compose-", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("scenario", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The changed paths that trigger the step, normalized, distinct, and
    /// ordered. Empty for an unknown (null) or unrelated diff.
    /// </summary>
    public static IReadOnlyList<string> Triggers(IEnumerable<string>? changedFiles)
        => (changedFiles ?? [])
            .Where(IsTrigger)
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The render scripts present in the checkout at <paramref name="repositoryPath"/>.</summary>
    public static IReadOnlyList<string> DeclaredScripts(string? repositoryPath)
        => string.IsNullOrWhiteSpace(repositoryPath)
            ? []
            : Scripts.Where(script => File.Exists(Path.Combine(
                    repositoryPath,
                    script.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();

    /// <summary>Render commands to run for this diff in this checkout, in execution order.</summary>
    public static IReadOnlyList<string> Commands(string? repositoryPath, IEnumerable<string>? changedFiles)
        => Triggers(changedFiles).Count == 0
            ? []
            : DeclaredScripts(repositoryPath).Select(Command).ToArray();

    /// <summary>
    /// True when a command line runs one of the render scripts. Claim planes
    /// derive the <see cref="Requirement"/> from the frozen command text, the
    /// same way they derive the dotnet, node, and Playwright toolchains.
    /// </summary>
    public static bool IsRenderInvocation(string? invocation)
        => !string.IsNullOrWhiteSpace(invocation)
           && Scripts.Any(script => invocation.Contains(script, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The routing verdict for a gate host without the requirement. It names
    /// the requirement and the triggering paths so the operator can see why
    /// the card has to move to a Linux gate host.
    /// </summary>
    public static string HostVerdict(IReadOnlyList<string> triggers)
    {
        const int shown = 5;
        var paths = string.Join(", ", triggers.Take(shown));
        if (triggers.Count > shown) paths += $" and {triggers.Count - shown} more";
        return $"{HostCannotRender}. The diff touches {paths}, so the gate step requires "
               + $"'{RequirementName}' ({Requirement}): a Docker CLI with the compose plugin. "
               + "This host has none.";
    }

    private static bool HasPrefix(string path, string prefix)
        => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimStart('/');
    }
}
