using System.Text;
using System.Text.Json;
using AgentStudio.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Provider semantics over the built-in store: versions, rotation, revocation, resolution, audit and scrubbing.</summary>
public sealed class SecretProviderTests : IDisposable
{
    private static readonly SecretActor Owner = SecretActor.Human("first.owner");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "secret-provider-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly SecretValueScrubber _scrubber;
    private readonly SecretAuditLog _audit;
    private readonly SecretProvider _provider;
    private readonly SecretWorkload _stageDeploy = new(SecretWorkload.DeployJob, "stage-host", "memowikis", "stage", ["DATABASE_URL", "MAIL_KEY"]);

    public SecretProviderTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
            ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek"),
        }).Build();
        _scrubber = new SecretValueScrubber(_clock);
        _audit = new SecretAuditLog(configuration);
        _provider = new SecretProvider(
            new BuiltInSecretStore(configuration, new ConfiguredKeyEncryptionKeySource(configuration, _ => null)),
            _audit, _scrubber, _clock, NullLogger<SecretProvider>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string NewValue() => "value-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Rotation_keeps_the_old_version_until_revoke_and_records_who_and_when()
    {
        var scope = SecretScope.ForEnvironment("memowikis", "stage");
        var (first, second) = (NewValue(), NewValue());
        await _provider.PutAsync(scope, "DATABASE_URL", first, Owner);
        _clock.Advance(TimeSpan.FromDays(30));
        var rotated = await _provider.RotateAsync(scope, "DATABASE_URL", second, SecretActor.Human("second.admin"));

        Assert.Equal(_clock.GetUtcNow().UtcDateTime, rotated.RotatedAt);
        Assert.Equal("human:second.admin", rotated.RotatedBy);
        Assert.Equal("human:first.owner", rotated.CreatedBy);
        Assert.Equal(second, (await Resolve("secret://memowikis/stage/DATABASE_URL")).Value);
        Assert.Equal(first, (await Resolve("secret://memowikis/stage/DATABASE_URL@1")).Value);

        await _provider.RevokeAsync(scope, "DATABASE_URL", 1, Owner);
        await Assert.ThrowsAsync<SecretRevokedException>(() => Resolve("secret://memowikis/stage/DATABASE_URL@1"));
        Assert.Equal(second, (await Resolve("secret://memowikis/stage/DATABASE_URL")).Value);

        var record = Assert.Single(await _provider.ListAsync(scope));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, record.LastUsedAt);
        Assert.Equal("workload:stage-host", record.LastUsedBy);
        Assert.Equal("deploy:stage", record.LastUsedPurpose);
    }

    [Fact]
    public async Task Put_on_an_existing_secret_replaces_it_and_revokes_earlier_versions()
    {
        var scope = SecretScope.ForProject("memowikis");
        var replacement = NewValue();
        await _provider.PutAsync(scope, "MAIL_KEY", NewValue(), Owner);
        await _provider.PutAsync(scope, "MAIL_KEY", replacement, Owner);

        await Assert.ThrowsAsync<SecretRevokedException>(() => Resolve("secret://memowikis/MAIL_KEY@1"));
        Assert.Equal(replacement, (await Resolve("secret://memowikis/MAIL_KEY")).Value);
    }

    [Fact]
    public async Task Revoking_every_version_stops_issuance_and_delete_removes_the_secret()
    {
        var scope = SecretScope.Workspace;
        await _provider.PutAsync(scope, "MAIL_KEY", NewValue(), Owner);
        var revoked = await _provider.RevokeAsync(scope, "MAIL_KEY", null, Owner);
        Assert.Null(SecretVersionPolicy.CurrentVersion(revoked));
        await Assert.ThrowsAsync<SecretRevokedException>(() => Resolve("secret://workspace/MAIL_KEY"));

        await _provider.DeleteAsync(scope, "MAIL_KEY", Owner);
        Assert.Empty(await _provider.ListAsync());
        await Assert.ThrowsAsync<SecretNotFoundException>(() => Resolve("secret://workspace/MAIL_KEY"));
        await Assert.ThrowsAsync<SecretNotFoundException>(() => _provider.DeleteAsync(scope, "MAIL_KEY", Owner));
    }

    [Fact]
    public async Task Deeper_scopes_inherit_and_override()
    {
        var (workspace, prod) = (NewValue(), NewValue());
        await _provider.PutAsync(SecretScope.Workspace, "MAIL_KEY", workspace, Owner);
        await _provider.PutAsync(SecretScope.ForEnvironment("memowikis", "prod"), "MAIL_KEY", prod, Owner);
        var prodDeploy = _stageDeploy with { Environment = "prod" };

        var inherited = await Resolve("secret://memowikis/stage/MAIL_KEY");
        Assert.Equal(workspace, inherited.Value);
        Assert.Equal("secret://workspace/MAIL_KEY@1", inherited.Resolved.ToString());
        var overridden = await _provider.GetForWorkloadAsync(SecretReference.Parse("secret://memowikis/prod/MAIL_KEY"), prodDeploy, "deploy:prod");
        Assert.Equal(prod, overridden.Value);
        Assert.Equal("secret://memowikis/prod/MAIL_KEY@1", overridden.Resolved.ToString());
    }

    [Fact]
    public async Task Resolution_outside_the_workload_scope_is_denied_and_audited()
    {
        await _provider.PutAsync(SecretScope.ForEnvironment("memowikis", "prod"), "DATABASE_URL", NewValue(), Owner);
        await _provider.PutAsync(SecretScope.ForProject("other"), "DATABASE_URL", NewValue(), Owner);

        Assert.Equal("environment-scope-denied",
            (await Assert.ThrowsAsync<SecretAccessDeniedException>(() => Resolve("secret://memowikis/prod/DATABASE_URL"))).Code);
        Assert.Equal("project-scope-denied",
            (await Assert.ThrowsAsync<SecretAccessDeniedException>(() => Resolve("secret://other/DATABASE_URL"))).Code);
        Assert.Equal("reference-not-declared",
            (await Assert.ThrowsAsync<SecretAccessDeniedException>(() => Resolve("secret://memowikis/stage/STRIPE_KEY"))).Code);

        var outcomes = _audit.Read(null, 10).Where(entry => entry.Action == SecretAuditActions.Resolve).Select(entry => entry.Outcome).ToList();
        Assert.Equal(["denied:reference-not-declared", "denied:project-scope-denied", "denied:environment-scope-denied"], outcomes);
    }

    [Fact]
    public async Task Workloads_cannot_write_and_people_cannot_resolve()
    {
        Assert.Equal("human-required", (await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _provider.PutAsync(SecretScope.Workspace, "MAIL_KEY", NewValue(), SecretActor.Workload("stage-host")))).Code);
        Assert.Equal("human-required", (await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _provider.RevokeAsync(SecretScope.Workspace, "MAIL_KEY", null, SecretActor.Workload("stage-host")))).Code);
        Assert.DoesNotContain(typeof(ISecretProvider).GetMethods(), method =>
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(SecretActor))
            && method.ReturnType.GenericTypeArguments.Any(type => type == typeof(ResolvedSecret)));
    }

    [Fact]
    public async Task Every_operation_is_audited_with_actor_scope_reference_purpose_and_outcome_but_no_value()
    {
        var scope = SecretScope.ForEnvironment("memowikis", "stage");
        var (first, second) = (NewValue(), NewValue());
        await _provider.PutAsync(scope, "DATABASE_URL", first, Owner);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _provider.RotateAsync(scope, "DATABASE_URL", second, Owner);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Resolve("secret://memowikis/stage/DATABASE_URL");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _provider.RevokeAsync(scope, "DATABASE_URL", 1, Owner);
        await Assert.ThrowsAsync<SecretNotFoundException>(() => _provider.RotateAsync(scope, "MISSING", NewValue(), Owner));

        var entries = _audit.Read(null, 50).Reverse().ToList();
        Assert.Equal(["put", "rotate", "resolve", "revoke", "rotate"], entries.Select(entry => entry.Action));
        Assert.Equal(["ok", "ok", "ok", "ok", "failed:secret-not-found"], entries.Select(entry => entry.Outcome));
        Assert.Equal([1, 2, 2, 1, (int?)null], entries.Select(entry => entry.Version));
        var resolve = entries[2];
        Assert.Equal("workload:stage-host", resolve.Actor);
        Assert.Equal("memowikis/stage", resolve.Scope);
        Assert.Equal("secret://memowikis/stage/DATABASE_URL", resolve.Reference);
        Assert.Equal("deploy:stage", resolve.Purpose);
        Assert.Equal("deploy-job:memowikis/stage", resolve.Workload);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(-1), resolve.At);
        Assert.Equal("human:first.owner", entries[0].Actor);

        var raw = string.Join("\n", Directory.EnumerateFiles(Path.Combine(_root, "workspace"), "*.jsonl", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain(first, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(second, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(first, JsonSerializer.Serialize(await _provider.ListAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolved_values_are_scrubbed_from_workload_output_verbatim_by_line_and_in_base64()
    {
        var pem = "line-one-" + Guid.NewGuid().ToString("N") + "\nline-two-" + Guid.NewGuid().ToString("N");
        await _provider.PutAsync(SecretScope.ForEnvironment("memowikis", "stage"), "DATABASE_URL", pem, Owner);
        Assert.Equal(pem, Assert.IsType<string>(_scrubber.Scrub(pem)));

        await Resolve("secret://memowikis/stage/DATABASE_URL");
        var lines = pem.Split('\n');
        var output = $"connecting with {pem}; second {lines[1]}; encoded {Convert.ToBase64String(Encoding.UTF8.GetBytes(pem))}";
        var scrubbed = _scrubber.Scrub(output);

        Assert.DoesNotContain(lines[0], scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(lines[1], scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)), scrubbed, StringComparison.Ordinal);
        Assert.Contains("[secret:secret://memowikis/stage/DATABASE_URL]", scrubbed, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(output, _scrubber.Scrub(output));
    }

    [Fact]
    public async Task Values_are_validated_before_anything_is_stored()
    {
        Assert.Equal("value-required", (await Assert.ThrowsAsync<SecretValidationException>(() =>
            _provider.PutAsync(SecretScope.Workspace, "MAIL_KEY", "", Owner))).Code);
        Assert.Equal("value-too-large", (await Assert.ThrowsAsync<SecretValidationException>(() =>
            _provider.PutAsync(SecretScope.Workspace, "MAIL_KEY", new string('x', SecretProvider.MaximumValueBytes + 1), Owner))).Code);
        Assert.Equal("invalid-name", (await Assert.ThrowsAsync<SecretValidationException>(() =>
            _provider.PutAsync(SecretScope.Workspace, "../escape", NewValue(), Owner))).Code);
        Assert.Empty(await _provider.ListAsync());
    }

    private Task<ResolvedSecret> Resolve(string reference)
        => _provider.GetForWorkloadAsync(SecretReference.Parse(reference), _stageDeploy, "deploy:stage");
}
