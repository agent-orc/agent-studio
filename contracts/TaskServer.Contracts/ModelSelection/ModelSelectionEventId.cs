using System.Security.Cryptography;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Sortable ULID for selection receipts shared by the monolith and remote hosts.</summary>
public static class ModelSelectionEventId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<byte> data = stackalloc byte[16];
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--) { data[i] = (byte)milliseconds; milliseconds >>= 8; }
        RandomNumberGenerator.Fill(data[6..]);
        Span<char> chars = stackalloc char[26];
        var bit = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            var value = 0;
            for (var b = 0; b < 5; b++)
            {
                var position = bit++ - 2;
                value = (value << 1) | (position < 0 ? 0 : (data[position / 8] >> (7 - position % 8)) & 1);
            }
            chars[i] = Alphabet[value];
        }
        return new string(chars);
    }
}
