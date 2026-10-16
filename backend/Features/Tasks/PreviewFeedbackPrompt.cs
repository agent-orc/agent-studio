using System.Text;

namespace AgentStudio.Tasks;

/// <summary>Builds the next-round instruction from a marked preview image.</summary>
public static class PreviewFeedbackPrompt
{
    public static string? Validate(string? comment, string? previewAddress, string? selector)
    {
        if (string.IsNullOrWhiteSpace(comment) || comment.Length > 4000)
            return "A comment of at most 4000 characters is required.";
        if (string.IsNullOrWhiteSpace(previewAddress) || previewAddress.Length > 2048
            || !Uri.TryCreate(previewAddress, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return "A valid HTTP preview address is required.";
        if (selector?.Length > 500)
            return "The element selector is too long.";
        return null;
    }

    public static string Build(string comment, string previewAddress, string? selector, string cropPath)
    {
        var text = new StringBuilder();
        text.AppendLine("Preview feedback for this card's next fix round:");
        text.AppendLine($"Preview address: {previewAddress.Trim()}");
        if (!string.IsNullOrWhiteSpace(selector))
            text.AppendLine($"Element selector: {selector.Trim()}");
        text.AppendLine($"Screenshot crop: {cropPath}");
        text.AppendLine("Requested change:");
        text.AppendLine(comment.Trim());
        text.AppendLine("Use the crop and preview address to locate the affected area. Keep this work on the same card. Capture before and after screenshots of each changed page in results/.");
        text.AppendLine("Use the project screenshot-critique workflow to check the changed pages against accepted design references.");
        return text.ToString();
    }
}
