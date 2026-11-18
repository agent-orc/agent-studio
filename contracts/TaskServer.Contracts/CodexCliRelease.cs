namespace AgentStudio.TaskServer.Contracts;

/// <summary>Verified CLI floors for the GPT-6.1 and GPT-6.2 releases on a ChatGPT login.</summary>
public static class CodexCliRelease
{
    public const string Gpt61MinimumVersion = "0.166.0";
    public const string Gpt61Sol = "gpt-6.1-sol";
    public const string Gpt61Terra = "gpt-6.1-terra";

    /// <summary>codex-cli 0.166.0 is the first release verified to run GPT-6.2
    /// (runner-01 probe, 2026-11-17); older releases are not proven.</summary>
    public const string Gpt62MinimumVersion = "0.166.0";
    public const string Gpt62Sol = "gpt-6.2-sol";
    public const string Gpt62Astra = "gpt-6.2-astra";

    /// <summary>Models a host probes and admits only at or above their verified floor.</summary>
    public static readonly string[] VersionGatedModels = [Gpt61Sol, Gpt61Terra, Gpt62Sol, Gpt62Astra];

    public static string? MinimumVersionForModel(string? model)
        => model switch
        {
            Gpt61Sol or Gpt61Terra => Gpt61MinimumVersion,
            Gpt62Sol or Gpt62Astra => Gpt62MinimumVersion,
            _ => null,
        };

    public static bool OffersGpt61(string? version) => Offers(Gpt61Sol, version);

    /// <summary>Whether the installed version is at or above the model's floor.
    /// A model without a floor is not gated here.</summary>
    public static bool Offers(string? model, string? version)
        => MinimumVersionForModel(model) is not { } minimum
           || (Version.TryParse(version, out var installed) && installed >= Version.Parse(minimum));
}
