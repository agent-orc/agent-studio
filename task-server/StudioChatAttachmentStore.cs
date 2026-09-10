using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Disk-backed storage for orchestrator chat image attachments, one
/// directory per project. Filenames are server-generated so a resolved path
/// never depends on caller-controlled characters beyond the extension.
/// </summary>
public sealed class StudioChatAttachmentStore(TaskServerStore store)
{
    private const long MaxBytes = 10 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> AllowedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };

    public async Task<ChatAttachmentUploadResponse> SaveAsync(
        string projectId, string originalFileName, Stream content, CancellationToken ct)
    {
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedContentTypes.ContainsKey(extension))
            throw new ArgumentException("Unsupported file type - only png, jpg, gif, webp allowed.");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            throw new ArgumentException("Empty file.");
        if (buffer.Length > MaxBytes)
            throw new ArgumentException("File too large (max 10 MB).");

        var directory = AttachmentsDirectory(projectId);
        Directory.CreateDirectory(directory);
        var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        await File.WriteAllBytesAsync(Path.Combine(directory, storedName), buffer.ToArray(), ct);

        return new ChatAttachmentUploadResponse(
            storedName,
            $"chat-attachments/{projectId}/{storedName}",
            $"/api/v1/studio/runner/{Uri.EscapeDataString(projectId)}/orchestrator-chat/attachments/{storedName}");
    }

    public (string Path, string ContentType)? Resolve(string projectId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('/')
            || fileName.Contains('\\'))
            return null;
        var path = Path.Combine(AttachmentsDirectory(projectId), fileName);
        if (!File.Exists(path)) return null;
        var contentType = AllowedContentTypes.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");
        return (path, contentType);
    }

    private string AttachmentsDirectory(string projectId)
        => Path.Combine(store.DataDirectory, "chat-attachments", projectId);
}
