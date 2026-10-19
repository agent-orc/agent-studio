using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Envelope encryption, integrity on read, key custody and the file layout of the built-in store.</summary>
public sealed class BuiltInSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "secret-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly string _value = "value-" + Guid.NewGuid().ToString("N");
    private string Workspace => Path.Combine(_root, "workspace");
    private string KeyFile => Path.Combine(_root, "operator-keys", "secrets.kek");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Round_trip_decrypts_each_version_and_keeps_no_plaintext_at_rest()
    {
        var store = Store();
        var scope = SecretScope.ForEnvironment("memowikis", "stage");
        await Append(store, scope, "DATABASE_URL", _value);
        await Append(store, scope, "DATABASE_URL", _value + "-two");

        Assert.Equal(_value, Encoding.UTF8.GetString(await store.ReadValueAsync(scope, "DATABASE_URL", 1, default)));
        Assert.Equal(_value + "-two", Encoding.UTF8.GetString(await store.ReadValueAsync(scope, "DATABASE_URL", 2, default)));

        var files = Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories).ToList();
        Assert.Contains(files, file => file.EndsWith("v000001.sealed.json", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("v000002.sealed.json", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.EndsWith(".tmp", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var bytes = File.ReadAllText(file);
            Assert.DoesNotContain(_value, bytes, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(_value)), bytes, StringComparison.Ordinal);
        }
        Assert.False(File.Exists(Path.Combine(store.Root, "secrets.kek")));
        Assert.DoesNotContain(Directory.EnumerateFiles(Workspace, "*", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains(File.ReadAllText(KeyFile).Trim(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_version_has_its_own_data_key_and_nonce()
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        var first = Sealed(store, SecretScope.Workspace, "MAIL", 1);
        var second = Sealed(store, SecretScope.Workspace, "MAIL", 2);

        Assert.NotEqual(first["wrappedKey"]!.GetValue<string>(), second["wrappedKey"]!.GetValue<string>());
        Assert.NotEqual(first["nonce"]!.GetValue<string>(), second["nonce"]!.GetValue<string>());
        Assert.NotEqual(first["ciphertext"]!.GetValue<string>(), second["ciphertext"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("wrappedKey")]
    [InlineData("nonce")]
    [InlineData("wrapNonce")]
    public async Task Tampered_sealed_version_fails_its_integrity_check(string field)
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        var path = VersionPath(store, SecretScope.Workspace, "MAIL", 1);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var bytes = Convert.FromBase64String(node[field]!.GetValue<string>());
        bytes[0] ^= 0x01;
        node[field] = Convert.ToBase64String(bytes);
        File.WriteAllText(path, node.ToJsonString());

        await Assert.ThrowsAsync<SecretIntegrityException>(() => store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default));
    }

    [Fact]
    public async Task Version_file_moved_to_another_secret_is_rejected()
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        await Append(store, SecretScope.Workspace, "OTHER", "other-" + Guid.NewGuid().ToString("N"));
        var node = JsonNode.Parse(File.ReadAllText(VersionPath(store, SecretScope.Workspace, "MAIL", 1)))!;
        node["secretId"] = BuiltInSecretStore.SecretId(SecretScope.Workspace, "OTHER");
        File.WriteAllText(VersionPath(store, SecretScope.Workspace, "OTHER", 1), node.ToJsonString());

        await Assert.ThrowsAsync<SecretIntegrityException>(() => store.ReadValueAsync(SecretScope.Workspace, "OTHER", 1, default));
    }

    [Fact]
    public async Task Edited_metadata_fails_its_mac_so_a_revocation_cannot_be_undone_on_disk()
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        await store.UpdateAsync(SecretScope.Workspace, "MAIL",
            record => SecretVersionPolicy.Revoke(record, null, SecretActor.Human("first.owner"), _clock.GetUtcNow().UtcDateTime), default);
        var path = Path.Combine(store.Root, "items", BuiltInSecretStore.SecretId(SecretScope.Workspace, "MAIL"), "metadata.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["payload"] = node["payload"]!.GetValue<string>().Replace("\"revokedAt\":\"", "\"unrevokedAt\":\"", StringComparison.Ordinal);
        File.WriteAllText(path, node.ToJsonString());

        await Assert.ThrowsAsync<SecretIntegrityException>(() => store.FindAsync(SecretScope.Workspace, "MAIL", default));
        await Assert.ThrowsAsync<SecretIntegrityException>(() => store.ListAsync(default));
    }

    [Fact]
    public async Task Another_key_cannot_open_the_store()
    {
        await Append(Store(), SecretScope.Workspace, "MAIL", _value);
        var other = new BuiltInSecretStore(Configuration(), new ConfiguredKeyEncryptionKeySource(Configuration(),
            name => name == ConfiguredKeyEncryptionKeySource.DefaultEnvironmentVariable
                ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null));

        await Assert.ThrowsAsync<SecretKeyUnavailableException>(() => other.FindAsync(SecretScope.Workspace, "MAIL", default));
    }

    [Fact]
    public async Task Environment_key_is_used_without_writing_a_key_file()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var source = new ConfiguredKeyEncryptionKeySource(Configuration(), name => name == "STUDIO_KEK" ? key : null);
        var config = Configuration(("Secrets:KeyEnvironmentVariable", "STUDIO_KEK"));
        var store = new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, name => name == "STUDIO_KEK" ? key : null));
        await Append(store, SecretScope.Workspace, "MAIL", _value);

        Assert.Equal(_value, Encoding.UTF8.GetString(await store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default)));
        Assert.False(File.Exists(KeyFile));
        Assert.Equal("key-file", source.Describe());
        Assert.Equal("environment", new ConfiguredKeyEncryptionKeySource(config, name => name == "STUDIO_KEK" ? key : null).Describe());
    }

    [Fact]
    public void Key_file_inside_the_workspace_is_refused()
    {
        var config = Configuration(("Secrets:KeyFile", Path.Combine(Workspace, ".security", "secrets.kek")));
        var source = new ConfiguredKeyEncryptionKeySource(config, _ => null);

        Assert.Contains("inside the workspace", Assert.Throws<SecretKeyUnavailableException>(() => source.Load(createIfMissing: true)).Message);
        Assert.False(File.Exists(Path.Combine(Workspace, ".security", "secrets.kek")));
    }

    [Fact]
    public void Key_file_is_created_owner_only_and_refused_once_others_can_read_it()
    {
        var source = new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null);
        var key = source.Load(createIfMissing: true);
        Assert.Equal(16, key.Id.Length);
        Assert.Equal("key-file", key.Source);
        Assert.Equal(32, Convert.FromBase64String(File.ReadAllText(KeyFile)).Length);
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var fresh = new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null);
        Assert.Contains("accessible to other users", Assert.Throws<SecretKeyUnavailableException>(() => fresh.Load(createIfMissing: false)).Message);
    }

    [Fact]
    public async Task Reading_an_empty_store_never_creates_a_key()
    {
        var store = Store();
        Assert.Empty(await store.ListAsync(default));
        Assert.Null(await store.FindAsync(SecretScope.Workspace, "MAIL", default));
        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public async Task Delete_removes_metadata_and_every_sealed_version()
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        await Append(store, SecretScope.Workspace, "MAIL", _value);

        Assert.True(await store.DeleteAsync(SecretScope.Workspace, "MAIL", default));
        Assert.False(await store.DeleteAsync(SecretScope.Workspace, "MAIL", default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(store.Root, "items")));
        await Assert.ThrowsAsync<SecretNotFoundException>(() => store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default));
    }

    [Fact]
    public async Task Store_and_version_files_are_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        var owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        Assert.Equal(owner, File.GetUnixFileMode(VersionPath(store, SecretScope.Workspace, "MAIL", 1)));
        Assert.Equal(owner | UnixFileMode.UserExecute, File.GetUnixFileMode(store.Root));
    }

    private BuiltInSecretStore Store()
        => new(Configuration(), new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null));

    private IConfiguration Configuration(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?> { ["TaskRepository"] = Workspace, ["Secrets:KeyFile"] = KeyFile };
        foreach (var (key, value) in extra) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private Task<SecretRecord> Append(BuiltInSecretStore store, SecretScope scope, string name, string value)
        => store.AppendVersionAsync(scope, name, Encoding.UTF8.GetBytes(value),
            current => current is null
                ? SecretVersionPolicy.Put(null, string.Empty, scope, name, SecretActor.Human("first.owner"), _clock.GetUtcNow().UtcDateTime)
                : SecretVersionPolicy.Rotate(current, new SecretReference(scope, name), SecretActor.Human("first.owner"), _clock.GetUtcNow().UtcDateTime),
            default);

    private static string VersionPath(BuiltInSecretStore store, SecretScope scope, string name, int version)
        => Path.Combine(store.Root, "items", BuiltInSecretStore.SecretId(scope, name), BuiltInSecretStore.VersionFileName(version));

    private static JsonNode Sealed(BuiltInSecretStore store, SecretScope scope, string name, int version)
        => JsonNode.Parse(File.ReadAllText(VersionPath(store, scope, name, version)))!;
}
