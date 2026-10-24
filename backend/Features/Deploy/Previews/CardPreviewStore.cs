using System.Text.Json;

namespace AgentStudio.Deploy;

public sealed record CardPreviewState(TaskPreview Preview, PreviewJobSpec Spec)
{
    public string? RemovalReason { get; init; }
}

/// <summary>One sidecar follows the task folder through lane moves.</summary>
public static class CardPreviewStore
{
    public const string FileName = "card-preview.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathFor(string taskFolder) => Path.Combine(taskFolder, FileName);

    public static CardPreviewState? Read(string taskFolder)
    {
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CardPreviewState>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "CardPreviewStore: unreadable preview sidecar");
            return null;
        }
    }

    public static void Write(string taskFolder, CardPreviewState preview)
    {
        var path = PathFor(taskFolder);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preview, Json));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Delete(string taskFolder)
    {
        var path = PathFor(taskFolder);
        if (File.Exists(path)) File.Delete(path);
    }
}
