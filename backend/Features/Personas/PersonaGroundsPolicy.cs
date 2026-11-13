namespace AgentStudio.Personas;

/// <summary>Maps a bounded source summary to one area the role is allowed to inspect.</summary>
public static class PersonaGroundsPolicy
{
    // A repository-root clone cannot honor narrower areas or excluded paths.
    public static bool CanCloneRepository(PersonaActivationV1 activation)
        => activation.Descriptor.Scope.Areas.Contains("repository.root", StringComparer.Ordinal)
           && activation.Scope.Areas.Contains("repository.root", StringComparer.Ordinal)
           && activation.Scope.ExcludedPaths is not { Count: > 0 };

    public static bool PathAllowed(PersonaActivationV1 activation, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return (activation.Scope.ExcludedPaths ?? []).All(excluded =>
        {
            var prefix = excluded.Replace('\\', '/').Trim('/');
            return !normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    public static IReadOnlyList<(string? Id, string? Name)> QuotaScopes(PersonaActivationV1 activation,
        IReadOnlyList<(string Id, string Name)> projects)
        => activation.Scope.Projects.Contains("*", StringComparer.Ordinal)
            ? [(null, null)]
            : projects.Where(project => activation.Scope.Projects.Contains(project.Id, StringComparer.OrdinalIgnoreCase)
                || activation.Scope.Projects.Contains(project.Name, StringComparer.OrdinalIgnoreCase))
                .Select(project => ((string?)project.Id, (string?)project.Name)).ToArray();

    public static bool CanReadWorkspaceIssue(PersonaActivationV1 activation)
        => activation.Scope.Projects.Contains("*", StringComparer.Ordinal) &&
           activation.Scope.Areas.Contains("magazine.issues", StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, (string Area, string[] Terms)[]> Rules =
        new Dictionary<string, (string, string[])[]>(StringComparer.Ordinal)
        {
            ["security-officer"] = [("security.code", ["security", "vulnerab", "exploit"]),
                ("security.dependencies", ["dependenc", "package"]), ("security.permissions", ["permission", "grant", "iam"]),
                ("security.prompts", ["prompt", "injection"])],
            ["reliability-engineer"] = [("runs.failures", ["run", "failure"]),
                ("runs.stalls", ["stall"]), ("runs.escalations", ["escalat"]),
                ("batches.gate", ["batch", "gate"]), ("api.latency", ["latency", "performance", "slow"]),
                ("hosts.load", ["host", "runner"])],
            ["quality-lead"] = [("review.rounds", ["review", "round"]), ("tests.gaps", ["test", "flaky"]),
                ("review.evidence", ["evidence", "proof"])],
            ["design-lead"] = [("frontend.visual", ["ui", "visual", "screen", "style", "theme", "design"])],
            ["product-manager"] = [("product.roadmap", ["roadmap", "goal"]),
                ("dossiers.decisions", ["decision", "dossier", "concept"]), ("board.backlog", ["backlog", "outcome"])],
            ["documentation-editor"] = [("docs.drift", ["doc", "readme", "guide"]),
                ("docs.links", ["link"]), ("docs.index", ["index"])],
            ["improvement-scout"] = [("improvement.delivery-integrity", ["delivery", "integration", "fenc", "stale"]),
                ("improvement.review-quality", ["review", "evidence", "test"]),
                ("improvement.capacity-economics", ["capacity", "quota", "cost", "wait", "runner"]),
                ("improvement.credentials-operations", ["credential", "backup", "operation"])],
        };

    public static string? Area(PersonaActivationV1 activation, string summary)
    {
        var role = activation.RoleId ?? activation.Descriptor.Id;
        if (!Rules.TryGetValue(role, out var rules)) return null;
        return rules.Where(rule => activation.Scope.Areas.Contains(rule.Area, StringComparer.Ordinal)
                && activation.Descriptor.Scope.Areas.Contains(rule.Area, StringComparer.Ordinal))
            .FirstOrDefault(rule => rule.Terms.Any(term => summary.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Area;
    }

    public static IReadOnlyList<PersonaGroundsSourceV1> Filter(PersonaActivationV1 activation,
        IEnumerable<PersonaGroundsSourceV1> sources)
        => sources.Where(source => source.Area is null
            ? source.Kind is "typed-fact" or "prior-outcome"
            : activation.Scope.Areas.Contains(source.Area, StringComparer.Ordinal) &&
              activation.Descriptor.Scope.Areas.Contains(source.Area, StringComparer.Ordinal)).ToArray();
}
