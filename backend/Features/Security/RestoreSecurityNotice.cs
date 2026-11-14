using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Security;

public sealed record RestoreNotice(string ArchiveId, string ArchiveCreatedAt, DateTimeOffset RestoredAt);

/// <summary>Records the security gap left by restoring an older access snapshot.</summary>
public static class RestoreSecurityNotice
{
    private const string FileName = "restore-notice.json";

    public static void MarkRestored(string workspace, string archiveId, string archiveCreatedAt)
    {
        var folder = Path.Combine(workspace, ".security");
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Combine(folder, "access-state.json");
        if (File.Exists(statePath))
        {
            var state = JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject
                ?? throw new InvalidDataException("Restored access state is not a JSON object.");
            // Remove every archived browser session. This also covers sessions
            // issued after the archive on a host that was later lost.
            foreach (var property in state.Select(pair => pair.Key)
                         .Where(key => key.Equals("Sessions", StringComparison.OrdinalIgnoreCase)).ToArray())
                state.Remove(property);
            state["Sessions"] = new JsonArray();
            WriteAtomic(statePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        var notice = new RestoreNotice(archiveId, archiveCreatedAt, DateTimeOffset.UtcNow);
        WriteAtomic(Path.Combine(folder, FileName), JsonSerializer.Serialize(notice));
    }

    public static RestoreNotice? Read(string securityFolder)
    {
        var path = Path.Combine(securityFolder, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<RestoreNotice>(File.ReadAllText(path)) : null;
    }

    public static void Acknowledge(string securityFolder, string archiveId)
    {
        var notice = Read(securityFolder);
        if (notice is null || notice.ArchiveId != archiveId)
            throw new SecurityOperationException(409, "restore-notice-changed", "The restore notice changed. Sign in again to review it.");
        File.Delete(Path.Combine(securityFolder, FileName));
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }
}
