using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.QualityStudio;

/// <summary>Signs a compact EdDSA assertion with an OpenSSL Ed25519 raw seed.</summary>
public static class Ed25519Assertion
{
    private const int Ed25519KeyType = 1087;
    public const string KeyId = "quality-studio-v1";

    public static string Sign(byte[] seed, object claims)
    {
        if (seed.Length != 32) throw new CryptographicException("The Ed25519 signing seed must be 32 bytes.");
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", typ = "JWT", kid = KeyId }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var message = Encoding.ASCII.GetBytes(header + "." + payload);
        var key = Native.EVP_PKEY_new_raw_private_key(Ed25519KeyType, IntPtr.Zero, seed, (nuint)seed.Length);
        if (key == IntPtr.Zero) throw new CryptographicException("OpenSSL could not load the Ed25519 key.");
        try
        {
            var context = Native.EVP_MD_CTX_new();
            if (context == IntPtr.Zero) throw new CryptographicException("OpenSSL could not create a signing context.");
            try
            {
                if (Native.EVP_DigestSignInit(context, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, key) != 1)
                    throw new CryptographicException("OpenSSL could not initialize Ed25519 signing.");
                var signature = new byte[64];
                nuint length = (nuint)signature.Length;
                if (Native.EVP_DigestSign(context, signature, ref length, message, (nuint)message.Length) != 1 || length != 64)
                    throw new CryptographicException("OpenSSL could not sign the assertion.");
                return Encoding.ASCII.GetString(message) + "." + Base64Url(signature);
            }
            finally { Native.EVP_MD_CTX_free(context); }
        }
        finally { Native.EVP_PKEY_free(key); }
    }

    public static byte[] PublicKey(byte[] seed)
    {
        if (seed.Length != 32) throw new CryptographicException("The Ed25519 signing seed must be 32 bytes.");
        var key = Native.EVP_PKEY_new_raw_private_key(Ed25519KeyType, IntPtr.Zero, seed, (nuint)seed.Length);
        if (key == IntPtr.Zero) throw new CryptographicException("OpenSSL could not load the Ed25519 key.");
        try
        {
            var publicKey = new byte[32];
            nuint length = (nuint)publicKey.Length;
            if (Native.EVP_PKEY_get_raw_public_key(key, publicKey, ref length) != 1 || length != 32)
                throw new CryptographicException("OpenSSL could not derive the Ed25519 public key.");
            return publicKey;
        }
        finally { Native.EVP_PKEY_free(key); }
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static class Native
    {
        [DllImport("libcrypto.so.3")] internal static extern IntPtr EVP_PKEY_new_raw_private_key(int type, IntPtr engine, byte[] key, nuint length);
        [DllImport("libcrypto.so.3")] internal static extern void EVP_PKEY_free(IntPtr key);
        [DllImport("libcrypto.so.3")] internal static extern IntPtr EVP_MD_CTX_new();
        [DllImport("libcrypto.so.3")] internal static extern void EVP_MD_CTX_free(IntPtr context);
        [DllImport("libcrypto.so.3")] internal static extern int EVP_DigestSignInit(IntPtr context, IntPtr keyContext, IntPtr digest, IntPtr engine, IntPtr key);
        [DllImport("libcrypto.so.3")] internal static extern int EVP_DigestSign(IntPtr context, byte[] signature, ref nuint signatureLength, byte[] message, nuint messageLength);
        [DllImport("libcrypto.so.3")] internal static extern int EVP_PKEY_get_raw_public_key(IntPtr key, byte[] publicKey, ref nuint length);
    }
}
