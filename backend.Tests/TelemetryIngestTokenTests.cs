using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>OTLP ingest tokens in the access store: scoped, expiring, revocable, stored as hashes (IP-15).</summary>
public sealed class TelemetryIngestTokenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "telemetry-tokens-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Token_authenticates_to_its_environment_and_signals_and_only_its_hash_is_stored()
    {
        var store = Store();
        var (token, secret) = store.CreateTelemetryIngestToken("Shop", "stage", "collector", ["logs"], null, "usr_1");

        var principal = store.AuthenticateTelemetryIngest(secret);

        Assert.StartsWith("otl." + token.Id + ".", secret);
        Assert.Equal(("Shop", "stage"), (principal!.Project, principal.Environment));
        Assert.Equal(["logs"], principal.Signals);
        var persisted = File.ReadAllText(Path.Combine(store.SecurityFolder, "access-state.json"));
        Assert.DoesNotContain(secret, persisted);
        Assert.Contains(PasswordSecretHasher.HashSecret(secret), persisted);
        // A restarted Studio reads the token back from the access store.
        Assert.NotNull(Store().AuthenticateTelemetryIngest(secret));
    }

    [Fact]
    public void Wrong_secret_other_prefix_and_malformed_bearers_are_rejected()
    {
        var store = Store();
        var (token, secret) = store.CreateTelemetryIngestToken("Shop", "stage", null, null, null, null);

        Assert.Null(store.AuthenticateTelemetryIngest(secret[..^1] + (secret[^1] == 'a' ? 'b' : 'a')));
        Assert.Null(store.AuthenticateTelemetryIngest("rnr" + secret[3..]));
        Assert.Null(store.AuthenticateTelemetryIngest($"otl.{token.Id}"));
        Assert.Null(store.AuthenticateTelemetryIngest(null));
    }

    [Fact]
    public void Revoked_and_expired_tokens_stop_authenticating()
    {
        var store = Store();
        var (revoked, revokedSecret) = store.CreateTelemetryIngestToken("Shop", "stage", null, null, null, null);
        var (_, expiringSecret) = store.CreateTelemetryIngestToken("Shop", "stage", null, null, _clock.GetUtcNow().UtcDateTime.AddHours(1), null);

        Assert.False(store.RevokeTelemetryIngestToken("Shop", "production", revoked.Id));
        Assert.True(store.RevokeTelemetryIngestToken("shop", "stage", revoked.Id));
        Assert.Null(store.AuthenticateTelemetryIngest(revokedSecret));
        Assert.NotNull(store.AuthenticateTelemetryIngest(expiringSecret));

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(store.AuthenticateTelemetryIngest(expiringSecret));
        Assert.Equal(2, store.ListTelemetryIngestTokens("Shop", "stage").Count);
        Assert.Empty(store.ListTelemetryIngestTokens("Shop", "production"));
    }

    [Fact]
    public void Last_use_is_recorded_at_most_every_five_minutes()
    {
        var store = Store();
        var (_, secret) = store.CreateTelemetryIngestToken("Shop", "stage", null, null, null, null);
        var first = _clock.GetUtcNow().UtcDateTime;

        store.AuthenticateTelemetryIngest(secret);
        _clock.Advance(TimeSpan.FromMinutes(4));
        store.AuthenticateTelemetryIngest(secret);
        Assert.Equal(first, Assert.Single(store.ListTelemetryIngestTokens("Shop", "stage")).LastUsedAt);

        _clock.Advance(TimeSpan.FromMinutes(1));
        store.AuthenticateTelemetryIngest(secret);
        Assert.Equal(first.AddMinutes(5), Assert.Single(store.ListTelemetryIngestTokens("Shop", "stage")).LastUsedAt);
    }

    [Theory]
    [InlineData("traces")]
    [InlineData("")]
    public void Unknown_signals_and_past_expiry_are_refused(string signal)
    {
        var store = Store();
        var unknown = Assert.Throws<SecurityOperationException>(() => store.CreateTelemetryIngestToken("Shop", "stage", null, [signal], null, null));
        var past = Assert.Throws<SecurityOperationException>(() =>
            store.CreateTelemetryIngestToken("Shop", "stage", null, null, _clock.GetUtcNow().UtcDateTime.AddMinutes(-1), null));

        Assert.Equal(("invalid-signal", "invalid-expiry"), (unknown.Code, past.Code));
    }

    [Fact]
    public void Credential_redactor_masks_ingest_tokens()
    {
        var (_, secret) = Store().CreateTelemetryIngestToken("Shop", "stage", null, null, null, null);

        Assert.Equal("token [REDACTED_CREDENTIAL] used", CredentialRedactor.Redact($"token {secret} used"));
    }

    private AccessSecurityStore Store()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        return new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock);
    }
}
