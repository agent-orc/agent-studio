using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Security;

/// <summary>RFC 6238 SHA-1 TOTP and one-use recovery code primitives shared by both sign-in stacks.</summary>
public static class SecondFactorCodes
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewTotpSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    public static IReadOnlyList<string> NewRecoveryCodes()
        => Enumerable.Range(0, 10).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(10))).ToArray();

    public static string HashRecoveryCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(NormalizeRecoveryCode(code))));

    public static bool VerifyRecoveryCode(string code, string hash)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || hash.Length != 64) return false;
        var actual = HashRecoveryCode(code);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(hash));
    }

    public static bool VerifyTotp(string secret, string? code, DateTime now)
    {
        if (code is null || code.Length != 6 || code.Any(c => c is < '0' or > '9')) return false;
        byte[] key;
        try { key = FromBase32(secret); }
        catch (FormatException) { return false; }
        var step = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeSeconds() / 30;
        return Enumerable.Range(-1, 3).Any(offset =>
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Generate(key, step + offset)), Encoding.ASCII.GetBytes(code)));
    }

    public static string CurrentTotp(string secret, DateTime now)
        => Generate(FromBase32(secret), new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeSeconds() / 30);

    private static string Generate(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var digest = HMACSHA1.HashData(key, counter);
        var index = digest[^1] & 15;
        var value = ((digest[index] & 127) << 24) | (digest[index + 1] << 16)
            | (digest[index + 2] << 8) | digest[index + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string NormalizeRecoveryCode(string code)
        => new(code.Where(c => c != '-' && c != ' ').Select(char.ToUpperInvariant).ToArray());

    private static string ToBase32(byte[] bytes)
    {
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) { bits -= 5; result.Append(Alphabet[(buffer >> bits) & 31]); }
        }
        if (bits > 0) result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    private static byte[] FromBase32(string secret)
    {
        if (secret.Length is < 16 or > 128) throw new FormatException();
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in secret.ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException();
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
        }
        return bytes.ToArray();
    }
}
