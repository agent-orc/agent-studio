using System.Text.RegularExpressions;
using AgentStudio.Pipeline;

namespace AgentStudio.Deploy;

public enum PreviewLifecycleAction { None, Create, Update, Sync, Remove }

public static class CardPreviewPolicy
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly Regex Label = new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

    public static string Address(string cardKey, string project, string domain)
    {
        var card = Slug(cardKey);
        var name = Slug(project);
        var baseDomain = domain.Trim().Trim('.').ToLowerInvariant();
        if (baseDomain.Length > 190 || baseDomain.Split('.').Length < 2
            || baseDomain.Split('.').Any(label => !Label.IsMatch(label)))
            throw new ArgumentException("Preview base domain must be a DNS name.", nameof(domain));
        return $"https://{card}.{name}.{baseDomain}";
    }

    public static string Slug(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 63) slug = slug[..63].TrimEnd('-');
        if (!Label.IsMatch(slug)) throw new ArgumentException("Invalid preview label.", nameof(value));
        return slug;
    }

    public static bool ShouldRemove(bool integrated, bool rejected, DateTime updatedAtUtc, DateTime nowUtc)
        => integrated || rejected || nowUtc >= updatedAtUtc + Lifetime;

    public static PreviewLifecycleAction Decide(TaskPreview? current, string? greenDeliverySha,
        bool integrated, bool rejected, DateTime nowUtc)
    {
        if (current is null)
            return greenDeliverySha is not null && !integrated && !rejected
                ? PreviewLifecycleAction.Create : PreviewLifecycleAction.None;
        if (current.Status is "expired" or "removed")
            return greenDeliverySha is not null && !integrated && !rejected
                && !string.Equals(current.DeliverySha, greenDeliverySha, StringComparison.OrdinalIgnoreCase)
                ? PreviewLifecycleAction.Update : PreviewLifecycleAction.None;
        if (current.Status == "removing" || integrated || rejected)
            return PreviewLifecycleAction.Remove;
        if (greenDeliverySha is not null
            && !string.Equals(current.DeliverySha, greenDeliverySha, StringComparison.OrdinalIgnoreCase))
            return PreviewLifecycleAction.Update;
        return ShouldRemove(false, false, current.UpdatedAtUtc, nowUtc)
            ? PreviewLifecycleAction.Remove : PreviewLifecycleAction.Sync;
    }

    public static bool IsGreenDelivery(ReviewSubjectRecord? subject, ReviewVerificationRecord? review)
        => subject is not null && review is not null
            && ReviewSubjectStore.IsValidResultSha(subject.ResultSha)
            && string.Equals(subject.ResultSha, review.ResultSha, StringComparison.OrdinalIgnoreCase)
            && review.BuildTestGate == ReviewBuildTestGateClasses.Passed;

    public static bool SecretsIsolated(IEnumerable<string> previewSecrets,
        IEnumerable<string> stagingAndProductionSecrets)
    {
        var protectedNames = stagingAndProductionSecrets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !previewSecrets.Any(protectedNames.Contains);
    }
}
