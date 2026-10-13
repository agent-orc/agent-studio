using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-3102: the principal rotation contract started to require an operation
/// id and declared consumers, while scripts/compose-rotate-credentials.sh still
/// posted only an overlap. Every Compose rotation failed with 400 and the gap
/// surfaced only in the one-box smoke. These tests post the exact request plan
/// the script renders, so a contract change fails here first.
/// </summary>
public sealed class ComposeCredentialRotationContractTests
{
    private static readonly JsonSerializerOptions StrictWire = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [SkippableTheory]
    [InlineData("studio", "bootstrap-studio")]
    [InlineData("engine", "bootstrap-engine")]
    [InlineData("runner", "runner:distributed-runner")]
    [InlineData("review-runner", "runner:distributed-review-runner")]
    public async Task Compose_rotation_request_plan_issues_delivers_and_retires_the_previous_generation(
        string role, string principalId)
    {
        PlatformGate.RequiresPosixShell();
        var operation = $"compose-{role}-contract-test";
        var plan = await RenderPlanAsync(role, operation);
        var tokens = new Dictionary<string, string>
        {
            ["studio"] = ComposeBootstrapToken(),
            ["engine"] = ComposeBootstrapToken(),
            ["runner"] = ComposeBootstrapToken(),
            ["review-runner"] = ComposeBootstrapToken(),
        };
        using var temp = new TempDirectory();
        await using var factory = new ComposeFactory(temp.Path, tokens);
        using var manager = Client(factory, tokens["studio"], consumer: null);

        var issue = plan.Step("issue");
        Assert.Equal($"/api/v1/management/principals/{principalId}/rotate", issue.Path);
        var request = JsonSerializer.Deserialize<RotatePrincipalRequest>(issue.Body!, StrictWire)!;
        Assert.Equal(operation, request.OperationId);
        Assert.Equal(plan.Consumer, Assert.Single(request.Consumers!).ConsumerId);
        var issued = await SendAsync(manager, issue);
        Assert.True(issued.IsSuccessStatusCode, await issued.Content.ReadAsStringAsync());
        var credential = (await issued.Content.ReadFromJsonAsync<IssuedPrincipalCredential>())!;
        Assert.Equal(principalId, credential.Principal.PrincipalId);
        Assert.Equal(operation, credential.Rotation!.OperationId);
        Assert.StartsWith($"ats_{credential.Rotation.CredentialGeneration}.", credential.Credential);

        using var consumer = Client(factory, credential.Credential!, plan.Consumer);
        foreach (var name in new[] { "deliver", "prove" })
        {
            var response = await SendAsync(consumer, plan.Step(name));
            Assert.True(response.IsSuccessStatusCode, $"{name}: {await response.Content.ReadAsStringAsync()}");
        }
        var acknowledge = plan.Step("acknowledge");
        Assert.Equal(plan.Consumer,
            JsonSerializer.Deserialize<PrincipalRotationAcknowledgement>(acknowledge.Body!, StrictWire)!.ConsumerId);
        var acknowledged = await SendAsync(consumer, acknowledge);
        Assert.True(acknowledged.IsSuccessStatusCode, await acknowledged.Content.ReadAsStringAsync());
        Assert.Equal("retired",
            (await acknowledged.Content.ReadFromJsonAsync<PrincipalRotationReceipt>())!.State);

        using var previous = Client(factory, tokens[role], consumer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await previous.GetAsync("/api/v1/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await consumer.GetAsync("/api/v1/workspaces")).StatusCode);
    }

    [SkippableFact]
    public async Task Compose_rotation_script_rejects_an_operation_id_the_task_server_would_reject()
    {
        PlatformGate.RequiresPosixShell();
        var (exitCode, _) = await RunScriptAsync("--print-requests", "runner", "not a valid id");
        Assert.Equal(64, exitCode);
    }

    private static string ComposeBootstrapToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static async Task<RequestPlan> RenderPlanAsync(string role, string operation)
    {
        var (exitCode, output) = await RunScriptAsync("--print-requests", role, operation);
        Assert.Equal(0, exitCode);
        string? consumer = null;
        var steps = new Dictionary<string, PlannedRequest>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(' ', 4);
            if (parts[0] == "consumer")
                consumer = parts[1];
            else
                steps[parts[0]] = new PlannedRequest(new HttpMethod(parts[1]), parts[2],
                    parts.Length == 4 ? parts[3] : null);
        }
        Assert.NotNull(consumer);
        Assert.Equal(["acknowledge", "deliver", "issue", "prove"], steps.Keys.Order(StringComparer.Ordinal));
        return new RequestPlan(consumer!, steps);
    }

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(params string[] arguments)
    {
        var script = Path.Combine(RepositoryRoot.Find(), "scripts", "compose-rotate-credentials.sh");
        var start = new ProcessStartInfo(PosixShell.RequirePath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(PosixShell.ToShellPath(script));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        _ = await error;
        return (process.ExitCode, await output);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, PlannedRequest planned)
    {
        var request = new HttpRequestMessage(planned.Method, planned.Path);
        if (planned.Method != HttpMethod.Get)
            request.Content = new StringContent(planned.Body ?? string.Empty, Encoding.UTF8, "application/json");
        return client.SendAsync(request);
    }

    private static HttpClient Client(ComposeFactory factory, string credential, string? consumer)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (consumer is not null)
            client.DefaultRequestHeaders.Add("X-Principal-Consumer-Id", consumer);
        return client;
    }

    private sealed record PlannedRequest(HttpMethod Method, string Path, string? Body);

    private sealed record RequestPlan(string Consumer, IReadOnlyDictionary<string, PlannedRequest> Steps)
    {
        public PlannedRequest Step(string name) => Steps[name];
    }

    // Mirrors the bootstrap principals docker-compose.yml configures.
    private sealed class ComposeFactory(string dataDirectory, IReadOnlyDictionary<string, string> tokens)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["AUTH"] = "bearer",
                    ["STUDIO_AUTH_TOKEN"] = tokens["studio"],
                    ["ENGINE_AUTH_TOKEN"] = tokens["engine"],
                    ["BOOTSTRAP_RUNNER_ID"] = "distributed-runner",
                    ["BOOTSTRAP_RUNNER_AUTH_TOKEN"] = tokens["runner"],
                    ["BOOTSTRAP_REVIEW_RUNNER_ID"] = "distributed-review-runner",
                    ["BOOTSTRAP_REVIEW_RUNNER_AUTH_TOKEN"] = tokens["review-runner"],
                }));
        }
    }
}
