using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace AgentStudio.Security;

/// <summary>RFC 9449 proofs for ES-4. Replay entries are process-local, bounded and never evicted while live.</summary>
public sealed class OperatorDpopVerifier(int capacity = 100_000)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset Until)> _expiry = new();

    public string? Validate(HttpRequest request, string token, string thumbprint, DateTimeOffset now)
        => request.Headers["DPoP"].Count > 1 ? "dpop-invalid"
            : Validate(request.Headers["DPoP"].FirstOrDefault(), request.Method, PublicUri(request), token, thumbprint, now);

    /// <summary>Uses only the scheme and host already resolved by trusted-proxy middleware, never raw forwarded headers.</summary>
    public static string PublicUri(HttpRequest request)
    {
        var target = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var path = target?.Split('?')[0];
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
            path = request.PathBase.ToUriComponent() + request.Path.ToUriComponent();
        return NormalizeUri($"{request.Scheme}://{request.Host}{path}");
    }

    private static string NormalizeUri(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new FormatException("Invalid proof URI.");
        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    public string? Validate(string? proof, string method, string uri, string token, string thumbprint, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(proof)) return "dpop-required";
        if (proof.Length > 4096) return "dpop-invalid";
        try
        {
            var parts = proof.Split('.');
            if (parts.Length != 3) return "dpop-invalid";
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var h = header.RootElement;
            var p = payload.RootElement;
            if (!UniqueObject(h) || !UniqueObject(p) || h.GetProperty("typ").GetString() != "dpop+jwt"
                || h.GetProperty("alg").GetString() != "ES256" || h.TryGetProperty("crit", out _)
                || h.TryGetProperty("b64", out _)) return "dpop-invalid";
            var jwk = h.GetProperty("jwk");
            if (!UniqueObject(jwk) || jwk.TryGetProperty("d", out _) || jwk.GetProperty("kty").GetString() != "EC"
                || jwk.GetProperty("crv").GetString() != "P-256") return "dpop-invalid";
            var x = Decode(jwk.GetProperty("x").GetString()!);
            var y = Decode(jwk.GetProperty("y").GetString()!);
            if (x.Length != 32 || y.Length != 32) return "dpop-invalid";
            var publicKey = new OperatorDevicePublicKey("EC", "P-256", OperatorDevicePublicKey.Base64Url(x), OperatorDevicePublicKey.Base64Url(y));
            if (publicKey.Thumbprint() != thumbprint) return "dpop-key-mismatch";
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            var signature = Decode(parts[2]);
            if (signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return "dpop-invalid";
            if (p.GetProperty("htm").GetString() != method || NormalizeUri(p.GetProperty("htu").GetString()!) != NormalizeUri(uri)
                || p.GetProperty("ath").GetString() != OperatorDevicePublicKey.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(token))))
                return "dpop-invalid";
            if (!p.GetProperty("iat").TryGetInt64(out var issued)
                || Math.Abs((double)now.ToUnixTimeSeconds() - issued) > 60) return "dpop-stale";
            var jti = p.GetProperty("jti").GetString();
            if (string.IsNullOrWhiteSpace(jti) || jti.Length > 128) return "dpop-invalid";
            var replayKey = thumbprint + ":" + jti;
            lock (_gate)
            {
                while (_expiry.TryPeek(out var entry) && entry.Until <= now)
                {
                    _expiry.Dequeue();
                    _seen.Remove(entry.Key);
                }
                if (_seen.ContainsKey(replayKey)) return "dpop-replayed";
                // Fail closed at capacity, preserving every live replay entry for five minutes.
                if (_seen.Count >= capacity) return "dpop-invalid";
                var until = now.AddMinutes(5);
                _seen.Add(replayKey, until);
                _expiry.Enqueue((replayKey, until));
            }
            return null;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or CryptographicException)
        {
            return "dpop-invalid";
        }
    }

    private static bool UniqueObject(JsonElement value)
        => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
            == value.EnumerateObject().Count();

    private static byte[] Decode(string value)
    {
        if (value.Length == 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new FormatException("Invalid base64url.");
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (OperatorDevicePublicKey.Base64Url(bytes) != value) throw new FormatException("Noncanonical base64url.");
        return bytes;
    }
}
