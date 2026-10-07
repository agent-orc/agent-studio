using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RepositoryRenewalCommandTests
{
    [Fact]
    public void Workstation_administration_allowlist_is_exact()
    {
        Assert.True(RepositoryRenewalCommand.WorkstationRepositoryAllowed(
            "example/repo, example/workspace", "example", "workspace"));
        Assert.False(RepositoryRenewalCommand.WorkstationRepositoryAllowed(
            "example/workspace-extra", "example", "workspace"));
        Assert.False(RepositoryRenewalCommand.WorkstationRepositoryAllowed(
            "other/workspace", "example", "workspace"));
    }

    [Fact]
    public async Task Discovery_command_reports_grants_and_unknown_expiry_without_bearer()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "renewal-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var request = new GitHubDiscoveryRequest("credential-fixture", "workspace",
                "https://github.com/example/private-workspace.git", false, null, null);
            var path = Path.Combine(root, "request.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));
            var output = new StringWriter();

            var code = await RepositoryRenewalCommand.RunAsync("discover-https", path, false,
                new StringReader("redacted-fixture-bearer"), output, CancellationToken.None,
                Path.Combine(root, "secrets"), discoveryHandler: new DiscoveryHandler());

            Assert.Equal(0, code);
            Assert.Contains("\"RepositoryPurpose\":\"workspace\"", output.ToString());
            Assert.Contains("\"FetchGrant\":true", output.ToString());
            Assert.Contains("\"PushGrant\":false", output.ToString());
            Assert.Contains("\"ExpiryKnowledge\":\"unknown\"", output.ToString());
            Assert.DoesNotContain("redacted-fixture-bearer", output.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.github.com/repos/example/private-workspace",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal("redacted-fixture-bearer", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"permissions\":{\"pull\":true,\"push\":false}}")
            });
        }
    }

    [Fact]
    public async Task Workstation_admin_transport_sends_public_material_only()
    {
        string? sent = null;
        var admin = new WorkstationGitHubAdministration("provisioning-workstation",
            (json, _) =>
            {
                sent = json;
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(
                    new WorkstationAdminReply("ok", new RegisteredRepositoryKey(42,
                        "SHA256:fixture", false))), ""));
            });
        var request = new RepositoryRotationRequest("op", "host", "old", "example", "repo",
            "product", "git@github.com:example/repo.git", true, 1,
            "provisioning-record", "/host/private/deploy-key");

        var key = await admin.RegisterAsync(request,
            new RepositoryKeyCandidate("new", "ssh-ed25519 PUBLIC", "SHA256:fixture",
                "/host/private/new-key"), CancellationToken.None);

        Assert.Equal(42, key?.GitHubKeyId);
        Assert.Contains("ssh-ed25519 PUBLIC", sent);
        Assert.DoesNotContain("/host/private", sent);
        Assert.DoesNotContain("provisioning-record", sent);
    }

    [Fact]
    public async Task Workstation_admin_denial_remains_a_guided_authority_result()
    {
        var admin = new WorkstationGitHubAdministration("provisioning-workstation",
            (_, _) => Task.FromResult(new ProcessResult(3,
                JsonSerializer.Serialize(new WorkstationAdminReply("authority-required", null)), "")));
        var request = new RepositoryRotationRequest("op", "host", "old", "example", "repo",
            "product", "git@github.com:example/repo.git", false, null,
            "provisioning-record");

        await Assert.ThrowsAsync<RepositoryAdminAuthorityException>(() =>
            admin.FindByFingerprintAsync(request, "SHA256:fixture", CancellationToken.None));
    }

    [Fact]
    public async Task Unavailable_workstation_session_is_guided_without_key_registration()
    {
        var admin = new WorkstationGitHubAdministration("provisioning-workstation",
            (_, _) => Task.FromResult(new ProcessResult(255, "", "redacted transport error")));
        var request = new RepositoryRotationRequest("op", "host", "old", "example", "repo",
            "product", "git@github.com:example/repo.git", false, null,
            "provisioning-record");

        await Assert.ThrowsAsync<RepositoryAdminAuthorityException>(() =>
            admin.FindByFingerprintAsync(request, "SHA256:fixture", CancellationToken.None));
    }

    [Fact]
    public async Task Workstation_admin_session_registers_public_key_without_returning_bearer()
    {
        var publicKey = "ssh-ed25519 " + Convert.ToBase64String(new byte[32]) + " fixture";
        var fingerprint = GitHubDeployKeyAdministration.Fingerprint(publicKey);
        var handler = new AdminHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "redacted-provisioning-bearer");
        var input = new StringReader(JsonSerializer.Serialize(new WorkstationAdminRequest(
            "register", "host", "example", "repo", "op", true, fingerprint, publicKey, null)));
        var output = new StringWriter();

        var code = await RepositoryRenewalCommand.RunAdminAsync(input, output,
            CancellationToken.None, _ => Task.FromResult<HttpClient?>(client));

        Assert.Equal(0, code);
        Assert.Equal("https://api.github.com/repos/example/repo/keys", handler.Url);
        Assert.Contains(publicKey, handler.Body);
        Assert.Contains("\"GitHubKeyId\":42", output.ToString());
        Assert.DoesNotContain("redacted-provisioning-bearer", output.ToString());
    }

    [Fact]
    public async Task GitHub_deletion_denial_is_a_guided_authority_failure()
    {
        using var client = new HttpClient(new DeniedHandler()) {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var administration = new GitHubDeployKeyAdministration(client);
        var request = new RepositoryRotationRequest("op", "host", "old", "example", "repo",
            "product", "git@github.com:example/repo.git", true, 12,
            "provisioning-record");

        await Assert.ThrowsAsync<RepositoryAdminAuthorityException>(() =>
            administration.DeleteAsync(request, 12, CancellationToken.None));
    }

    private sealed class DeniedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    private sealed class AdminHandler : HttpMessageHandler
    {
        public string? Url;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created) {
                Content = new StringContent("{\"id\":42,\"read_only\":false}")
            };
        }
    }

    [Fact]
    public async Task Https_command_requires_protected_consent_before_staging()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "renewal-command-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var request = new RepositoryHttpsRotationRequest("op", "old",
                new("github-token", "unknown", "workspace",
                    "https://github.com/example/private-workspace.git", false,
                    true, null, null, "unknown"), "old-token");
            var path = Path.Combine(root, "request.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));
            var output = new StringWriter();

            var code = await RepositoryRenewalCommand.RunAsync("https", path, false,
                new StringReader(""), output, CancellationToken.None, Path.Combine(root, "secrets"));

            Assert.Equal(3, code);
            Assert.Contains("guided-consent-required", output.ToString());
            Assert.DoesNotContain("Bearer", output.ToString());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "secrets"), "*.credentials"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Deploy_key_command_requires_workstation_administration_session()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "renewal-admin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var request = new RepositoryRotationRequest("op", "host", "old", "example",
                "private-workspace", "workspace", "git@github.com:example/private-workspace.git",
                false, 12, "provisioning-record");
            var path = Path.Combine(root, "request.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));
            var output = new StringWriter();

            var code = await RepositoryRenewalCommand.RunAsync("deploy-key", path, true,
                new StringReader(""), output, CancellationToken.None, Path.Combine(root, "secrets"),
                _ => Task.FromResult<HttpClient?>(null));

            Assert.Equal(3, code);
            Assert.Contains("administrator-action-required", output.ToString());
            Assert.False(Directory.Exists(Path.Combine(root, "secrets")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
