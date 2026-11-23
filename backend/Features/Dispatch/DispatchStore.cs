using AgentStudio.TaskServer.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Dispatch;

/// <summary>Dated project indexes and per-card forecasts, separate from platform-owned task folders.</summary>
public sealed class DispatchStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    public DispatchStore(IConfiguration configuration)
    {
        _root = configuration["Dispatch:StoragePath"] ?? Path.Combine(configuration["TaskRepository"]
            ?? LocalStateRoot.Combine("agentstudio"), ".dispatch");
    }
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string Project(string project) => Path.Combine(_root, Key(project.ToUpperInvariant()));
    private string Card(string project, string card) => Path.Combine(Project(project), "cards", Key(card) + ".json");
    public TouchForecastV1? Read(string project, string card) => Read<TouchForecastV1>(Card(project, card));
    public void Write(string project, string card, TouchForecastV1 forecast) => Write(Card(project, card), forecast);
    public CoChangeIndex? IndexAsOf(string project, DateTimeOffset at)
    {
        var folder = Path.Combine(Project(project), "indexes");
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, "*.json").OrderDescending(StringComparer.Ordinal)
            .Select(Read<CoChangeIndex>).FirstOrDefault(index => index is not null && index.AsOf <= at);
    }
    public void WriteIndex(string project, CoChangeIndex index) => Write(Path.Combine(Project(project), "indexes",
        $"{index.AsOf:yyyyMMddTHHmmssfffffffZ}-{index.IntegrationTip}.json"), index);
    private static T? Read<T>(string path) where T : class => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
