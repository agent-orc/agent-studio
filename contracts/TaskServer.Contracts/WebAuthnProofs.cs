using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Security;

public sealed record PasskeyRegistrationProof(string CredentialId, string ClientDataJson,
    string AuthenticatorData, string Name);
public sealed record PasskeyAssertionProof(string CredentialId, string ClientDataJson,
    string AuthenticatorData, string Signature);

/// <summary>ES256 WebAuthn registration and assertion checks for the two Studio session stores.</summary>
public static class WebAuthnProofs
{
    public static string NewChallenge() => Encode(RandomNumberGenerator.GetBytes(32));

    public static string Encode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32768) throw new FormatException("Invalid WebAuthn value.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) & ~3, '='));
    }

    public static void VerifyClientData(string encoded, string expectedType, string challenge, string origin)
    {
        var bytes = Decode(encoded);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() != expectedType
            || root.GetProperty("origin").GetString() != origin
            || !CryptographicOperations.FixedTimeEquals(
                Decode(root.GetProperty("challenge").GetString()), Decode(challenge)))
            throw new CryptographicException("WebAuthn challenge, origin or ceremony does not match.");
        if (root.TryGetProperty("crossOrigin", out var crossOrigin) && crossOrigin.ValueKind == JsonValueKind.True)
            throw new CryptographicException("Cross-origin WebAuthn ceremonies are not allowed.");
    }

    public static uint VerifyAuthenticatorData(ReadOnlySpan<byte> data, string rpId, bool registration)
    {
        if (data.Length < 37) throw new CryptographicException("WebAuthn authenticator data is too short.");
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        if (!CryptographicOperations.FixedTimeEquals(data[..32], expected))
            throw new CryptographicException("WebAuthn relying party does not match.");
        if ((data[32] & 0x05) != 0x05 || (registration && (data[32] & 0x40) == 0))
            throw new CryptographicException("WebAuthn user presence and verification are required.");
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(33, 4));
    }

    public static string RegistrationPublicKey(string authenticatorData, string credentialId, string rpId)
    {
        var data = Decode(authenticatorData);
        VerifyAuthenticatorData(data, rpId, registration: true);
        if (data.Length < 55) throw new CryptographicException("WebAuthn attested credential is missing.");
        var idLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(53, 2));
        if (idLength is 0 or > 1024 || data.Length <= 55 + idLength
            || !CryptographicOperations.FixedTimeEquals(data.AsSpan(55, idLength), Decode(credentialId)))
            throw new CryptographicException("WebAuthn credential identifier does not match.");
        var reader = new CoseReader(data.AsSpan(55 + idLength));
        var key = reader.ReadP256Key();
        using var ecdsa = ECDsa.Create(key);
        return Encode(ecdsa.ExportSubjectPublicKeyInfo());
    }

    public static uint VerifyAssertion(string clientDataJson, string authenticatorData, string signature,
        string publicKey, string challenge, string origin, string rpId, uint previousCounter)
    {
        VerifyClientData(clientDataJson, "webauthn.get", challenge, origin);
        var authBytes = Decode(authenticatorData);
        var counter = VerifyAuthenticatorData(authBytes, rpId, registration: false);
        if (previousCounter != 0 && counter != 0 && counter <= previousCounter)
            throw new CryptographicException("WebAuthn sign counter did not advance.");
        var clientHash = SHA256.HashData(Decode(clientDataJson));
        var signed = new byte[authBytes.Length + clientHash.Length];
        authBytes.CopyTo(signed, 0);
        clientHash.CopyTo(signed, authBytes.Length);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Decode(publicKey), out _);
        if (!ecdsa.VerifyData(signed, Decode(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new CryptographicException("WebAuthn assertion signature is invalid.");
        return counter;
    }

    private ref struct CoseReader(ReadOnlySpan<byte> source)
    {
        private ReadOnlySpan<byte> _source = source;
        private int _offset;

        public ECParameters ReadP256Key()
        {
            var pairs = ReadLength(5);
            int? kind = null, algorithm = null, curve = null;
            byte[]? x = null, y = null;
            for (var i = 0; i < pairs; i++)
            {
                var label = ReadInteger();
                switch (label)
                {
                    case 1: kind = ReadInteger(); break;
                    case 3: algorithm = ReadInteger(); break;
                    case -1: curve = ReadInteger(); break;
                    case -2: x = ReadBytes(); break;
                    case -3: y = ReadBytes(); break;
                    default: throw new CryptographicException("Unsupported COSE key field.");
                }
            }
            if (kind != 2 || algorithm != -7 || curve != 1 || x?.Length != 32 || y?.Length != 32)
                throw new CryptographicException("Only ES256 P-256 passkeys are supported.");
            return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
        }

        private int ReadInteger()
        {
            var initial = Take();
            var major = initial >> 5;
            if (major is not (0 or 1)) throw new CryptographicException("Invalid COSE integer.");
            var number = ReadAdditional(initial & 31);
            return major == 0 ? number : -1 - number;
        }

        private byte[] ReadBytes()
        {
            var length = ReadLength(2);
            if (length > 1024 || _source.Length - _offset < length)
                throw new CryptographicException("Invalid COSE byte string.");
            var value = _source.Slice(_offset, length).ToArray();
            _offset += length;
            return value;
        }

        private int ReadLength(int major)
        {
            var initial = Take();
            if ((initial >> 5) != major) throw new CryptographicException("Invalid COSE key shape.");
            return ReadAdditional(initial & 31);
        }

        private int ReadAdditional(int value)
        {
            if (value < 24) return value;
            if (value == 24) return Take();
            if (value == 25) return (Take() << 8) | Take();
            throw new CryptographicException("Unsupported COSE length.");
        }

        private byte Take()
        {
            if (_offset >= _source.Length) throw new CryptographicException("Truncated COSE key.");
            return _source[_offset++];
        }
    }
}
