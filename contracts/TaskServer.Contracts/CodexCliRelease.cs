namespace AgentStudio.TaskServer.Contracts;

/// <summary>Verified CLI floor for GPT-6.1 on a ChatGPT login.</summary>
public static class CodexCliRelease
{
    public const string Gpt61MinimumVersion = "0.166.0";
    public const string Gpt61Sol = "gpt-6.1-sol";
    public const string Gpt61Terra = "gpt-6.1-terra";

    public static string? MinimumVersionForModel(string? model)
        => model is Gpt61Sol or Gpt61Terra ? Gpt61MinimumVersion : null;

    public static bool OffersGpt61(string? version)
        => Version.TryParse(version, out var installed)
           && installed >= Version.Parse(Gpt61MinimumVersion);
}
