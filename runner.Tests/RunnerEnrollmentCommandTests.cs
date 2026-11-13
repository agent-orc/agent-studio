using System.Net;
using System.Text;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerEnrollmentCommandTests
{
    [Fact]
    public async Task Enrollment_saves_enrolled_identity_and_secret_with_private_permissions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-enrollment-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new FakeEnrollmentHandler()) { BaseAddress = new Uri("https://studio.example") };
            using var output = new StringWriter();
            using var error = new StringWriter();
            var result = await RunnerEnrollmentCommand.RunAsync(
                "https://studio.example", "one-time-code", output, error,
                CancellationToken.None, http, directory);
            Assert.Equal(0, result);
            Assert.Equal("", error.ToString());
            Assert.Contains("RUNNER_ID=runner_enrolled", File.ReadAllText(Path.Combine(directory, "installation.env")));
            Assert.Contains("RUNNER_NAME=\"agent-runner-01\"", File.ReadAllText(Path.Combine(directory, "installation.env")));
            Assert.Equal("generated-secret", File.ReadAllText(Path.Combine(directory, "runner.token")).Trim());
            Assert.DoesNotContain("generated-secret", output.ToString());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(directory, "runner.token")));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(directory, "installation.env")));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Enrollment_refuses_cleartext_without_sending_the_code()
    {
        using var handler = new FakeEnrollmentHandler();
        using var http = new HttpClient(handler);
        var result = await RunnerEnrollmentCommand.RunAsync(
            "http://studio.example", "one-time-code", TextWriter.Null, TextWriter.Null,
            CancellationToken.None, http, Path.GetTempPath());
        Assert.Equal(2, result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Enrollment_names_the_server_refusal_code()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-enrollment-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new RefusalHandler());
            using var error = new StringWriter();
            var result = await RunnerEnrollmentCommand.RunAsync(
                "https://studio.example", "one-time-code", TextWriter.Null, error,
                CancellationToken.None, http, directory);
            Assert.Equal(4, result);
            Assert.Contains("HTTP 401, invalid-enrollment", error.ToString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class RefusalHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid-enrollment\"}", Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeEnrollmentHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://studio.example/api/auth/runner-enroll", request.RequestUri!.ToString());
            Assert.Contains("one-time-code", await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"runnerId\":\"runner_enrolled\",\"runnerName\":\"agent-runner-01\",\"secret\":\"generated-secret\"}",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
