using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Secrets;

/// <summary>One stored secret version: the wrapped data key and the AES-GCM ciphertext.</summary>
public sealed record SealedSecretVersion
{
    /// <summary>Format 2 wraps the data key under an HKDF-derived wrap key; format 1 used the raw KEK and is refused.</summary>
    public const int CurrentFormat = 2;

    public required int Format { get; init; }
    public required string SecretId { get; init; }
    public required int Version { get; init; }
    public required string KeyId { get; init; }
    public required string WrapNonce { get; init; }
    public required string WrappedKey { get; init; }
    public required string Nonce { get; init; }
    public required string Ciphertext { get; init; }
}

/// <summary>
/// Envelope encryption for the built-in store. Every version gets its own
/// random 256-bit data key; the value is sealed with AES-GCM under that key
/// and the data key is sealed with AES-GCM under a wrap key that HKDF derives
/// from the key-encryption key (the metadata MAC key is a separate HKDF
/// output, so the raw KEK never keys a cipher or MAC itself). Both
/// seals bind the secret id and version as associated data, so a version file
/// copied to another secret or version fails authentication.
/// </summary>
public static class SecretEnvelope
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static SealedSecretVersion Seal(KeyEncryptionKey kek, string secretId, int version, ReadOnlySpan<byte> plaintext)
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = Encrypt(dataKey, nonce, plaintext, Context(secretId, version, "value"));
            var wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var wrapped = WithWrapKey(kek, wrapKey => Encrypt(wrapKey, wrapNonce, dataKey, Context(secretId, version, "data-key")));
            return new SealedSecretVersion
            {
                Format = SealedSecretVersion.CurrentFormat,
                SecretId = secretId,
                Version = version,
                KeyId = kek.Id,
                WrapNonce = Convert.ToBase64String(wrapNonce),
                WrappedKey = Convert.ToBase64String(wrapped),
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public static byte[] Open(KeyEncryptionKey kek, SealedSecretVersion sealedVersion, string secretId, int version)
    {
        if (sealedVersion.Format != SealedSecretVersion.CurrentFormat)
            throw new SecretIntegrityException($"Unsupported sealed version format {sealedVersion.Format}.");
        if (!string.Equals(sealedVersion.SecretId, secretId, StringComparison.Ordinal) || sealedVersion.Version != version)
            throw new SecretIntegrityException("The stored version does not belong to this secret and version.");
        if (!string.Equals(sealedVersion.KeyId, kek.Id, StringComparison.Ordinal))
            throw new SecretKeyUnavailableException(
                $"The version was sealed with key-encryption key {sealedVersion.KeyId}; the configured key is {kek.Id}.");

        byte[]? dataKey = null;
        try
        {
            dataKey = WithWrapKey(kek, wrapKey => Decrypt(wrapKey, Decode(sealedVersion.WrapNonce), Decode(sealedVersion.WrappedKey),
                Context(secretId, version, "data-key")));
            return Decrypt(dataKey, Decode(sealedVersion.Nonce), Decode(sealedVersion.Ciphertext), Context(secretId, version, "value"));
        }
        finally
        {
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static T WithWrapKey<T>(KeyEncryptionKey kek, Func<byte[], T> operation)
    {
        var wrapKey = kek.DeriveWrapKey();
        try { return operation(wrapKey); }
        finally { CryptographicOperations.ZeroMemory(wrapKey); }
    }

    private static byte[] Context(string secretId, int version, string part)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"agent-studio/secret/v1|{secretId}|{version}|{part}"));

    private static byte[] Encrypt(byte[] key, byte[] nonce, ReadOnlySpan<byte> plaintext, byte[] associatedData)
    {
        var output = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), associatedData);
        return output;
    }

    private static byte[] Decrypt(byte[] key, byte[] nonce, byte[] sealedBytes, byte[] associatedData)
    {
        if (nonce.Length != NonceSize || sealedBytes.Length < TagSize)
            throw new SecretIntegrityException("The stored version is truncated.");
        var plaintext = new byte[sealedBytes.Length - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, sealedBytes.AsSpan(0, plaintext.Length), sealedBytes.AsSpan(plaintext.Length), plaintext, associatedData);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SecretIntegrityException("The stored version failed its integrity check: " + ex.GetType().Name + ".");
        }
    }

    private static byte[] Decode(string value)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new SecretIntegrityException("The stored version is not valid base64: " + ex.Message); }
    }
}
