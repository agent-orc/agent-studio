namespace AgentStudio.Secrets;

public interface IExternalSecretBackend
{
    string Kind { get; }
    Task ProbeAsync(CancellationToken ct);
    Task<int> WriteAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value, int previousVersion, CancellationToken ct);
    Task<byte[]> ReadAsync(SecretScope scope, string name, int version, CancellationToken ct);
    Task RevokeAsync(SecretScope scope, string name, IReadOnlyCollection<int> versions, CancellationToken ct);
    Task DeleteAsync(SecretScope scope, string name, CancellationToken ct);
}

public sealed class SecretAdapterException(string code, string message) : SecretValidationException(code, message);

public static class SecretAdapterCredentials
{
    public static string Require(string environmentName, Func<string, string?>? readEnvironment = null)
    {
        var value = (readEnvironment ?? Environment.GetEnvironmentVariable)(environmentName);
        if (string.IsNullOrWhiteSpace(value))
            throw new SecretAdapterException("adapter-credential-missing", "The adapter credential environment variable is missing.");
        CredentialRedactor.RegisterLiteral(value, pinned: true);
        return value;
    }
}
