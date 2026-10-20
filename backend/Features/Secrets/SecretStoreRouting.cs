using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>One exact scope assignment. Credentials are references to the built-in store or environment variable names.</summary>
public sealed record SecretStoreAssignment(string Scope, string Provider, string? Address = null, string? Mount = null,
    string? Vault = null, string? Credential = null, int TtlSeconds = 30);

public sealed record SecretStoreHealth(string Scope, string Provider, string State, string? Reason);

public sealed class SecretStoreSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, SecretStoreAssignment>? _assignments;

    public SecretStoreSettings(IConfiguration configuration)
        : this(Path.Combine(SecretStorePaths.DataRoot(configuration), "providers.json")) { }

    public SecretStoreSettings(string path) => _path = path;

    public IReadOnlyList<SecretStoreAssignment> All()
    {
        lock (_gate) return [.. Load().Values.OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)];
    }

    public SecretStoreAssignment For(SecretScope scope)
    {
        lock (_gate) return Load().GetValueOrDefault(scope.Key) ?? new SecretStoreAssignment(scope.Key, BuiltInSecretStore.Id);
    }

    public void Set(SecretStoreAssignment assignment)
    {
        var scope = SecretScope.Parse(assignment.Scope);
        if (assignment.Provider is not (BuiltInSecretStore.Id or "vault" or "onepassword"))
            throw new SecretValidationException("invalid-provider", "Choose builtin, vault, or onepassword.");
        if (assignment.TtlSeconds is < 1 or > 300)
            throw new SecretValidationException("invalid-ttl", "Cache TTL must be between 1 and 300 seconds.");
        if (assignment.Provider != BuiltInSecretStore.Id)
        {
            if (!Uri.TryCreate(assignment.Address, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new SecretValidationException("invalid-address", "Provider address must be HTTPS (HTTP is allowed on loopback), without credentials or query.");
            if (string.IsNullOrWhiteSpace(assignment.Credential)
                || !(assignment.Credential.StartsWith("env:", StringComparison.Ordinal)
                     || (SecretReference.TryParse(assignment.Credential, out var reference)
                         && reference.Scope == SecretScope.Workspace)))
                throw new SecretValidationException("invalid-credential", "Use env:VARIABLE or a built-in workspace secret reference for provider credentials.");
            if (assignment.Provider == "vault" && !ValidSegment(assignment.Mount))
                throw new SecretValidationException("invalid-mount", "A Vault KV v2 mount is required.");
            if (assignment.Provider == "onepassword" && !ValidSegment(assignment.Vault))
                throw new SecretValidationException("invalid-vault", "A 1Password vault ID is required.");
        }
        lock (_gate)
        {
            var current = Load();
            var next = new Dictionary<string, SecretStoreAssignment>(current, StringComparer.OrdinalIgnoreCase);
            next[scope.Key] = assignment with { Scope = scope.Key };
            OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = OwnerOnlyAccess.CreateNewFile(temporary))
                {
                    JsonSerializer.Serialize(stream, next.Values.ToList(), Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                _assignments = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private Dictionary<string, SecretStoreAssignment> Load()
    {
        if (_assignments is not null) return _assignments;
        var entries = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<SecretStoreAssignment>>(File.ReadAllText(_path), Json) ?? []
            : [];
        _assignments = entries.ToDictionary(x => SecretScope.Parse(x.Scope).Key, StringComparer.OrdinalIgnoreCase);
        return _assignments;
    }

    private static bool ValidSegment(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>Resolves adapter credentials directly from the built-in store to avoid a routing cycle.</summary>
public sealed class SecretAdapterCredentials(BuiltInSecretStore builtIn)
{
    public async Task<string> ResolveAsync(string? source, CancellationToken ct)
    {
        if (source?.StartsWith("env:", StringComparison.Ordinal) == true)
        {
            var name = source[4..];
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new SecretProviderUnavailableException("Provider credential environment name is invalid.");
            return ValidCredential(Environment.GetEnvironmentVariable(name));
        }
        if (!SecretReference.TryParse(source, out var reference) || reference.Scope != SecretScope.Workspace)
            throw new SecretProviderUnavailableException("Provider credential source is invalid.");
        try
        {
            var record = await builtIn.FindAsync(reference.Scope, reference.Name, ct)
                ?? throw new SecretProviderUnavailableException("Provider credential is missing from the built-in store.");
            var version = SecretVersionPolicy.Select(record, reference.Version).Version;
            var bytes = await builtIn.ReadValueAsync(reference.Scope, reference.Name, version, ct);
            try { return ValidCredential(Encoding.UTF8.GetString(bytes)); }
            finally { Array.Clear(bytes); }
        }
        catch (Exception ex) when (ex is SecretValidationException or SecretIntegrityException or SecretKeyUnavailableException)
        {
            throw new SecretProviderUnavailableException("Provider credential is unavailable from the built-in store.");
        }
    }

    private static string ValidCredential(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.Contains('\r') && !value.Contains('\n')
            ? value : throw new SecretProviderUnavailableException("Provider credential is missing or malformed.");
}

public sealed class SecretProviderUnavailableException(string message) : Exception(message);
public sealed class SecretProviderUnsupportedException(string operation)
    : SecretValidationException("provider-operation-unsupported", $"The selected provider does not support {operation}.");

/// <summary>Routes every operation by its exact scope. A broken assignment cannot fall back to another store.</summary>
public sealed class RoutedSecretStore(
    BuiltInSecretStore builtIn, SecretStoreSettings settings, SecretAdapterCredentials credentials,
    IHttpClientFactory clients, TimeProvider clock) : ISecretStore
{
    private readonly ConcurrentDictionary<SecretStoreAssignment, ISecretStore> _adapters = new();
    public string StoreId => settings.All().Any(x => x.Provider != BuiltInSecretStore.Id) ? "scoped" : BuiltInSecretStore.Id;
    public async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        var records = new List<SecretRecord>();
        var unreadable = new List<UnreadableSecret>();
        var builtin = await builtIn.ListAsync(ct);
        records.AddRange(builtin.Records.Where(x => settings.For(x.ParsedScope).Provider == BuiltInSecretStore.Id));
        unreadable.AddRange(builtin.Unreadable);
        foreach (var assignment in settings.All().Where(x => x.Provider != BuiltInSecretStore.Id))
        {
            try
            {
                var listing = await Adapter(assignment).ListAsync(ct);
                records.AddRange(listing.Records.Where(x => string.Equals(x.Scope, assignment.Scope, StringComparison.OrdinalIgnoreCase)));
                unreadable.AddRange(listing.Unreadable);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is
                (SecretProviderUnavailableException or HttpRequestException or TaskCanceledException
                 or JsonException or InvalidOperationException or SecretKeyUnavailableException))
            {
                unreadable.Add(new UnreadableSecret(assignment.Scope, assignment.Scope, null, UnreadableSecret.ReadFailed,
                    "Selected provider is unavailable for this scope."));
            }
        }
        return new SecretListing(records, unreadable);
    }
    public Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct) => For(scope).FindAsync(scope, name, ct);
    public Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct) => For(scope).AppendVersionAsync(scope, name, value, transition, ct);
    public Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
        => For(scope).UpdateAsync(scope, name, transition, ct);
    public Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
        => For(scope).ReadValueAsync(scope, name, version, ct);
    public Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct) => For(scope).DeleteAsync(scope, name, ct);

    public async Task<IReadOnlyList<SecretStoreHealth>> HealthAsync(CancellationToken ct)
    {
        var result = new List<SecretStoreHealth>();
        foreach (var assignment in settings.All())
        {
            if (assignment.Provider == BuiltInSecretStore.Id)
            {
                result.Add(new(assignment.Scope, assignment.Provider, "ready", null));
                continue;
            }
            try
            {
                if (Adapter(assignment) is IRemoteSecretStore remote) await remote.ProbeAsync(ct);
                result.Add(new(assignment.Scope, assignment.Provider, "ready", null));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is
                (SecretProviderUnavailableException or HttpRequestException or TaskCanceledException
                 or SecretKeyUnavailableException or SecretValidationException or JsonException or InvalidOperationException))
            {
                result.Add(new(assignment.Scope, assignment.Provider, "unavailable", "Check the address, credential and provider permissions."));
            }
        }
        return result;
    }

    private ISecretStore For(SecretScope scope)
    {
        var assignment = settings.For(scope);
        return assignment.Provider == BuiltInSecretStore.Id ? builtIn : Adapter(assignment);
    }
    private ISecretStore Adapter(SecretStoreAssignment assignment)
    {
        if (!Uri.TryCreate(assignment.Address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || string.IsNullOrWhiteSpace(assignment.Credential)
            || (assignment.Provider == "vault" && string.IsNullOrWhiteSpace(assignment.Mount))
            || (assignment.Provider == "onepassword" && string.IsNullOrWhiteSpace(assignment.Vault)))
            throw new SecretProviderUnavailableException("Selected provider configuration is incomplete.");
        return _adapters.GetOrAdd(assignment, item => item.Provider switch
        {
            "vault" => new VaultKv2SecretStore(item, credentials, clients.CreateClient("secrets-external"), clock),
            "onepassword" => new OnePasswordSecretStore(item, credentials, clients.CreateClient("secrets-external"), clock),
            _ => throw new SecretProviderUnavailableException("Unknown selected provider.")
        });
    }
}

internal interface IRemoteSecretStore { Task ProbeAsync(CancellationToken ct); }
