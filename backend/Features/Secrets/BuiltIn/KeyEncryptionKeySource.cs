using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Secrets;

/// <summary>The key-encryption key of the built-in store. Its material never leaves this type except into a crypto call.</summary>
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

    internal T Use<T>(Func<byte[], T> operation) => operation(_material);

    /// <summary>Derived key that authenticates metadata files, separate from the wrapping key.</summary>
    internal byte[] DeriveMetadataKey()
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, _material, 32, info: Encoding.UTF8.GetBytes("agent-studio/secrets/metadata-mac/v1"));
}

public interface IKeyEncryptionKeySource
{
    /// <summary>Loads the key, creating a protected key file on first use when allowed.</summary>
    KeyEncryptionKey Load(bool createIfMissing);

    /// <summary>Where the key comes from, for status display: never the key itself.</summary>
    string Describe();
}

/// <summary>
/// Reads the key-encryption key from an environment variable (base64 of 32
/// bytes) or from an OS-protected key file. The key file defaults to the
/// service account's private configuration directory, is created with
/// owner-only permissions, is refused when other users can read it, and is
/// refused when it lies inside the workspace that holds the encrypted data.
/// Backups of the workspace therefore never carry the key.
/// </summary>
public sealed class ConfiguredKeyEncryptionKeySource : IKeyEncryptionKeySource
{
    public const string DefaultEnvironmentVariable = "AGENT_STUDIO_SECRETS_KEK";
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OtherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly IConfiguration _configuration;
    private readonly Func<string, string?> _environment;
    private readonly object _gate = new();
    private KeyEncryptionKey? _loaded;

    public ConfiguredKeyEncryptionKeySource(IConfiguration configuration)
        : this(configuration, Environment.GetEnvironmentVariable)
    {
    }

    public ConfiguredKeyEncryptionKeySource(IConfiguration configuration, Func<string, string?> environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    private string EnvironmentVariable => _configuration["Secrets:KeyEnvironmentVariable"] is { Length: > 0 } name
        ? name : DefaultEnvironmentVariable;

    public string KeyFilePath => _configuration["Secrets:KeyFile"] is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : DefaultKeyFilePath();

    public string Describe()
        => _environment(EnvironmentVariable) is { Length: > 0 } ? "environment" : "key-file";

    public KeyEncryptionKey Load(bool createIfMissing)
    {
        lock (_gate)
        {
            if (_loaded is not null) return _loaded;
            if (_environment(EnvironmentVariable) is { Length: > 0 } encoded)
                return _loaded = new KeyEncryptionKey(DecodeKey(encoded, "the environment variable"), "environment");

            var path = KeyFilePath;
            RejectPathInsideWorkspace(path);
            if (!File.Exists(path))
            {
                if (!createIfMissing)
                    throw new SecretKeyUnavailableException("No key-encryption key is configured yet.");
                CreateKeyFile(path);
            }
            RejectUnprotectedFile(path);
            return _loaded = new KeyEncryptionKey(DecodeKey(File.ReadAllText(path).Trim(), "the key file"), "key-file");
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

    private static void RejectUnprotectedFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        if ((File.GetUnixFileMode(path) & OtherAccess) != 0)
            throw new SecretKeyUnavailableException(
                "The key-encryption key file is accessible to other users; restrict it to the service account (chmod 600).");
    }

    private static void CreateKeyFile(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, OwnerOnlyDirectory);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;
        var material = RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size);
        try
        {
            using var stream = new FileStream(path, options);
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
