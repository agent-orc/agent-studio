using System.Net.Http.Json;
using System.Security.Cryptography;

namespace AgentRunner;

public sealed partial class TaskServerClient
{
    /// <summary>Send a host selection receipt to Studio with the runner Events scope.</summary>
    public async Task<bool> PostModelSelectionEventAsync(object entry, CancellationToken ct)
    {
        var baseUrl = _options?.PostMergeStateUrl ?? _options?.ServerUrl;
        var target = baseUrl is null
            ? new Uri(_http.BaseAddress!, "/api/model-selection/events")
            : new Uri(baseUrl.TrimEnd('/') + "/api/model-selection/events");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await _http.PostAsJsonAsync(target, entry, deadline.Token);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                if (attempt == 2) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
        }
        return false;
    }

    public static string NewModelSelectionId()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
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
            chars[i] = alphabet[value];
        }
        return new string(chars);
    }
}
