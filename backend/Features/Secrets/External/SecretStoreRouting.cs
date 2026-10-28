using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

public sealed record SecretBackendBinding(string Scope, string Kind, string Url, string? Mount, string? VaultId,
    string? CredentialReference, string? CredentialEnvironmentVariable, int TtlSeconds = 30);

public sealed record SecretBackendStatus(string Scope, string Kind, string State, string? Reason);

public sealed class SecretBackendException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Owner-only installation settings. A scope without a binding uses the built-in store.</summary>
public sealed class SecretBackendSettings
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, SecretBackendBinding> _bindings;

    public SecretBackendSettings(IConfiguration configuration)
        : this(Path.Combine(SecretStorePaths.DataRoot(configuration), "backends.json")) { }

    public SecretBackendSettings(string path)
    {
        _path = path;
        _bindings = File.Exists(path)
            ? JsonSerializer.Deserialize<List<SecretBackendBinding>>(File.ReadAllText(path))?
                .ToDictionary(item => item.Scope, StringComparer.OrdinalIgnoreCase) ?? []
            : [];
    }

    public IReadOnlyList<SecretBackendBinding> All()
    {
        lock (_sync) return _bindings.Values.OrderBy(item => item.Scope, StringComparer.Ordinal).ToList();
    }

    public SecretBackendBinding? For(SecretScope scope)
    {
        lock (_sync) return _bindings.GetValueOrDefault(scope.Key);
    }

    public async Task SaveAsync(SecretBackendBinding binding, CancellationToken ct)
    {
        Validate(binding);
        await _gate.WaitAsync(ct);
        try
        {
            Dictionary<string, SecretBackendBinding> next;
            lock (_sync) next = new(_bindings, StringComparer.OrdinalIgnoreCase);
            if (binding.Kind == BuiltInSecretStore.Id) next.Remove(binding.Scope);
            else next[binding.Scope] = binding;
            var directory = Path.GetDirectoryName(_path)!;
            OwnerOnlyAccess.CreateDirectory(directory);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = OwnerOnlyAccess.CreateNewFile(temporary))
                {
                    await JsonSerializer.SerializeAsync(stream, next.Values.ToList(), cancellationToken: ct);
                    await stream.FlushAsync(ct);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            lock (_sync) _bindings = next;
        }
        finally { _gate.Release(); }
    }

    private static void Validate(SecretBackendBinding binding)
    {
        var scope = SecretScope.Parse(binding.Scope);
        if (scope.Key != binding.Scope) throw new SecretValidationException("invalid-scope", "Use the canonical scope key.");
        if (binding.Kind == BuiltInSecretStore.Id) return;
        if (binding.Kind is not ("vault" or "onepassword"))
            throw new SecretValidationException("invalid-backend", "Choose builtin, vault, or onepassword.");
        if (!Uri.TryCreate(binding.Url, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            || url.UserInfo.Length != 0 || url.Fragment.Length != 0 || url.Query.Length != 0)
            throw new SecretValidationException("invalid-backend-url", "Use an HTTPS URL, or HTTP on loopback.");
        if (binding.TtlSeconds is < 1 or > 300)
            throw new SecretValidationException("invalid-backend-ttl", "Cache TTL must be between 1 and 300 seconds.");
        if (binding.Kind == "vault" && (string.IsNullOrWhiteSpace(binding.Mount)
            || binding.Mount.Split('/').Any(segment => !SecretReference.IsValidName(segment))))
            throw new SecretValidationException("invalid-backend-mount", "A Vault KV v2 mount is required.");
        if (binding.Kind == "onepassword" && string.IsNullOrWhiteSpace(binding.VaultId))
            throw new SecretValidationException("invalid-backend-vault", "A 1Password vault ID is required.");
        if (binding.CredentialReference is null && binding.CredentialEnvironmentVariable is null)
            throw new SecretValidationException("backend-credential-required", "Choose a built-in credential reference or environment variable.");
        if (binding.CredentialReference is { } reference && SecretReference.Parse(reference).Scope != SecretScope.Workspace)
            throw new SecretValidationException("invalid-backend-credential", "Adapter credentials must use a built-in workspace secret.");
        if (binding.CredentialEnvironmentVariable is { } variable
            && (variable.Length > 128 || variable.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_'))))
            throw new SecretValidationException("invalid-backend-credential", "The credential environment variable name is invalid.");
    }
}

public sealed class ScopedSecretStore : ISecretStore
{
    private readonly BuiltInSecretStore _builtin;
    private readonly SecretBackendSettings _settings;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly SecretAuditLog _audit;
    private readonly Dictionary<SecretBackendBinding, IExternalSecretStore> _stores = [];
    private readonly object _gate = new();

    public ScopedSecretStore(BuiltInSecretStore builtin, SecretBackendSettings settings,
        IHttpClientFactory http, TimeProvider clock, SecretAuditLog audit)
        => (_builtin, _settings, _http, _clock, _audit) = (builtin, settings, http, clock, audit);

    public string StoreId => _settings.All().Count == 0 ? BuiltInSecretStore.Id : "scoped";

    private ISecretStore Select(SecretScope scope)
    {
        var binding = _settings.For(scope);
        lock (_gate)
        {
            foreach (var stale in _stores.Keys.Where(item => item.Scope == scope.Key && item != binding).ToList())
            {
                if (_stores.Remove(stale, out var previous) && previous is IDisposable disposable) disposable.Dispose();
            }
            if (binding is null) return _builtin;
            if (_stores.TryGetValue(binding, out var found)) return found;
            IExternalSecretStore store = binding.Kind switch
            {
                "vault" => new VaultKvSecretStore(binding, _http.CreateClient("secret-external"), Credential, _clock),
                "onepassword" => new OnePasswordConnectSecretStore(binding, _http.CreateClient("secret-external"), Credential, _clock),
                _ => throw new SecretBackendException("backend-misconfigured", "The selected backend is unknown.")
            };
            _stores[binding] = store;
            return store;
        }
    }

    private async Task<string> Credential(SecretBackendBinding binding, CancellationToken ct)
    {
        if (binding.CredentialEnvironmentVariable is { } variable && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } env)
            return env;
        if (binding.CredentialReference is { } name)
        {
            var reference = SecretReference.Parse(name);
            var record = await _builtin.FindAsync(reference.Scope, reference.Name, ct);
            if (record is not null)
            {
                var version = SecretVersionPolicy.Select(record, reference.Version).Version;
                var bytes = await _builtin.ReadValueAsync(reference.Scope, reference.Name, version, ct);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { Array.Clear(bytes); }
            }
        }
        throw new SecretBackendException("backend-credential-unavailable", "The selected backend credential is unavailable.");
    }

    public async Task<IReadOnlyList<SecretBackendStatus>> HealthAsync(CancellationToken ct)
    {
        var states = new List<SecretBackendStatus>();
        foreach (var binding in _settings.All())
        {
            try
            {
                await ((IExternalSecretStore)Select(SecretScope.Parse(binding.Scope))).HealthAsync(ct);
                states.Add(new(binding.Scope, binding.Kind, "healthy", null));
            }
            catch (Exception ex) when (ex is SecretBackendException or HttpRequestException or TaskCanceledException)
            {
                states.Add(new(binding.Scope, binding.Kind, "unavailable", ex is SecretBackendException backend ? backend.Message : "The backend cannot be reached."));
            }
        }
        return states;
    }

    public async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        var bindings = _settings.All();
        var routed = bindings.Select(item => item.Scope).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builtin = await _builtin.ListAsync(ct);
        var records = builtin.Records.Where(item => !routed.Contains(item.Scope)).ToList();
        var unreadable = builtin.Unreadable.Where(item => item.Scope is null || !routed.Contains(item.Scope)).ToList();
        foreach (var binding in bindings)
        {
            try
            {
                var listing = await Select(SecretScope.Parse(binding.Scope)).ListAsync(ct);
                records.AddRange(listing.Records);
                unreadable.AddRange(listing.Unreadable);
            }
            catch (Exception ex) when (ex is SecretBackendException or HttpRequestException or TaskCanceledException)
            {
                unreadable.Add(new("backend:" + binding.Scope, binding.Scope, null, UnreadableSecret.ReadFailed,
                    ex is SecretBackendException backend ? backend.Message : "The backend cannot be reached."));
            }
        }
        return new(records, unreadable);
    }

    public Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct) => Select(scope).FindAsync(scope, name, ct);
    public Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
        => Select(scope).AppendVersionAsync(scope, name, value, transition, ct);
    public Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
        => Select(scope).UpdateAsync(scope, name, transition, ct);
    public Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
        => Select(scope).ReadValueAsync(scope, name, version, ct);
    public Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct) => Select(scope).DeleteAsync(scope, name, ct);
}

public interface IExternalSecretStore : ISecretStore
{
    Task HealthAsync(CancellationToken ct);
}
