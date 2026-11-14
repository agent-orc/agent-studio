using System.Security.Cryptography;
using AgentStudio.Management;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The Studio backend reads the Update Service trigger token that the service
/// generated beside its configuration, so the Update Center can send it.
/// Tokens are generated fixtures.
/// </summary>
public sealed class UpdateServiceTokenEndpointsTests : IDisposable
{
    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "update-token-read-" + Guid.NewGuid().ToString("N"));

    public UpdateServiceTokenEndpointsTests()
    {
        Directory.CreateDirectory(Path.Combine(_checkout, "backend"));
        Directory.CreateDirectory(Path.Combine(_checkout, "update-service"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_checkout, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string BackendRoot => Path.Combine(_checkout, "backend");

    private static string FixtureToken() => "fixture-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Default_path_is_the_token_file_beside_the_update_service_configuration()
    {
        Assert.Equal(Path.Combine(_checkout, "update-service", ".update-token"),
            UpdateServiceTokenEndpoints.TokenFilePath(Config(), BackendRoot));
    }

    [Fact]
    public void Reads_the_generated_token_file()
    {
        var token = FixtureToken();
        File.WriteAllText(Path.Combine(_checkout, "update-service", ".update-token"), token + "\n");
        Assert.Equal(token, UpdateServiceTokenEndpoints.ReadToken(Config(), BackendRoot));
    }

    [Fact]
    public void A_configured_token_file_path_and_a_configured_token_take_precedence()
    {
        var stored = FixtureToken();
        var elsewhere = Path.Combine(_checkout, "elsewhere.token");
        File.WriteAllText(elsewhere, stored);
        Assert.Equal(stored, UpdateServiceTokenEndpoints.ReadToken(
            Config((UpdateServiceTokenEndpoints.TokenFileKey, elsewhere)), BackendRoot));

        var configured = FixtureToken();
        Assert.Equal(configured, UpdateServiceTokenEndpoints.ReadToken(
            Config((UpdateServiceTokenEndpoints.TokenFileKey, elsewhere), (UpdateServiceTokenEndpoints.TokenKey, configured)), BackendRoot));
    }

    [Fact]
    public void A_missing_or_empty_token_file_yields_no_token()
    {
        if (Environment.GetEnvironmentVariable("ATP_UPDATE_TOKEN") is { Length: > 0 }) return;
        Assert.Null(UpdateServiceTokenEndpoints.ReadToken(Config(), BackendRoot));
        File.WriteAllText(Path.Combine(_checkout, "update-service", ".update-token"), "  \n");
        Assert.Null(UpdateServiceTokenEndpoints.ReadToken(Config(), BackendRoot));
    }
}
