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
    /// that declares the step (<see cref="IsDeclared"/>) owes every one of
    /// them; a missing script fails the gate instead of dropping its command.
    /// </summary>
    public static IReadOnlyList<string> Scripts { get; } =
    [
        "scripts/scenario.test.sh",
        "scripts/compose-smoke-version.test.sh",
    ];

    /// <summary>The shell command that runs one render script from the repository root.</summary>
    public static string Command(string script) => "bash " + script;

    /// <summary>
    /// The frozen remote-plane form of <see cref="Command"/>. A missing script
    /// exits 1 with <see cref="MissingScriptsPrefix"/> instead of letting bash
    /// exit 127, which the Remote Review and Remote Gate planes classify as an
    /// unavailable toolchain and retry rather than charge to the delivery.
    /// </summary>
    public static string GuardedCommand(string script)
        => $"test -f {script} || {{ echo '{MissingScriptsPrefix}: {script}' >&2; exit 1; }}; {Command(script)}";

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

    /// <summary>
    /// True when the repository owns the Compose-render step: one of the
    /// <paramref name="checkouts"/> carries a render script, or the diff
    /// touches one. The diff counts because a delivery that deletes the
    /// scripts must not thereby opt out of the step; the checkouts count
    /// because a rename shows only its new path in a name-only diff, while the
    /// Studio checkout still carries the old one. A repository that neither
    /// carries nor touches the scripts has no Compose stack to render with
    /// them, so its Dockerfiles do not owe the step.
    /// </summary>
    public static bool IsDeclared(IEnumerable<string?> checkouts, IEnumerable<string>? changedFiles)
        => (changedFiles ?? []).Select(Normalize).Any(path =>
               Scripts.Contains(path, StringComparer.OrdinalIgnoreCase))
           || checkouts.Any(checkout => MissingScripts(checkout).Count < Scripts.Count);

    /// <summary>
    /// The render scripts absent from the checkout at <paramref name="checkout"/>,
    /// in execution order. Every script counts as absent for an unknown checkout.
    /// </summary>
    public static IReadOnlyList<string> MissingScripts(string? checkout)
        => string.IsNullOrWhiteSpace(checkout)
            ? Scripts
            : Scripts.Where(script => !File.Exists(Path.Combine(
                    checkout,
                    script.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();

    /// <summary>
    /// Render scripts a triggering diff owes, in execution order: all of them
    /// when the repository declares the step, whether or not each script is
    /// still present; none for an unrelated diff or a repository without the
    /// step. A missing script therefore fails its command, never removes it.
    /// </summary>
    public static IReadOnlyList<string> OwedScripts(
        IEnumerable<string?> checkouts,
        IEnumerable<string>? changedFiles)
        => Triggers(changedFiles).Count == 0 || !IsDeclared(checkouts, changedFiles)
            ? []
            : Scripts;

    /// <summary>Render commands (<see cref="Command"/>) for <see cref="OwedScripts"/>.</summary>
    public static IReadOnlyList<string> Commands(
        IEnumerable<string?> checkouts,
        IEnumerable<string>? changedFiles)
        => OwedScripts(checkouts, changedFiles).Select(Command).ToArray();

    /// <inheritdoc cref="Commands(IEnumerable{string?}, IEnumerable{string}?)"/>
    public static IReadOnlyList<string> Commands(string? repositoryPath, IEnumerable<string>? changedFiles)
        => Commands([repositoryPath], changedFiles);

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

    /// <summary>
    /// The verdict for a delivery whose checkout lacks a render script the
    /// repository declares. The gate cannot render the stack, and the cause is
    /// in the delivery, so the card is charged rather than rerouted.
    /// </summary>
    public static string MissingScriptVerdict(IReadOnlyList<string> missing)
        => $"{MissingScriptsPrefix}: {string.Join(", ", missing)}. The repository declares the "
           + $"'{RequirementName}' step and the diff can change the Compose stack; restore the "
           + "script(s) so the gate can render it.";

    /// <summary>Stable verdict prefix for a checkout that lacks a declared render script.</summary>
    public const string MissingScriptsPrefix = "compose-render script missing from the delivery";

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
