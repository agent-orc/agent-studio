namespace AgentStudio.Cli;

/// <summary>
/// Pure pre-spawn guard for explicitly pinned models. Claude uses the
/// registry's minimum CLI version while Codex uses the installed CLI's live
/// catalogue. Unknown capability evidence fails open so a probe outage does
/// not turn into a false model rejection; the post-run observed-model guard is
/// the second line of defence.
/// </summary>
public static class ModelPinAdmissionPolicy
{
    public const string RejectionCode = "model-unsupported";

    public static ModelPinAdmissionDecision Evaluate(
        string? cliType,
        string? model,
        bool explicitlyPinned,
        string? installedCliVersion,
        IReadOnlyCollection<string>? supportedModels = null)
    {
        if (!explicitlyPinned || string.IsNullOrWhiteSpace(model))
            return ModelPinAdmissionDecision.Allowed;

        var cli = CliTypes.Normalize(cliType);
        var requested = model.Trim();
        var metadata = ModelMetadataRegistry.Find(requested);
        var installedVersion = ExtractSemanticVersion(installedCliVersion);

        if (string.Equals(cli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
        {
            if (metadata is null || !string.Equals(metadata.Vendor, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return ModelPinAdmissionDecision.Reject(
                    $"model '{requested}' is not registered for the Claude CLI");
            }

            if (!string.IsNullOrWhiteSpace(metadata.MinimumCliVersion)
                && SemanticCliVersion.TryCompare(
                    installedVersion,
                    metadata.MinimumCliVersion,
                    out var comparison)
                && comparison < 0)
            {
                return ModelPinAdmissionDecision.Reject(
                    $"model unsupported by installed CLI {installedVersion} " +
                    $"(minimum {metadata.MinimumCliVersion.Trim()})");
            }

            return ModelPinAdmissionDecision.Allowed;
        }

        if (string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
            && supportedModels is not null
            && !ContainsModel(supportedModels, requested, metadata?.Id))
        {
            var version = string.IsNullOrWhiteSpace(installedVersion)
                ? "unknown version"
                : installedVersion;
            return ModelPinAdmissionDecision.Reject(
                $"model '{requested}' unsupported by installed codex-cli {version} " +
                "(not offered by live catalogue)");
        }

        return ModelPinAdmissionDecision.Allowed;
    }

    private static string? ExtractSemanticVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"(?<![0-9A-Za-z])v?\d+(?:\.\d+){1,2}(?:-[0-9A-Za-z.-]+)?",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Value.TrimStart('v', 'V') : value.Trim();
    }

    private static bool ContainsModel(
        IEnumerable<string> supportedModels,
        string requested,
        string? canonicalRequested)
    {
        foreach (var candidate in supportedModels)
        {
            if (string.Equals(candidate?.Trim(), requested, StringComparison.OrdinalIgnoreCase)) return true;
            var canonicalCandidate = ModelMetadataRegistry.Find(candidate)?.Id ?? candidate?.Trim();
            if (!string.IsNullOrWhiteSpace(canonicalRequested)
                && string.Equals(canonicalCandidate, canonicalRequested, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

public sealed record ModelPinAdmissionDecision(bool IsAllowed, string? Code, string? Reason)
{
    public static ModelPinAdmissionDecision Allowed { get; } = new(true, null, null);

    public static ModelPinAdmissionDecision Reject(string reason)
        => new(false, ModelPinAdmissionPolicy.RejectionCode, reason);
}
