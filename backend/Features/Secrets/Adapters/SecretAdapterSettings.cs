using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>Non-secret adapter bindings. Credentials stay in the process environment or built-in store.</summary>
public sealed record SecretAdapterBinding(string Scope, string Kind, string Endpoint, string Container,
    string Credential, int CacheSeconds = 30);

public sealed record SecretAdapterHealth(string Scope, string Kind, bool Reachable, string? Problem);

public sealed class SecretAdapterSettings
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SecretAdapterSettings(IConfiguration configuration)
        : this(Path.Combine(SecretStorePaths.DataRoot(configuration), "adapter-bindings.json")) { }

    public SecretAdapterSettings(string path) => _path = path;

    public bool HasBindings => Read().Count > 0;

    public async Task<IReadOnlyList<SecretAdapterBinding>> ReadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return Read(); }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(SecretAdapterBinding binding, CancellationToken ct = default)
    {
        Validate(binding);
        await _gate.WaitAsync(ct);
        try
        {
            var bindings = Read().Where(item => !string.Equals(item.Scope, binding.Scope, StringComparison.OrdinalIgnoreCase)).ToList();
            bindings.Add(binding);
            Write(bindings);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(SecretScope scope, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { Write(Read().Where(item => !string.Equals(item.Scope, scope.Key, StringComparison.OrdinalIgnoreCase)).ToList()); }
        finally { _gate.Release(); }
    }

    private List<SecretAdapterBinding> Read()
        => File.Exists(_path) ? JsonSerializer.Deserialize<List<SecretAdapterBinding>>(File.ReadAllText(_path), Json) ?? [] : [];

    private void Write(List<SecretAdapterBinding> bindings)
    {
        OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = OwnerOnlyAccess.CreateNewFile(temp))
        {
            JsonSerializer.Serialize(stream, bindings, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, overwrite: true);
    }

    public static void Validate(SecretAdapterBinding binding)
    {
        if (!SecretScope.TryParse(binding.Scope, out _)) throw new SecretValidationException("invalid-scope", "Choose a valid secret scope.");
        if (binding.Kind is not ("vault" or "onepassword")) throw new SecretValidationException("invalid-adapter", "Choose Vault/OpenBao or 1Password.");
        if (!Uri.TryCreate(binding.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new SecretValidationException("invalid-endpoint", "The adapter endpoint must use HTTPS (HTTP is allowed on loopback only).");
        if (string.IsNullOrWhiteSpace(binding.Container) || binding.Kind == "onepassword" && binding.Container.Contains('/') ||
            binding.Container.Split('/').Any(part =>
                part.Length == 0 || part == "." || part == ".." || part.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and '_')))
            throw new SecretValidationException("invalid-container", "Choose a Vault KV mount or 1Password vault ID.");
        if (string.IsNullOrWhiteSpace(binding.Credential) ||
            !(binding.Credential.StartsWith("env:", StringComparison.Ordinal) &&
              System.Text.RegularExpressions.Regex.IsMatch(binding.Credential[4..], "^[A-Z][A-Z0-9_]{0,127}$")) &&
            !(SecretReference.TryParse(binding.Credential, out var reference) && reference.Scope == SecretScope.Workspace))
            throw new SecretValidationException("invalid-credential", "Use an environment variable or a workspace secret reference for the adapter credential.");
        if (binding.CacheSeconds is < 0 or > 300)
            throw new SecretValidationException("invalid-cache-ttl", "Cache TTL must be between 0 and 300 seconds.");
    }
}
