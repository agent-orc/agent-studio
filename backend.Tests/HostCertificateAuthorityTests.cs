using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Operations.Contracts;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HostCertificateAuthorityTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Break_glass_certificate_expires_at_the_window_end()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "window-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ca = Path.Combine(root, "ca");
            var user = Path.Combine(root, "user");
            Run("-q", "-t", "ed25519", "-N", "", "-f", ca);
            Run("-q", "-t", "ed25519", "-N", "", "-f", user);
            var tokenFile = Path.Combine(root, "token");
            File.WriteAllText(tokenFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostCertificates:CaPrivateKeyFile"] = ca,
                ["HostCertificates:StateDirectory"] = Path.Combine(root, "state"),
                ["Operations:Url"] = "https://operations.example.test",
                ["Operations:ServiceTokenFile"] = tokenFile,
            }).Build();
            var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeTimeProvider(now);
            var end = now.AddMinutes(2);
            var authority = new HostCertificateAuthority(configuration, new ClientFactory(new RevocationReceiver()), clock);
            var receipt = await authority.IssueAsync("sample-host", "operator-agent:sample/session:sample",
                File.ReadAllText(user + ".pub"), default, end);
            Assert.Equal(end, receipt.ExpiresAt);

            var cert = Path.Combine(root, "issued-cert.pub");
            File.WriteAllText(cert, receipt.Certificate);
            var inspection = Run("-L", "-f", cert);
            var validity = Regex.Match(inspection, @"Valid: from (\S+) to (\S+)");
            Assert.True(validity.Success, inspection);
            var actualEnd = DateTimeOffset.Parse(validity.Groups[2].Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal);
            Assert.True(actualEnd <= end, inspection);
            Assert.True(actualEnd > now, inspection);
            Assert.True(actualEnd - DateTimeOffset.Parse(validity.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal) <= TimeSpan.FromMinutes(3), inspection);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Issued_certificate_expires_within_an_hour_and_revocation_is_published_as_a_host_krl()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "host-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ca = Path.Combine(root, "ca");
            var user = Path.Combine(root, "user");
            Run("-q", "-t", "ed25519", "-N", "", "-f", ca);
            Run("-q", "-t", "ed25519", "-N", "", "-f", user);
            var tokenFile = Path.Combine(root, "token");
            File.WriteAllText(tokenFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostCertificates:CaPrivateKeyFile"] = ca,
                ["HostCertificates:StateDirectory"] = Path.Combine(root, "state"),
                ["Operations:Url"] = "http://127.0.0.1:5081",
                ["Operations:ServiceTokenFile"] = tokenFile,
            }).Build();
            var receiver = new RevocationReceiver();
            var authority = new HostCertificateAuthority(configuration, new ClientFactory(receiver),
                new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)));
            var receipt = await authority.IssueAsync("host-a", "operator-agent:agent/session:session",
                File.ReadAllText(user + ".pub"), default);
            var cert = Path.Combine(root, "issued-cert.pub");
            File.WriteAllText(cert, receipt.Certificate);
            var inspection = Run("-L", "-f", cert);
            Assert.Contains("force-command /usr/local/libexec/agent-operations-command", inspection);
            Assert.Contains("agent-operations", inspection);
            var validity = Regex.Match(inspection, @"Valid: from (\S+) to (\S+)");
            Assert.True(validity.Success, inspection);
            Assert.True(DateTime.Parse(validity.Groups[2].Value) - DateTime.Parse(validity.Groups[1].Value)
                <= TimeSpan.FromMinutes(60));
            Assert.True(receipt.ExpiresAt - receipt.IssuedAt <= TimeSpan.FromMinutes(60));
            Assert.True(receipt.Serial > 0);
            Assert.NotNull(receiver.Latest);

            await authority.RevokeAsync("host-a", receipt.Id, default);
            var krl = Path.Combine(root, "revoked.krl");
            File.WriteAllBytes(krl, Convert.FromBase64String(receiver.Latest!.Base64Krl));
            Assert.Equal(2, receiver.Latest.Version);
            Assert.Contains("REVOKED", Run("-Q", "-f", krl, cert));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Run(params string[] args)
    {
        var start = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 || args[0] == "-Q", output);
        return output;
    }

    private sealed class RevocationReceiver : HttpMessageHandler
    {
        public HostRevocationList? Latest { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/operations/v1/certificates/revocations", request.RequestUri!.AbsolutePath);
            Latest = JsonSerializer.Deserialize<HostRevocationList>(await request.Content!.ReadAsStringAsync(ct), OperationsProtocol.Json);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
