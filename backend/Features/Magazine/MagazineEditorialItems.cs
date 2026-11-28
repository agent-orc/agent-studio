using System.Text.Json.Serialization;
using AgentStudio.Security;

namespace AgentStudio.Magazine;

/// <summary>An editor's scored finding and the evidence for its score.</summary>
public sealed record MagazineEditorialItem(
    string Id,
    string Headline,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Importance,
    string HowDerived,
    IReadOnlyList<string> Evidence,
    string Proposal,
    [property: JsonRequired] bool NeedsOwner);

/// <summary>Validates editorial findings for Magazine writes and report delivery readers.</summary>
public static class MagazineEditorialItems
{
    public const int MaxItems = 8;
    public const int MaxIdLength = 80;
    public const int MaxHeadlineLength = 200;
    public const int MaxExplanationLength = 2000;
    public const int MaxEvidenceEntries = 16;
    public const int MaxEvidenceLength = 1000;

    /// <summary>Validates the supplied items and returns them in lead order.</summary>
    public static IReadOnlyList<MagazineEditorialItem> ValidateAndOrder(IReadOnlyList<MagazineEditorialItem>? items)
    {
        if (items is null) return [];
        if (items.Count > MaxItems)
            throw new MagazineException(StatusCodes.Status400BadRequest, "magazine-items-too-many",
                $"An article holds at most {MaxItems} editorial items.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null)
                throw Invalid("Each editorial item must be an object.");
            ValidateText(item.Id, "item ID", MaxIdLength);
            if (item.Id.Any(char.IsWhiteSpace) || item.Id.Any(char.IsControl))
                throw Invalid("Item IDs must be free of whitespace and control characters.");
            if (!ids.Add(item.Id))
                throw Invalid("Item IDs must be unique within an article.");
            if (item.Importance is < 0 or > 100)
                throw Invalid("Item importance must be an integer from 0 to 100.");

            ValidateText(item.Headline, "headline", MaxHeadlineLength);
            ValidateText(item.HowDerived, "score rationale", MaxExplanationLength);
            ValidateText(item.Proposal, "proposal", MaxExplanationLength);
            if (item.Evidence is null || item.Evidence.Count > MaxEvidenceEntries)
                throw Invalid($"Item evidence must be an array of at most {MaxEvidenceEntries} text entries.");
            foreach (var entry in item.Evidence)
                ValidateText(entry, "evidence entry", MaxEvidenceLength);
        }

        return items.OrderByDescending(item => item.Importance)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateText(string? text, string field, int maxLength)
    {
        if (text is null || text.Length > maxLength || string.IsNullOrWhiteSpace(text) || text.Contains('\0'))
            throw Invalid($"Each {field} must contain text of at most {maxLength} characters without NUL characters.");
        if (CredentialRedactor.ContainsToken(text))
            throw new MagazineException(StatusCodes.Status400BadRequest, "magazine-credential-in-content",
                "An editorial item contains a credential-shaped value. Remove it and revoke it if it is real.");
    }

    private static MagazineException Invalid(string message)
        => new(StatusCodes.Status400BadRequest, "magazine-item-invalid", message);
}
