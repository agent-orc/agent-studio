using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The document is versioned data. Code owns predicate semantics, step order,
/// and adapter names. A changed document fails closed until code reviews it.
/// </summary>
public static class CredentialRunbookCatalog
{
    private static readonly IReadOnlyDictionary<string, string[]> Steps = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["RB-CLAUDE-ROTATION"] = ["discover_effective_source", "classify_provider_access", "begin_host_login",
            "stage_local_secret", "install_generation", "reconcile_units_safely", "verify_real_request",
            "advertise_generation", "retire_old_generation"],
        ["RB-CODEX-INCIDENT"] = ["discover_effective_source", "verify_real_request", "correlate_incident",
            "pause_affected_claims", "schedule_bounded_retry", "verify_recovery_canary", "advertise_generation"],
        ["RB-GITHUB-WORKSPACE"] = ["discover_repository_binding", "probe_exact_origin", "generate_host_key",
            "register_public_key", "verify_repository_access", "switch_repository_identity", "retire_old_generation"]
    };

    private static readonly HashSet<string> HumanBoundaries = new(StringComparer.Ordinal)
    {
        "begin_host_login", "register_public_key"
    };

    private static readonly IReadOnlyDictionary<string, string> Outcomes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RB-CLAUDE-ROTATION"] = "credential_invalid",
            ["RB-CODEX-INCIDENT"] = "provider_incident",
            ["RB-GITHUB-WORKSPACE"] = "credential_invalid"
        };

    static CredentialRunbookCatalog()
    {
        var assembly = typeof(CredentialRunbookCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Runbooks.runbooks.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidOperationException("Unsupported runbook catalogue schema.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runbook in root.GetProperty("runbooks").EnumerateArray())
        {
            var id = runbook.GetProperty("id").GetString()!;
            var provenance = runbook.GetProperty("provenance");
            var execution = runbook.GetProperty("executionPolicy");
            if (!Steps.TryGetValue(id, out var expected) || !seen.Add(id) ||
                runbook.GetProperty("version").GetInt32() != 1 ||
                runbook.GetProperty("targetOutcome").GetString() != Outcomes[id] ||
                provenance.GetProperty("type").GetString() != "operator-report" ||
                provenance.GetProperty("independentlyReproduced").GetBoolean() ||
                !execution.GetProperty("generationFenceRequired").GetBoolean() ||
                !execution.GetProperty("durableIdempotencyRequired").GetBoolean() ||
                execution.GetProperty("secretValuesAllowedInReceipts").GetBoolean() ||
                !runbook.GetProperty("platformSteps").EnumerateArray()
                    .Select(step => step.GetString()).SequenceEqual(expected))
                throw new InvalidOperationException("Runbook data differs from its allowlisted v1 adapter contract.");
        }
        if (seen.Count != Steps.Count)
            throw new InvalidOperationException("Runbook data is missing an allowlisted runbook.");
    }

    public static CredentialRunbookSelection? Classify(CredentialIncidentFacts facts)
    {
        if (facts.CredentialKind is "claude_native_login" or "claude_oauth_token"
            && facts.RefreshSessionSharedAcrossHosts == true
            && facts.ProbeOutcome == "credential_invalid")
            return new("RB-CLAUDE-ROTATION", 1, "guided-repair");
        if (facts.CredentialKind == "codex_chatgpt_login" && facts.NormalizedRequestCode == "unauthorized")
            return facts.ProbeOutcome == "provider_incident" &&
                   facts.IncidentCorroboration is "official-applicable" or "independent-known-good-match"
                ? new("RB-CODEX-INCIDENT", 1, "bounded-retry")
                : new("RB-CODEX-INCIDENT", 1, "diagnosis-only");
        if (facts.CredentialKind is "github_https_token" or "github_provisioning_oauth" or "github_deploy_key"
            && facts.RepositoryPurpose == "workspace" && facts.RepositoryRequiredAccessVerified == false
            && facts.ProbeOutcome == "credential_invalid")
            return new("RB-GITHUB-WORKSPACE", 1, "guided-repair");
        return null;
    }

    public static IReadOnlyList<string> StepsFor(string runbookId, string policy)
    {
        var steps = Steps[runbookId];
        return policy == "diagnosis-only" ? steps.Take(3).ToArray() : steps;
    }

    public static bool RequiresHuman(string stepId) => HumanBoundaries.Contains(stepId);
}
