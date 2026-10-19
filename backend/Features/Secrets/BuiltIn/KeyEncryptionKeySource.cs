using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Secrets;

/// <summary>The key-encryption key of the built-in store. Its material never leaves this type except into a key derivation.</summary>
public sealed class KeyEncryptionKey
{
    public const int Size = 32;
    private readonly byte[] _material;

    public KeyEncryptionKey(byte[] material, string source)
    {
        if (material.Length != Size)
            throw new SecretKeyUnavailableException($"The key-encryption key must be {Size} bytes; {source} holds {material.Length}.");
        _material = material.ToArray();
        Source = source;
        Id = Convert.ToHexStringLower(SHA256.HashData([.. "agent-studio/kek-id/v1|"u8, .. _material]))[..16];
    }

    /// <summary>A short public fingerprint recorded with every sealed version.</summary>
    public string Id { get; }

    /// <summary><c>environment</c> or <c>key-file</c>; never a path or value.</summary>
    public string Source { get; }

    /// <summary>Derived key that wraps data keys. The raw KEK is only ever key-derivation input.</summary>
    internal byte[] DeriveWrapKey()
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, _material, 32, info: Encoding.UTF8.GetBytes("agent-studio/secrets/data-key-wrap/v2"));

    /// <summary>Derived key that authenticates metadata files, separate from the wrapping key.</summary>
    internal byte[] DeriveMetadataKey()
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, _material, 32, info: Encoding.UTF8.GetBytes("agent-studio/secrets/metadata-mac/v1"));
}

public interface IKeyEncryptionKeySource
{
    /// <summary>
    /// Loads the key. <paramref name="createIfMissing"/> lets a first write
    /// create a protected key file; the store passes it only while it holds no
    /// sealed item, so a missing key never forks an existing store.
    /// </summary>
    KeyEncryptionKey Load(bool createIfMissing);

    /// <summary>Where the key comes from, for status display: never the key itself.</summary>
    string Describe();
}

/// <summary>
/// Reads the key-encryption key from an environment variable (base64 of 32
/// bytes) or from an OS-protected key file. The environment variable is read
/// once, when the service starts, and then removed from the process
/// environment, so no child process (CLI runs, build gates, project URL
/// processes) inherits it; its text is registered with
/// <see cref="CredentialRedactor"/>. The key file defaults to the service
/// account's private configuration directory in the <c>local</c> profile; every
/// other profile must name the key explicitly. The file is created owner-only,
/// refused when other users can read it, and refused when it lies inside the
/// workspace that holds the encrypted data. Backups of the workspace therefore
/// never carry the key.
/// </summary>
public sealed class ConfiguredKeyEncryptionKeySource : IKeyEncryptionKeySource
{
    public const string DefaultEnvironmentVariable = "AGENT_STUDIO_SECRETS_KEK";

    private readonly IConfiguration _configuration;
    private readonly string _environmentVariable;
    private readonly bool _fromEnvironment;
    private readonly string? _environmentProblem;
    private readonly object _gate = new();
    private KeyEncryptionKey? _loaded;

    public ConfiguredKeyEncryptionKeySource(IConfiguration configuration)
        : this(configuration, Environment.GetEnvironmentVariable, name => Environment.SetEnvironmentVariable(name, null))
    {
    }

    /// <summary>Captures the environment key now and removes it through <paramref name="removeEnvironment"/>.</summary>
    public ConfiguredKeyEncryptionKeySource(IConfiguration configuration, Func<string, string?> readEnvironment, Action<string> removeEnvironment)
    {
        _configuration = configuration;
        _environmentVariable = configuration["Secrets:KeyEnvironmentVariable"] is { Length: > 0 } name ? name : DefaultEnvironmentVariable;
        if (readEnvironment(_environmentVariable) is not { Length: > 0 } encoded) return;

        _fromEnvironment = true;
        CredentialRedactor.RegisterLiteral(encoded.Trim());
        removeEnvironment(_environmentVariable);
        try { _loaded = new KeyEncryptionKey(DecodeKey(encoded, "the environment variable"), "environment"); }
        catch (SecretKeyUnavailableException ex) { _environmentProblem = ex.Message; }
    }

    private bool ExplicitKeyFile => _configuration["Secrets:KeyFile"] is { Length: > 0 };

    public string KeyFilePath => _configuration["Secrets:KeyFile"] is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : DefaultKeyFilePath();

    public string Describe() => _fromEnvironment ? "environment" : "key-file";

    public KeyEncryptionKey Load(bool createIfMissing)
    {
        lock (_gate)
        {
            if (_loaded is not null) return _loaded;
            if (_environmentProblem is not null) throw new SecretKeyUnavailableException(_environmentProblem);
            if (!SecurityProfiles.IsLocal(_configuration) && !ExplicitKeyFile)
                throw new SecretKeyUnavailableException(
                    $"The {SecurityProfiles.ActiveProfile(_configuration)} profile needs an explicitly configured key-encryption key: "
                    + $"set {_environmentVariable} or Secrets:KeyFile.");

            var path = KeyFilePath;
            RejectPathInsideWorkspace(path);
            if (!File.Exists(path))
            {
                if (!createIfMissing)
                    throw new SecretKeyUnavailableException(
                        "No key-encryption key was found. Sealed secrets open only with the key that sealed them: "
                        + "restore that key file or set the environment variable.");
                CreateKeyFile(path);
            }
            OwnerOnlyAccess.RejectForeignAccess(path);
            var encoded = File.ReadAllText(path).Trim();
            CredentialRedactor.RegisterLiteral(encoded);
            return _loaded = new KeyEncryptionKey(DecodeKey(encoded, "the key file"), "key-file");
        }
    }

    private void RejectPathInsideWorkspace(string keyPath)
    {
        var workspace = Path.GetFullPath(SecretStorePaths.WorkspaceRoot(_configuration));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = workspace.EndsWith(Path.DirectorySeparatorChar) ? workspace : workspace + Path.DirectorySeparatorChar;
        if (keyPath.StartsWith(root, comparison) || string.Equals(keyPath, workspace, comparison))
            throw new SecretKeyUnavailableException(
                "The key-encryption key file must not be stored inside the workspace that holds the encrypted secrets.");
    }

    private static void CreateKeyFile(string path)
    {
        OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(path)!);
        var material = RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size);
        try
        {
            using var stream = OwnerOnlyAccess.CreateNewFile(path);
            stream.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(material)));
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created the key first; the caller reads that one.
            return;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] DecodeKey(string encoded, string origin)
    {
        try { return Convert.FromBase64String(encoded.Trim()); }
        catch (FormatException) { throw new SecretKeyUnavailableException($"The key-encryption key in {origin} is not valid base64."); }
    }

    private static string DefaultKeyFilePath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "keys", "secrets.kek");
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "agent-studio", "keys", "secrets.kek");
    }
}

public static class SecretStorePaths
{
    public static string WorkspaceRoot(IConfiguration configuration)
        => string.IsNullOrWhiteSpace(configuration["TaskRepository"])
            ? Path.Combine(AppContext.BaseDirectory, "workspace")
            : configuration["TaskRepository"]!;

    /// <summary>The built-in store lives in the workspace's owner-only <c>.security</c> directory.</summary>
    public static string DataRoot(IConfiguration configuration)
        => Path.Combine(WorkspaceRoot(configuration), ".security", "secrets");
}
