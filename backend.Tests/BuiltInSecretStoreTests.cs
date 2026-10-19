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
        var flagged = Assert.Single((await store.ListAsync(default)).Unreadable);
        Assert.Equal(UnreadableSecret.IntegrityFailed, flagged.Problem);
        Assert.Equal(("workspace", "MAIL"), (flagged.Scope, flagged.Name));
    }

    [Fact]
    public async Task Revocation_deletes_the_sealed_file_so_restoring_older_metadata_cannot_reissue_it()
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        await Append(store, SecretScope.Workspace, "MAIL", _value + "-two");
        var metadata = Path.Combine(store.Root, "items", BuiltInSecretStore.SecretId(SecretScope.Workspace, "MAIL"), "metadata.json");
        var beforeRevoke = File.ReadAllBytes(metadata);

        await store.UpdateAsync(SecretScope.Workspace, "MAIL",
            record => SecretVersionPolicy.Revoke(record, 1, SecretActor.Human("first.owner"), _clock.GetUtcNow().UtcDateTime), default);

        Assert.False(File.Exists(VersionPath(store, SecretScope.Workspace, "MAIL", 1)));
        Assert.True(File.Exists(VersionPath(store, SecretScope.Workspace, "MAIL", 2)));
        await Assert.ThrowsAsync<SecretRevokedException>(() => store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default));

        File.WriteAllBytes(metadata, beforeRevoke);
        Assert.True((await store.FindAsync(SecretScope.Workspace, "MAIL", default))!.Versions.Single(item => item.Version == 1).Active);
        await Assert.ThrowsAsync<SecretIntegrityException>(() => store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default));
        Assert.Equal(_value + "-two", Encoding.UTF8.GetString(await store.ReadValueAsync(SecretScope.Workspace, "MAIL", 2, default)));
    }

    [Fact]
    public async Task Put_replacement_deletes_the_sealed_files_of_the_versions_it_revokes()
    {
        var store = Store();
        var owner = SecretActor.Human("first.owner");
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        await store.AppendVersionAsync(SecretScope.Workspace, "MAIL", Encoding.UTF8.GetBytes(_value + "-new"),
            current => SecretVersionPolicy.Put(current, string.Empty, SecretScope.Workspace, "MAIL", owner, _clock.GetUtcNow().UtcDateTime), default);

        Assert.False(File.Exists(VersionPath(store, SecretScope.Workspace, "MAIL", 1)));
        await Assert.ThrowsAsync<SecretRevokedException>(() => store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default));
        Assert.Equal(_value + "-new", Encoding.UTF8.GetString(await store.ReadValueAsync(SecretScope.Workspace, "MAIL", 2, default)));
    }

    [Fact]
    public async Task Data_keys_are_wrapped_under_a_derived_key_not_the_raw_kek()
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        var node = Sealed(store, SecretScope.Workspace, "MAIL", 1);
        Assert.Equal(SealedSecretVersion.CurrentFormat, node["format"]!.GetValue<int>());

        var rawKek = Convert.FromBase64String(File.ReadAllText(KeyFile).Trim());
        var wrapped = Convert.FromBase64String(node["wrappedKey"]!.GetValue<string>());
        var context = Encoding.UTF8.GetBytes($"agent-studio/secret/v1|{BuiltInSecretStore.SecretId(SecretScope.Workspace, "MAIL")}|1|data-key");
        using var aes = new AesGcm(rawKek, 16);
        Assert.ThrowsAny<CryptographicException>(() => aes.Decrypt(Convert.FromBase64String(node["wrapNonce"]!.GetValue<string>()),
            wrapped.AsSpan(0, wrapped.Length - 16), wrapped.AsSpan(wrapped.Length - 16), new byte[wrapped.Length - 16], context));

        node["format"] = 1;
        File.WriteAllText(VersionPath(store, SecretScope.Workspace, "MAIL", 1), node.ToJsonString());
        await Assert.ThrowsAsync<SecretIntegrityException>(() => store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default));
    }

    [Fact]
    public async Task A_missing_key_file_never_forks_a_store_that_holds_items()
    {
        await Append(Store(), SecretScope.Workspace, "MAIL", _value);
        File.Delete(KeyFile);
        var redeployed = Store();

        Assert.Contains("restore that key", (await Assert.ThrowsAsync<SecretKeyUnavailableException>(() =>
            Append(redeployed, SecretScope.Workspace, "OTHER", _value))).Message);
        Assert.False(File.Exists(KeyFile));
        var listing = await redeployed.ListAsync(default);
        Assert.Empty(listing.Records);
        var flagged = Assert.Single(listing.Unreadable);
        Assert.Equal(UnreadableSecret.KeyUnavailable, flagged.Problem);
        Assert.Equal("MAIL", flagged.Name);
    }

    [Fact]
    public async Task One_item_sealed_with_another_key_is_flagged_while_the_rest_still_list()
    {
        var store = Store();
        await Append(store, SecretScope.Workspace, "MAIL", _value);
        var foreignKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var foreignConfig = Configuration(("Secrets:KeyEnvironmentVariable", "FOREIGN_KEK"));
        var foreign = new BuiltInSecretStore(Path.Combine(_root, "foreign"),
            new ConfiguredKeyEncryptionKeySource(foreignConfig, name => name == "FOREIGN_KEK" ? foreignKey : null, _ => { }));
        await Append(foreign, SecretScope.ForProject("memowikis"), "API_TOKEN", _value);
        var foreignId = BuiltInSecretStore.SecretId(SecretScope.ForProject("memowikis"), "API_TOKEN");
        Directory.Move(Path.Combine(foreign.Root, "items", foreignId), Path.Combine(store.Root, "items", foreignId));

        var listing = await store.ListAsync(default);
        Assert.Equal("MAIL", Assert.Single(listing.Records).Name);
        var flagged = Assert.Single(listing.Unreadable);
        Assert.Equal((foreignId, UnreadableSecret.KeyUnavailable, "memowikis"), (flagged.Id, flagged.Problem, flagged.Scope));
    }

    [Fact]
    public async Task Another_key_cannot_open_the_store()
    {
        await Append(Store(), SecretScope.Workspace, "MAIL", _value);
        var other = new BuiltInSecretStore(Configuration(), new ConfiguredKeyEncryptionKeySource(Configuration(),
            name => name == ConfiguredKeyEncryptionKeySource.DefaultEnvironmentVariable
                ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null, _ => { }));

        await Assert.ThrowsAsync<SecretKeyUnavailableException>(() => other.FindAsync(SecretScope.Workspace, "MAIL", default));
    }

    [Fact]
    public async Task Environment_key_is_used_without_writing_a_key_file()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var source = new ConfiguredKeyEncryptionKeySource(Configuration(), name => name == "STUDIO_KEK" ? key : null, _ => { });
        var config = Configuration(("Secrets:KeyEnvironmentVariable", "STUDIO_KEK"));
        var store = new BuiltInSecretStore(config, new ConfiguredKeyEncryptionKeySource(config, name => name == "STUDIO_KEK" ? key : null, _ => { }));
        await Append(store, SecretScope.Workspace, "MAIL", _value);

        Assert.Equal(_value, Encoding.UTF8.GetString(await store.ReadValueAsync(SecretScope.Workspace, "MAIL", 1, default)));
        Assert.False(File.Exists(KeyFile));
        Assert.Equal("key-file", source.Describe());
        Assert.Equal("environment", new ConfiguredKeyEncryptionKeySource(config, name => name == "STUDIO_KEK" ? key : null, _ => { }).Describe());
    }

    [Fact]
    public void Environment_key_is_captured_once_removed_from_the_environment_and_redacted()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["STUDIO_KEK"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        var encoded = environment["STUDIO_KEK"]!;
        var config = Configuration(("Secrets:KeyEnvironmentVariable", "STUDIO_KEK"));

        var source = new ConfiguredKeyEncryptionKeySource(config, name => environment.GetValueOrDefault(name), name => environment.Remove(name));

        Assert.False(environment.ContainsKey("STUDIO_KEK"));
        Assert.Equal("environment", source.Describe());
        Assert.Equal("environment", source.Load(createIfMissing: false).Source);
        Assert.Equal($"env STUDIO_KEK=[REDACTED_CREDENTIAL] done", AgentStudio.Security.CredentialRedactor.Redact($"env STUDIO_KEK={encoded} done"));
    }

    [Fact]
    public void Malformed_environment_key_is_still_removed_and_fails_on_load()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["STUDIO_KEK"] = "not-base64-" + Guid.NewGuid().ToString("N") };
        var source = new ConfiguredKeyEncryptionKeySource(Configuration(("Secrets:KeyEnvironmentVariable", "STUDIO_KEK")),
            name => environment.GetValueOrDefault(name), name => environment.Remove(name));

        Assert.Empty(environment);
        Assert.Contains("not valid base64", Assert.Throws<SecretKeyUnavailableException>(() => source.Load(createIfMissing: true)).Message);
        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public void Outside_the_local_profile_the_key_must_be_configured_explicitly()
    {
        var values = new Dictionary<string, string?> { ["TaskRepository"] = Workspace, ["Security:Profile"] = "networked" };
        var implicitSource = new ConfiguredKeyEncryptionKeySource(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), _ => null, _ => { });
        Assert.Contains("explicitly configured", Assert.Throws<SecretKeyUnavailableException>(() => implicitSource.Load(createIfMissing: true)).Message);

        var explicitSource = new ConfiguredKeyEncryptionKeySource(Configuration(("Security:Profile", "networked")), _ => null, _ => { });
        Assert.Equal("key-file", explicitSource.Load(createIfMissing: true).Source);
        Assert.True(File.Exists(KeyFile));
    }

    [Fact]
    public void Key_file_text_is_registered_with_the_credential_redactor()
    {
        new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null, _ => { }).Load(createIfMissing: true);
        var encoded = File.ReadAllText(KeyFile).Trim();

        Assert.DoesNotContain(encoded, AgentStudio.Security.CredentialRedactor.Redact("cat secrets.kek: " + encoded), StringComparison.Ordinal);
    }

    [Fact]
    public void Key_file_inside_the_workspace_is_refused()
    {
        var config = Configuration(("Secrets:KeyFile", Path.Combine(Workspace, ".security", "secrets.kek")));
        var source = new ConfiguredKeyEncryptionKeySource(config, _ => null, _ => { });

        Assert.Contains("inside the workspace", Assert.Throws<SecretKeyUnavailableException>(() => source.Load(createIfMissing: true)).Message);
        Assert.False(File.Exists(Path.Combine(Workspace, ".security", "secrets.kek")));
    }

    [Fact]
    public void Key_file_is_created_owner_only_and_refused_once_others_can_read_it()
    {
        var source = new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null, _ => { });
        var key = source.Load(createIfMissing: true);
        Assert.Equal(16, key.Id.Length);
        Assert.Equal("key-file", key.Source);
        Assert.Equal(32, Convert.FromBase64String(File.ReadAllText(KeyFile)).Length);
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var fresh = new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null, _ => { });
        Assert.Contains("accessible to other users", Assert.Throws<SecretKeyUnavailableException>(() => fresh.Load(createIfMissing: false)).Message);
    }

    [Fact]
    public async Task Reading_an_empty_store_never_creates_a_key()
    {
        var store = Store();
        var listing = await store.ListAsync(default);
        Assert.Empty(listing.Records);
        Assert.Empty(listing.Unreadable);
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
        => new(Configuration(), new ConfiguredKeyEncryptionKeySource(Configuration(), _ => null, _ => { }));

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
