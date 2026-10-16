using System.Text.RegularExpressions;

namespace AgentStudio.Projects.Previews;

public enum PreviewOperation
{
    None,
    Create,
    Update,
    Remove,
}

public sealed record PreviewEnvironment(
    string ProjectCode,
    string CardKey,
    string DeliverySha,
    string ArtifactDigest,
    Uri Address,
    DateTimeOffset LastChangedAt,
    string? ScreenshotPath = null);

public sealed record PreviewFacts(
    string ProjectCode,
    string CardKey,
    string? DeliverySha,
    string? ArtifactDigest,
    bool WebProject,
    bool ChangesVisibleOutput,
    bool GreenBuild,
    bool Integrated,
    bool Rejected,
    bool HostingQuotaAdmitted,
    DateTimeOffset LastChangedAt);

public sealed record PreviewDecision(PreviewOperation Operation, string Reason, PreviewEnvironment? Desired);

/// <summary>
/// The deploy job applies this decision only after validating its current card
/// and gate subject. A newer delivery always replaces the old preview at the
/// same address, while removal does not remove the immutable artifact.
/// </summary>
public static partial class PreviewEnvironmentPolicy
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(7);
    public const string DefaultBaseDomain = "preview.internal";

    public static PreviewDecision Decide(
        PreviewEnvironment? current,
        PreviewFacts facts,
        DateTimeOffset now,
        string? baseDomain = null)
    {
        if (current is not null
            && (!string.Equals(current.ProjectCode, facts.ProjectCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.CardKey, facts.CardKey, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The current preview belongs to a different card.", nameof(current));

        if (current is not null && (facts.Integrated || facts.Rejected))
            return new(PreviewOperation.Remove, facts.Integrated ? "integrated" : "rejected", null);

        if (current is not null && now - facts.LastChangedAt >= IdleLifetime)
            return new(PreviewOperation.Remove, "expired", null);

        if (facts.Integrated || facts.Rejected)
            return new(PreviewOperation.None, "closed", null);

        if (!facts.WebProject || !facts.ChangesVisibleOutput)
            return new(PreviewOperation.None, "ineligible", null);

        if (!facts.GreenBuild || string.IsNullOrWhiteSpace(facts.DeliverySha)
            || string.IsNullOrWhiteSpace(facts.ArtifactDigest))
            return new(PreviewOperation.None, "awaiting-green-delivery", null);

        if (!facts.HostingQuotaAdmitted)
            return new(PreviewOperation.None, "hosting-quota", null);

        var address = AllocateAddress(facts.CardKey, facts.ProjectCode, baseDomain);
        var desired = new PreviewEnvironment(
            facts.ProjectCode, facts.CardKey, facts.DeliverySha, facts.ArtifactDigest,
            address, facts.LastChangedAt);

        if (current is null)
            return new(PreviewOperation.Create, "first-green-build", desired);

        if (string.Equals(current.DeliverySha, desired.DeliverySha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.ArtifactDigest, desired.ArtifactDigest, StringComparison.OrdinalIgnoreCase))
            return new(PreviewOperation.None, "current-delivery", current);

        return new(PreviewOperation.Update, "new-delivery", desired);
    }

    public static Uri AllocateAddress(string cardKey, string projectCode, string? baseDomain = null)
    {
        var card = Label(cardKey, nameof(cardKey));
        var project = Label(projectCode, nameof(projectCode));
        var domain = (baseDomain ?? DefaultBaseDomain).Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length > 253 || domain.Split('.').Length < 2
            || domain.Split('.').Any(part => !DnsLabel().IsMatch(part)))
            throw new ArgumentException("The preview base domain must contain valid DNS labels.", nameof(baseDomain));

        return new Uri($"https://{card}.{project}.{domain}/", UriKind.Absolute);
    }

    private static string Label(string value, string parameter)
    {
        var label = value.Trim().ToLowerInvariant();
        if (!DnsLabel().IsMatch(label))
            throw new ArgumentException("Preview card and project identifiers must be DNS labels.", parameter);
        return label;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DnsLabel();
}

public sealed record PreviewSecretReference(string Name, string Environment);

public static class PreviewSecretScope
{
    public static bool Allows(IEnumerable<PreviewSecretReference> references) =>
        references.All(reference =>
            !string.IsNullOrWhiteSpace(reference.Name)
            && string.Equals(reference.Environment, "preview", StringComparison.OrdinalIgnoreCase));
}
