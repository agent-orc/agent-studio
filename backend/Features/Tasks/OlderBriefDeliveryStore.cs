using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>A settled remote result retained for an explicit decision after the card brief changed.</summary>
public sealed record OlderBriefDeliveryOffer(
    string AttemptId,
    string BriefVersion,
    string CurrentBriefVersion,
    string? ResultSha,
    string? ResultRef,
    string? SalvageBranch,
    string? SalvageCommitSha,
    DateTime OfferedAtUtc,
    string Status = "pending",
    DateTime? DecidedAtUtc = null);

public static class OlderBriefDeliveryStore
{
    private const string RelativePath = "results/older-brief-delivery.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static OlderBriefDeliveryOffer? Read(string folder)
    {
        var path = Path.Combine(folder, RelativePath);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<OlderBriefDeliveryOffer>(File.ReadAllText(path), Json)
            : null;
    }

    public static void Write(string folder, OlderBriefDeliveryOffer offer)
    {
        var path = Path.Combine(folder, RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(offer, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
