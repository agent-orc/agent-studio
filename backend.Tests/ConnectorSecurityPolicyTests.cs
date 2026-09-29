using AgentStudio.Connector;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Direct matrix tests for the connector's pure security and attach policies.</summary>
public sealed class ConnectorSecurityPolicyTests
{
    private static readonly ConnectorRequestFacts ValidMutation = new(
        HostAllowed: true,
        Origin: ConnectorOriginFact.Studio,
        SameOriginFetch: true,
        Surface: ConnectorSurface.Protected,
        UnsafeMethod: true,
        WebSocketUpgrade: false,
        HubNegotiate: false,
        SessionValid: true,
        CsrfValid: true);

    public static TheoryData<string, ConnectorRequestFacts, int?, string?, bool> RequestMatrix => new()
    {
        { "valid mutation", ValidMutation, null, null, false },
        { "rebinding host", ValidMutation with { HostAllowed = false }, 400, ConnectorRequestPolicy.HostRejected, false },
        { "cross-origin mutation", ValidMutation with { Origin = ConnectorOriginFact.Foreign }, 403, ConnectorRequestPolicy.OriginRejected, false },
        { "cross-origin read", ValidMutation with { Origin = ConnectorOriginFact.Foreign, UnsafeMethod = false }, 403, ConnectorRequestPolicy.OriginRejected, false },
        { "missing-origin mutation", ValidMutation with { Origin = ConnectorOriginFact.Absent }, 403, ConnectorRequestPolicy.OriginRequired, false },
        { "missing-origin websocket", ValidMutation with { Origin = ConnectorOriginFact.Absent, UnsafeMethod = false, WebSocketUpgrade = true }, 403, ConnectorRequestPolicy.OriginRequired, false },
        { "missing csrf", ValidMutation with { CsrfValid = false }, 403, ConnectorRequestPolicy.CsrfRejected, false },
        { "mutation without session", ValidMutation with { SessionValid = false, CsrfValid = false }, 401, ConnectorRequestPolicy.SessionRequired, false },
        { "hub negotiate without csrf", ValidMutation with { HubNegotiate = true, CsrfValid = false }, null, null, false },
        { "hub negotiate without session", ValidMutation with { HubNegotiate = true, SessionValid = false, CsrfValid = false }, 401, ConnectorRequestPolicy.SessionRequired, false },
        { "same-origin read without session", ValidMutation with { Origin = ConnectorOriginFact.Absent, UnsafeMethod = false, SessionValid = false, CsrfValid = false }, null, null, true },
        { "cross-site read without session", ValidMutation with { Origin = ConnectorOriginFact.Absent, UnsafeMethod = false, SameOriginFetch = false, SessionValid = false, CsrfValid = false }, 401, ConnectorRequestPolicy.SessionRequired, false },
        { "websocket without session", ValidMutation with { UnsafeMethod = false, WebSocketUpgrade = true, SessionValid = false, CsrfValid = false }, 401, ConnectorRequestPolicy.SessionRequired, false },
        { "read with session", ValidMutation with { Origin = ConnectorOriginFact.Absent, UnsafeMethod = false, CsrfValid = false }, null, null, false },
        { "bootstrap with studio origin", ValidMutation with { Surface = ConnectorSurface.SessionBootstrap, SessionValid = false, CsrfValid = false }, null, null, false },
        { "bootstrap same-origin get", ValidMutation with { Surface = ConnectorSurface.SessionBootstrap, Origin = ConnectorOriginFact.Absent, UnsafeMethod = false, SessionValid = false }, null, null, false },
        { "bootstrap get without proof", ValidMutation with { Surface = ConnectorSurface.SessionBootstrap, Origin = ConnectorOriginFact.Absent, UnsafeMethod = false, SameOriginFetch = false }, 403, ConnectorRequestPolicy.OriginRequired, false },
        { "bootstrap post without origin", ValidMutation with { Surface = ConnectorSurface.SessionBootstrap, Origin = ConnectorOriginFact.Absent }, 403, ConnectorRequestPolicy.OriginRequired, false },
        { "logout without csrf", ValidMutation with { Surface = ConnectorSurface.SessionControl, CsrfValid = false }, 403, ConnectorRequestPolicy.CsrfRejected, false },
        { "liveness without session", ValidMutation with { Surface = ConnectorSurface.Liveness, Origin = ConnectorOriginFact.Absent, UnsafeMethod = false, SessionValid = false }, null, null, false },
    };

    [Theory]
    [MemberData(nameof(RequestMatrix))]
    public void Request_admission_matrix(
        string scenario,
        ConnectorRequestFacts facts,
        int? expectedStatus,
        string? expectedCode,
        bool expectedIssueSession)
    {
        var admission = ConnectorRequestPolicy.Decide(facts);

        Assert.True(expectedStatus == admission.RejectStatus, $"{scenario}: status {admission.RejectStatus}");
        Assert.Equal(expectedCode, admission.RejectCode);
        Assert.Equal(expectedIssueSession, admission.IssueSession);
    }

    private static readonly ConnectorProtocolRange Range = new(1, 2, 1, 1);

    private static ProtocolAttachResponse Attached(int? api = 2, int? hub = 1, string? path = TaskServerHubProtocol.StudioHubPath)
        => new(true, ProtocolAttachCodes.Attached, RecordingTransport.ServerRange(), api, hub, path, "studio-robert-windows", null);

    private static ProtocolAttachResponse Refused(string code, string reason)
        => new(false, code, RecordingTransport.ServerRange(), null, null, null, "studio-robert-windows", reason);

    public static TheoryData<string, int, ProtocolAttachResponse?, string?> AttachMatrix => new()
    {
        { "attached", 200, Attached(), null },
        { "absent or invalid bearer", 401, null, ConnectorAttachFailureCodes.CredentialRejected },
        { "scope denied", 403, null, ConnectorAttachFailureCodes.CredentialNotStudio },
        { "runner credential", 403, Refused(ProtocolAttachCodes.PrincipalKindMismatch, "runner"), ConnectorAttachFailureCodes.CredentialNotStudio },
        { "old task server", 404, null, ConnectorAttachFailureCodes.AttachUnsupported },
        { "server error", 500, null, ConnectorAttachFailureCodes.HandshakeFailed },
        { "api mismatch", 426, Refused(ProtocolAttachCodes.ApiProtocolIncompatible, "api"), ConnectorAttachFailureCodes.ProtocolIncompatible },
        { "hub mismatch", 426, Refused(ProtocolAttachCodes.HubProtocolIncompatible, "hub"), ConnectorAttachFailureCodes.HubProtocolIncompatible },
        { "unknown refusal", 400, Refused("something-new", "new"), ConnectorAttachFailureCodes.AttachRefused },
        { "negotiated api above offer", 200, Attached(api: 3), ConnectorAttachFailureCodes.ProtocolIncompatible },
        { "no negotiated api", 200, Attached(api: null), ConnectorAttachFailureCodes.ProtocolIncompatible },
        { "negotiated hub above offer", 200, Attached(hub: 2), ConnectorAttachFailureCodes.HubProtocolIncompatible },
        { "hub path moved", 200, Attached(path: "/hubs/v2/studio"), ConnectorAttachFailureCodes.HubPathMismatch },
        { "api error body", 426, new ProtocolAttachResponse(false, "protocol-unsupported", null!, null, null, null, null, null), ConnectorAttachFailureCodes.HandshakeFailed },
    };

    [Theory]
    [MemberData(nameof(AttachMatrix))]
    public void Attach_decision_matrix(string scenario, int status, ProtocolAttachResponse? response, string? expectedFailure)
    {
        var decision = ConnectorAttachPolicy.Evaluate(Range, status, response, TaskServerHubProtocol.StudioHubPath);

        Assert.True(expectedFailure == decision.FailureCode, $"{scenario}: {decision.FailureCode}");
        Assert.Equal(expectedFailure is null, decision.Attached);
        if (expectedFailure is null)
        {
            Assert.Equal(2, decision.ApiProtocol);
            Assert.Equal(1, decision.HubProtocol);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(decision.FailureReason));
        }
    }

    [Theory]
    [InlineData("http://localhost:4011", "http://localhost:4011")]
    [InlineData("HTTP://LocalHost:4011/", "http://localhost:4011")]
    [InlineData("http://[::1]:4011", "http://[::1]:4011")]
    [InlineData("http://127.0.0.1:4200", "http://127.0.0.1:4200")]
    [InlineData("https://localhost", "https://localhost")]
    public void Loopback_studio_origins_normalize_to_browser_serialization(string configured, string expected)
        => Assert.Equal(expected, ConnectorOptions.NormalizeLoopbackOrigin(configured));

    [Theory]
    [InlineData("http://studio.example:4011")]
    [InlineData("http://192.168.1.10:4011")]
    [InlineData("http://[::]:4011")]
    [InlineData("http://0.0.0.0:4011")]
    [InlineData("http://localhost:4011/app")]
    [InlineData("http://user@localhost:4011")]
    [InlineData("ws://localhost:4011")]
    [InlineData("localhost:4011")]
    public void Non_loopback_or_non_origin_values_are_refused(string configured)
        => Assert.Throws<InvalidOperationException>(() => ConnectorOptions.NormalizeLoopbackOrigin(configured));

    [Fact]
    public void Default_origins_are_localhost_and_bracketed_loopback_on_port_4011()
    {
        var options = ConnectorOptions.Load(Configuration());

        Assert.Equal(["http://localhost:4011", "http://[::1]:4011"], options.StudioOrigins);
        Assert.True(options.IsStudioOrigin("http://localhost:4011"));
        Assert.False(options.IsStudioOrigin("http://127.0.0.1:4011"));
        Assert.False(options.IsStudioOrigin(null));
    }

    [Theory]
    [InlineData("Connector:BearerToken")]
    [InlineData("Connector:Upstream:Token")]
    [InlineData("TaskServer:AuthToken")]
    [InlineData("TaskServer:AuthTokenFile")]
    [InlineData("STUDIO_AUTH_TOKEN")]
    public void Credential_values_in_configuration_are_refused(string key)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectorOptions.Load(Configuration(new Dictionary<string, string?> { [key] = "leaked-secret" })));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Credential_provider_rereads_after_the_refresh_interval_and_after_invalidation()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T08:00:00Z"));
        var source = new MutableCredentialSource("v1");
        var provider = new ConnectorCredentialProvider(
            ConnectorProfileTests.TestOptions() with { CredentialRefreshInterval = TimeSpan.FromSeconds(5) },
            source,
            time);

        Assert.Equal("v1", provider.Current().Credential!.Bearer);
        var firstRevision = provider.Revision;
        source.Rotate("v2");
        Assert.Equal("v1", provider.Current().Credential!.Bearer);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("v2", provider.Current().Credential!.Bearer);
        Assert.Equal(firstRevision + 1, provider.Revision);

        source.Rotate("v3");
        provider.Invalidate();
        Assert.Equal("v3", provider.Current().Credential!.Bearer);
        Assert.Equal(firstRevision + 2, provider.Revision);

        Assert.Equal(3, source.Loads);
        Assert.DoesNotContain("v3", provider.Current().Credential!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_credential_file_must_be_owner_only_and_is_rotated_in_place()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("connector-credential-");
        var path = Path.Combine(directory.FullName, "studio-connector.credential");
        try
        {
            File.WriteAllText(path, "linux-studio-secret\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal("linux-studio-secret", ConnectorCredentialSource.ReadCredentialFile(path));

            File.WriteAllText(path, "rotated-studio-secret");
            Assert.Equal("rotated-studio-secret", ConnectorCredentialSource.ReadCredentialFile(path));

            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            Assert.Equal("rotated-studio-secret", ConnectorCredentialSource.ReadCredentialFile(path));

            foreach (var loose in new[] { UnixFileMode.GroupRead, UnixFileMode.OtherRead, UnixFileMode.UserExecute })
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | loose);
                var exception = Assert.Throws<ConnectorCredentialException>(() => ConnectorCredentialSource.ReadCredentialFile(path));
                Assert.Equal(ConnectorCredentialFailureCodes.InsecureFile, exception.Code);
                Assert.DoesNotContain("rotated-studio-secret", exception.Message, StringComparison.Ordinal);
            }

            var link = Path.Combine(directory.FullName, "link.credential");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(link, path);
            Assert.Equal(
                ConnectorCredentialFailureCodes.InsecureFile,
                Assert.Throws<ConnectorCredentialException>(() => ConnectorCredentialSource.ReadCredentialFile(link)).Code);

            Assert.Equal(
                ConnectorCredentialFailureCodes.Unavailable,
                Assert.Throws<ConnectorCredentialException>(() =>
                    ConnectorCredentialSource.ReadCredentialFile(Path.Combine(directory.FullName, "missing"))).Code);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [SkippableFact]
    public void Windows_credential_manager_entry_is_read_and_rotated_without_restart()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows Credential Manager is the native Windows credential store.");

        var target = WindowsCredentialManager.NewTestTarget();
        var options = ConnectorProfileTests.TestOptions() with { CredentialTarget = target };
        var source = new ConnectorCredentialSource();
        try
        {
            var missing = source.Load(options);
            Assert.Equal(ConnectorCredentialFailureCodes.Unavailable, missing.FailureCode);
            Assert.Contains("set-studio-credential.ps1", missing.FailureMessage, StringComparison.Ordinal);

            var writable = WindowsCredentialManager.TryWrite(target, "wincred-v1", out var error);
            Skip.IfNot(writable, $"This logon session has no writable Credential Manager vault (Win32 error {error}).");

            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T08:00:00Z"));
            var provider = new ConnectorCredentialProvider(
                options with { CredentialRefreshInterval = TimeSpan.FromSeconds(5) },
                source,
                time);
            var first = provider.CurrentState();
            Assert.Equal("wincred-v1", first.Result.Credential!.Bearer);

            Assert.True(WindowsCredentialManager.TryWrite(target, "wincred-v2", out error), $"Win32 error {error}");
            Assert.Equal("wincred-v1", provider.Current().Credential!.Bearer);
            time.Advance(TimeSpan.FromSeconds(5));
            var rotated = provider.CurrentState();
            Assert.Equal("wincred-v2", rotated.Result.Credential!.Bearer);
            Assert.Equal(first.Revision + 1, rotated.Revision);

            WindowsCredentialManager.Delete(target);
            provider.Invalidate();
            var removed = provider.Current();
            Assert.Null(removed.Credential);
            Assert.Equal(ConnectorCredentialFailureCodes.Unavailable, removed.FailureCode);
            Assert.DoesNotContain("wincred", removed.FailureMessage, StringComparison.Ordinal);
        }
        finally
        {
            WindowsCredentialManager.Delete(target);
        }
    }

    [Fact]
    public void Linux_native_source_reads_the_configured_file()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"connector-native-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "native-secret");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var options = ConnectorProfileTests.TestOptions() with { CredentialFile = path };

            var loaded = new ConnectorCredentialSource().Load(options);
            Assert.Equal("native-secret", loaded.Credential!.Bearer);

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            var insecure = new ConnectorCredentialSource().Load(options);
            Assert.Null(insecure.Credential);
            Assert.Equal(ConnectorCredentialFailureCodes.InsecureFile, insecure.FailureCode);
            Assert.Contains("chmod 600", insecure.FailureMessage, StringComparison.Ordinal);
            // The message reaches the browser in the attach refusal body.
            Assert.DoesNotContain(path, insecure.FailureMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Docker_secret_requires_owner_read_only_permissions()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"connector-secret-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "docker-studio-secret\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            Assert.Equal("docker-studio-secret", ConnectorCredentialSource.ReadDockerSecret(path));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Throws<ConnectorCredentialException>(() => ConnectorCredentialSource.ReadDockerSecret(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IConfiguration Configuration(IDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Connector:Upstream:BaseUrl"] = "https://task-server.invalid",
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>()) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
