using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.AttemptCredentials;

/// <summary>External vault adapters implement the same root-material boundary.</summary>
public interface IAttemptRootSecretStore
{
    Task<string?> ReadAsync(string name, CancellationToken ct);
    Task WriteAsync(string name, string value, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}

/// <summary>
/// AES-GCM root store. The operator-owned wrapping key lives in a different,
/// mode-restricted file; it is never accepted as an environment value.
/// </summary>
public sealed class EncryptedAttemptRootSecretStore(string directory, string wrappingKeyFile) : IAttemptRootSecretStore
{
    private static string SafeName(string name)
    {
        if (name.Length is < 1 or > 120 || name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Invalid root-secret name.", nameof(name));
        return name;
    }

    private byte[] Key()
    {
        var info = new FileInfo(wrappingKeyFile);
        if (!info.Exists) throw new InvalidOperationException("Attempt credential wrapping key is unavailable.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(wrappingKeyFile) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            throw new InvalidOperationException("Attempt credential wrapping key is not restricted.");
        var key = Convert.FromBase64String(File.ReadAllText(wrappingKeyFile).Trim());
        if (key.Length != 32) throw new InvalidOperationException("Attempt credential wrapping key must be 32 bytes.");
        return key;
    }

    public async Task<string?> ReadAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(directory, SafeName(name) + ".enc");
        if (!File.Exists(path)) return null;
        var envelope = await File.ReadAllBytesAsync(path, ct);
        if (envelope.Length < 1 + 12 + 16 || envelope[0] != 1)
            throw new InvalidDataException("Invalid root-secret envelope.");
        var plaintext = new byte[envelope.Length - 29];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(envelope.AsSpan(1, 12), envelope.AsSpan(29), envelope.AsSpan(13, 16),
            plaintext, Encoding.UTF8.GetBytes(name));
        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task WriteAsync(string name, string value, CancellationToken ct)
    {
        SafeName(name);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var envelope = new byte[1 + 12 + 16 + plaintext.Length];
        envelope[0] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
        using (var aes = new AesGcm(Key(), 16))
            aes.Encrypt(envelope.AsSpan(1, 12), plaintext, envelope.AsSpan(29), envelope.AsSpan(13, 16),
                Encoding.UTF8.GetBytes(name));
        var path = Path.Combine(directory, name + ".enc");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, envelope, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        CryptographicOperations.ZeroMemory(plaintext);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, SafeName(name) + ".enc");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
