using AgentStudio.TaskServer.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace AgentStudio.Dispatch;

/// <summary>Dated project indexes and per-card forecasts, separate from platform-owned task folders.</summary>
public sealed class DispatchStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly object _indexLock = new();
    private static readonly TimeSpan IndexRetention = TimeSpan.FromDays(60);
    private const string IndexDateFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";
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
        lock (_indexLock)
            return IndexFiles(project).Where(file => file.AsOf <= at)
                .OrderByDescending(file => file.Path, StringComparer.Ordinal)
                .Select(file => Read<CoChangeIndex>(file.Path))
                .FirstOrDefault(index => index is not null && index.AsOf <= at);
    }
    public void WriteIndex(string project, CoChangeIndex index)
    {
        lock (_indexLock)
        {
            Write(Path.Combine(Project(project), "indexes",
                $"{index.AsOf.UtcDateTime.ToString(IndexDateFormat, CultureInfo.InvariantCulture)}-{index.IntegrationTip}.json"), index);
            var files = IndexFiles(project).OrderByDescending(file => file.Path, StringComparer.Ordinal).ToArray();
            var cutoff = files.Max(file => file.AsOf) - IndexRetention;
            // Retain the predecessor so a wave at the window boundary can still
            // select the index it would have seen. Backfills cannot reset the window.
            foreach (var expired in files.Where(file => file.AsOf < cutoff).Skip(1))
                File.Delete(expired.Path);
        }
    }
    private IEnumerable<(string Path, DateTimeOffset AsOf)> IndexFiles(string project)
    {
        var folder = Path.Combine(Project(project), "indexes");
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            var name = Path.GetFileName(path);
            if (name.Length > 23 && DateTimeOffset.TryParseExact(name[..23], IndexDateFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                yield return (path, at);
        }
    }
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
