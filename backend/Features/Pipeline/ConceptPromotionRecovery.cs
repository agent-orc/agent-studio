using System.Text.Json;
using AgentStudio.Shared;

namespace AgentStudio.Pipeline;

public static class ConceptPromotionRecoveryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string PathFor(string folder) => Path.Combine(folder, ".metadata", "concept-promotion.json");

    public static ConceptPromotionRecovery? Read(string folder)
    {
        var path = PathFor(folder);
        return File.Exists(path) ? JsonSerializer.Deserialize<ConceptPromotionRecovery>(File.ReadAllText(path), Json) : null;
    }

    public static void Write(string folder, ConceptPromotionRecovery state)
    {
        var path = PathFor(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
