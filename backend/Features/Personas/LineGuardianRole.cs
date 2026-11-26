using System.Text.Json;

namespace AgentStudio.Personas;

/// <summary>The guardian uses the DS-5 role contract, independent of dispatcher strategies.</summary>
public static class LineGuardianRole
{
    public static JsonElement Descriptor { get; } = Load("role");
    public static JsonElement Rights { get; } = Load("grants");
    private static JsonElement Load(string name)
    {
        using var stream = typeof(LineGuardianRole).Assembly.GetManifestResourceStream(
            $"Policies.personas.line-guardian.{name}.v1.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
