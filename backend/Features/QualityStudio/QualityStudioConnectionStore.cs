using AgentStudio.TaskServer.Contracts;
using System.Text.Json;

namespace AgentStudio.QualityStudio;

public sealed record QualityStudioConnection(string InternalBaseUrl, string PublicUrl, string ServiceClientId);

/// <summary>Workspace integration coordinates only. Credentials live in the secret provider.</summary>
public sealed class QualityStudioConnectionStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public QualityStudioConnectionStore(IConfiguration configuration)
    {
        var root = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
            root = LocalStateRoot.Combine("agent-studio");
        _path = Path.Combine(root, ".security", "quality-studio-connection.json");
    }

    public QualityStudioConnection? Get()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<QualityStudioConnection>(File.ReadAllText(_path), JsonSerializerOptions.Web);
        }
    }

    public void Set(QualityStudioConnection connection)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(_path)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(connection, JsonSerializerOptions.Web));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
