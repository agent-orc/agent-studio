using System.Text.Json;

namespace AgentStudio.Secrets;

public sealed record SecretAdapterConfig(string Kind, string? Address, string? Mount, string? Vault,
    string CredentialEnvironment, int CacheTtlSeconds = 30);

public sealed record SecretAdapterStatus(string Scope, string Kind, bool Healthy, string Message,
    int CacheTtlSeconds);

/// <summary>Scope choices contain no credentials and live in the owner-only installation state.</summary>
public sealed class SecretAdapterSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, SecretAdapterConfig> _scopes;

    public SecretAdapterSettings(IConfiguration configuration)
        : this(Path.Combine(SecretStorePaths.DataRoot(configuration), "adapters.json")) { }

    public SecretAdapterSettings(string path)
    {
        _path = path;
        _scopes = new Dictionary<string, SecretAdapterConfig>(File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, SecretAdapterConfig>>(File.ReadAllText(path), Json)
                ?? throw new SecretValidationException("adapter-config-invalid", "Secret adapter settings are invalid.")
            : new Dictionary<string, SecretAdapterConfig>(), StringComparer.OrdinalIgnoreCase);
    }

    public SecretAdapterConfig? For(SecretScope scope)
    {
        lock (_gate) return _scopes.GetValueOrDefault(scope.Key);
    }

    public IReadOnlyDictionary<string, SecretAdapterConfig> Snapshot()
    {
        lock (_gate) return new Dictionary<string, SecretAdapterConfig>(_scopes, StringComparer.OrdinalIgnoreCase);
    }

    public void Set(SecretScope scope, SecretAdapterConfig? value)
    {
        if (value is not null) Validate(value);
        lock (_gate)
        {
            var next = new Dictionary<string, SecretAdapterConfig>(_scopes, StringComparer.OrdinalIgnoreCase);
            if (value is null || value.Kind == BuiltInSecretStore.Id) next.Remove(scope.Key);
            else next[scope.Key] = value;
            OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(_path)!);
            BuiltInSecretStore.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            _scopes = next;
        }
    }

    public static void Validate(SecretAdapterConfig config)
    {
        if (config.Kind == BuiltInSecretStore.Id) return;
        if (config.Kind is not ("vault" or "openbao" or "1password"))
            throw new SecretValidationException("adapter-kind-invalid", "Choose built-in, Vault, OpenBao, or 1Password.");
        if (config.CacheTtlSeconds is < 0 or > 300)
            throw new SecretValidationException("adapter-ttl-invalid", "Cache TTL must be between 0 and 300 seconds.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(config.CredentialEnvironment ?? "", "^[A-Z_][A-Z0-9_]{0,127}$"))
            throw new SecretValidationException("adapter-credential-invalid", "Name an environment variable containing the adapter credential.");
        if (config.Kind == "1password")
        {
            if (string.IsNullOrWhiteSpace(config.Vault) || config.Vault.Length > 128)
                throw new SecretValidationException("adapter-vault-invalid", "A 1Password vault is required.");
            return;
        }
        if (!Uri.TryCreate(config.Address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme == "http" && !uri.IsLoopback))
            throw new SecretValidationException("adapter-address-invalid", "Vault or OpenBao requires an HTTPS address (HTTP only on loopback).");
        if (!System.Text.RegularExpressions.Regex.IsMatch(config.Mount ?? "", "^[A-Za-z0-9][A-Za-z0-9/_-]{0,127}$"))
            throw new SecretValidationException("adapter-mount-invalid", "A KV v2 mount path is required.");
    }
}
