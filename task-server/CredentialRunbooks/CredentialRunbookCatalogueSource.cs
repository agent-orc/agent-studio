using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The catalogue is docs/credentials-and-logins/runbooks.json, embedded at build time so
/// the reviewed data is the executed data. Parsing rejects any step that is not an
/// allowlisted opcode.
/// </summary>
internal static class CredentialRunbookCatalogueSource
{
    public const string ResourceName = "credential-runbooks.json";

    private static readonly Lazy<CredentialRunbookCatalogueDto> Embedded = new(() =>
    {
        using var stream = typeof(CredentialRunbookCatalogueSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The credential runbook catalogue is not embedded.");
        using var reader = new StreamReader(stream);
        return CredentialRunbookPolicy.Parse(reader.ReadToEnd());
    });

    public static CredentialRunbookCatalogueDto Current => Embedded.Value;
}
