using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperatorDpopTests
{
    internal static string Proof(ECDsa key, string token, string method, string uri, DateTimeOffset now,
        string? jti = null, object? header = null, object? payload = null)
    {
        var point = key.ExportParameters(false).Q;
        var jwk = new OperatorDevicePublicKey("EC", "P-256", B64(point.X!), B64(point.Y!));
        var h = B64(JsonSerializer.SerializeToUtf8Bytes(header ?? new { typ = "dpop+jwt", alg = "ES256", jwk = new { kty = jwk.Kty, crv = jwk.Crv, x = jwk.X, y = jwk.Y } }));
        var p = B64(JsonSerializer.SerializeToUtf8Bytes(payload ?? new
        { htm = method, htu = uri, iat = now.ToUnixTimeSeconds(), jti = jti ?? Guid.NewGuid().ToString("N"), ath = B64(SHA256.HashData(Encoding.ASCII.GetBytes(token))) }));
        return h + "." + p + "." + B64(key.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    internal static string Thumb(ECDsa key)
    {
        var point = key.ExportParameters(false).Q;
        return new OperatorDevicePublicKey("EC", "P-256", B64(point.X!), B64(point.Y!)).Thumbprint();
    }

    private static string B64(byte[] bytes) => OperatorDevicePublicKey.Base64Url(bytes);
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private const string Address = "https://studio.test/api/tasks";
    private static string Token() => string.Join('.', "oas", Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void Valid_proof_is_single_use_and_binds_token_method_address_and_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new OperatorDpopVerifier();
        var token = Token();
        var thumb = Thumb(key);
        var proof = Proof(key, token, "GET", Address, Now);
        Assert.Equal("dpop-required", verifier.Validate(null, "GET", Address, token, thumb, Now));
        Assert.Equal("dpop-invalid", verifier.Validate(proof, "POST", Address, token, thumb, Now));
        Assert.Equal("dpop-invalid", verifier.Validate(proof, "GET", Address + "/other", token, thumb, Now));
        Assert.Equal("dpop-invalid", verifier.Validate(proof, "GET", "https://other.test/api/tasks", token, thumb, Now));
        Assert.Equal("dpop-invalid", verifier.Validate(proof, "GET", Address, Token(), thumb, Now));
        Assert.Equal("dpop-key-mismatch", verifier.Validate(proof, "GET", Address, token, Thumb(other), Now));
        Assert.Null(verifier.Validate(proof, "GET", Address, token, thumb, Now));
        Assert.Equal("dpop-replayed", verifier.Validate(proof, "GET", Address, token, thumb, Now));
    }

    [Theory]
    [InlineData(-61, "dpop-stale")]
    [InlineData(61, "dpop-stale")]
    [InlineData(-60, null)]
    [InlineData(60, null)]
    public void Proof_time_window_is_sixty_seconds_in_both_directions(int seconds, string? expected)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = Token();
        Assert.Equal(expected, new OperatorDpopVerifier().Validate(Proof(key, token, "GET", Address, Now.AddSeconds(seconds)),
            "GET", Address, token, Thumb(key), Now));
    }

    [Fact]
    public void Replay_cache_is_bounded_and_preserves_live_entries_for_five_minutes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new OperatorDpopVerifier(1);
        var token = Token();
        var first = Proof(key, token, "GET", Address, Now, "first");
        Assert.Null(verifier.Validate(first, "GET", Address, token, Thumb(key), Now));
        Assert.Equal("dpop-invalid", verifier.Validate(Proof(key, token, "GET", Address, Now), "GET", Address, token, Thumb(key), Now));
        Assert.Equal("dpop-replayed", verifier.Validate(Proof(key, token, "GET", Address, Now.AddMinutes(4), "first"), "GET", Address, token, Thumb(key), Now.AddMinutes(4)));
        Assert.Null(verifier.Validate(Proof(key, token, "GET", Address, Now.AddMinutes(5)), "GET", Address, token, Thumb(key), Now.AddMinutes(5)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a.b.c")]
    [InlineData("...a")]
    public void Malformed_proofs_are_typed_refusals(string proof)
    {
        Assert.Equal("dpop-invalid", new OperatorDpopVerifier().Validate(proof, "GET", Address, Token(), "thumb", Now));
    }

    [Fact]
    public void Signature_and_Jose_header_are_checked()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new OperatorDpopVerifier();
        var token = Token();
        var proof = Proof(key, token, "GET", Address, Now);
        var parts = proof.Split('.');
        var signature = Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/') + "==");
        signature[0] ^= 1;
        Assert.Equal("dpop-invalid", verifier.Validate(parts[0] + "." + parts[1] + "." + B64(signature), "GET", Address, token, Thumb(key), Now));
        Assert.Equal("dpop-invalid", verifier.Validate(Proof(key, token, "GET", Address, Now, header: new { typ = "JWT", alg = "none" }), "GET", Address, token, Thumb(key), Now));
    }

    [Fact]
    public void Private_Jwk_duplicate_claims_and_query_or_fragment_in_proof_uri_are_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = Token();
        var thumb = Thumb(key);
        var parameters = key.ExportParameters(true);
        var jwk = new { kty = "EC", crv = "P-256", x = B64(parameters.Q.X!), y = B64(parameters.Q.Y!), d = B64(parameters.D!) };
        var verifier = new OperatorDpopVerifier();
        Assert.Equal("dpop-invalid", verifier.Validate(Proof(key, token, "GET", Address, Now,
            header: new { typ = "dpop+jwt", alg = "ES256", jwk }), "GET", Address, token, thumb, Now));
        foreach (var suffix in new[] { "?query=1", "#fragment" })
            Assert.Equal("dpop-invalid", verifier.Validate(Proof(key, token, "GET", Address + suffix, Now), "GET", Address, token, thumb, Now));
        var duplicate = JsonDocument.Parse("{\"jti\":\"one\",\"jti\":\"two\"}").RootElement;
        Assert.Equal("dpop-invalid", verifier.Validate(Proof(key, token, "GET", Address, Now, payload: duplicate), "GET", Address, token, thumb, Now));
    }

    [Fact]
    public void Public_uri_uses_resolved_origin_original_escaped_path_and_no_query()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("STUDIO.test", 443);
        context.Request.Path = "/api/rewritten";
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/studio/api/%74asks?limit=1";
        context.Request.Headers["X-Forwarded-Host"] = "attacker.test";
        Assert.Equal("https://studio.test/studio/api/tasks", OperatorDpopVerifier.PublicUri(context.Request));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Verification_median_over_one_thousand_proofs_is_below_two_milliseconds()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new OperatorDpopVerifier();
        var token = Token();
        var thumb = Thumb(key);
        var proofs = Enumerable.Range(0, 1050).Select(_ => Proof(key, token, "GET", Address, Now)).ToArray();
        var samples = new List<double>();
        for (var i = 0; i < proofs.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Assert.Null(verifier.Validate(proofs[i], "GET", Address, token, thumb, Now));
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (i >= 50) samples.Add(elapsed);
        }
        samples.Sort();
        var median = (samples[499] + samples[500]) / 2;
        var report = $"1000 server proof validations after 50 warmups; median {median:F6} ms; p95 {samples[949]:F6} ms; max {samples[^1]:F6} ms. Signing excluded.\n";
        if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { } folder)
            File.WriteAllText(Path.Combine(folder, "dpop-verification-performance.txt"), report);
        Assert.True(median < 2, report);
    }
}
