using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operations.Agent;
using AgentStudio.Operations.Agent.Features.Execution;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Dispatch;
using Xunit;

namespace OperationsServer.Tests;

/// <summary>
/// AGT-3207: the AI Operator's bounded restart. The caller names one
/// allowlisted service; the agent validates the compose configuration before
/// it recreates exactly that service and never runs anything else.
/// </summary>
public sealed class ServiceRestartTests
{
    [Fact]
    public async Task Allowlisted_service_is_validated_then_recreated_with_a_hidden_console()
    {
        await using var f = await Start();
        var calls = new List<ProcessStartInfo>();
        var agent = Agent(f, calls, exitCodes: [0, 0]);
        await agent.RegisterAsync(default);

        var attempt = await f.Submit(Restart(f, "review-daemon"));
        await agent.PollOnceAsync(default);

        var completed = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + $"attempts/{attempt.Id}");
        Assert.Equal("succeeded", completed!.State);
        Assert.True(completed.Result!.Output.GetProperty("restarted").GetBoolean());
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.True(call.CreateNoWindow && !call.UseShellExecute));
        Assert.Equal(["compose", "-f", f.ComposeFile, "config", "--quiet"], calls[0].ArgumentList);
        Assert.Equal(["compose", "-f", f.ComposeFile, "up", "-d", "--force-recreate", "--no-deps", "review"], calls[1].ArgumentList);
    }

    [Fact]
    public async Task Accepted_restart_can_be_found_by_its_idempotency_key_after_a_lost_response()
    {
        await using var f = await Start();
        var agent = Agent(f, [], exitCodes: [0, 0]);
        await agent.RegisterAsync(default);
        var command = Restart(f, "review-daemon");
        var accepted = await f.Submit(command);

        var recovered = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api
            + $"attempts/by-key/{command.IdempotencyKey}");
        Assert.Equal(accepted.Id, recovered!.Id);
        Assert.Equal("queued", recovered.State);
        await agent.PollOnceAsync(default);
        var terminal = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api
            + $"attempts/by-key/{command.IdempotencyKey}");
        Assert.Equal("succeeded", terminal!.State);
    }

    [Fact]
    public async Task Invalid_configuration_fails_without_touching_the_container()
    {
        await using var f = await Start();
        var calls = new List<ProcessStartInfo>();
        var agent = Agent(f, calls, exitCodes: [1]);
        await agent.RegisterAsync(default);

        var attempt = await f.Submit(Restart(f, "review-daemon"));
        await agent.PollOnceAsync(default);

        var completed = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + $"attempts/{attempt.Id}");
        Assert.Equal(("failed", "configuration-invalid"), (completed!.State, completed.Result!.ExitClassification));
        Assert.False(completed.Result.Output.GetProperty("configurationValid").GetBoolean());
        Assert.Single(calls);
    }

    [Fact]
    public async Task Restart_is_not_repeated_when_result_spooling_fails_after_recreation()
    {
        await using var f = await Start();
        var spool = Path.Combine(f.Directory, "spool");
        var displaced = spool + "-displaced";
        var calls = 0;
        var services = new RestartableServices(new Dictionary<string, RestartableService>
        {
            ["review-daemon"] = new(f.ComposeFile, "review"),
        });
        var agent = new OperationsAgentClient(f.Agent, "host-a", "boot-a", spool, f.Clock, services,
            (start, _, _) =>
            {
                calls++;
                if (calls == 2)
                {
                    Directory.Move(spool, displaced);
                    File.WriteAllText(spool, "result spool unavailable");
                }
                return Task.FromResult(new ToolRun(0, string.Empty, false));
            });
        await agent.RegisterAsync(default);
        var attempt = await f.Submit(Restart(f, "review-daemon"));

        await Assert.ThrowsAnyAsync<IOException>(() => agent.PollOnceAsync(default));
        File.Delete(spool);
        Directory.Move(displaced, spool);
        Assert.True(File.Exists(Path.Combine(spool, attempt.Id + ".restart-started")));

        await agent.PollOnceAsync(default);

        Assert.Equal(2, calls);
        var completed = await f.Service.GetFromJsonAsync<OperationAttempt>(Fixture.Api + $"attempts/{attempt.Id}");
        Assert.Equal(("failed", "restart-outcome-unknown"),
            (completed!.State, completed.Result!.ExitClassification));
    }

    [Fact]
    public async Task Agent_without_an_allowlist_offers_no_restart()
    {
        await using var f = await Start();
        var agent = new OperationsAgentClient(f.Agent, "host-a", "boot-a", Path.Combine(f.Directory, "spool"), f.Clock);
        await agent.RegisterAsync(default);

        var response = await f.Service.PostAsJsonAsync(Fixture.Api + "commands", Restart(f, "review-daemon"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("agent-unavailable", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{\"service\":\"review-daemon\"}", null)]
    [InlineData("{\"service\":\"Review Daemon\"}", "invalid-input")]
    [InlineData("{\"service\":\"review-daemon\",\"path\":\"/etc\"}", "invalid-input")]
    [InlineData("{\"command\":\"rm -rf /\"}", "invalid-input")]
    [InlineData("{}", "invalid-input")]
    public void Admission_accepts_only_one_allowlistable_service_name(string input, string? expected)
    {
        var principal = Fixture.Edge with { Scopes = [.. Fixture.Edge.Scopes, "service.restart"] };
        var element = JsonDocument.Parse(input).RootElement.Clone();
        var command = Fixture.NewCommand(new TestClock().GetUtcNow()) with
        {
            OperationId = "service.restart", Input = element, InputDigest = OperationsProtocol.Digest(element),
        };

        Assert.Equal(expected, OperationPolicy.Admission(principal, command, new TestClock().GetUtcNow()));
    }

    [Fact]
    public void Allowlist_rejects_relative_paths_and_invalid_names()
    {
        var directory = Path.Combine(Path.GetTempPath(), "restart-allowlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var compose = Path.Combine(directory, "compose.yaml");
            File.WriteAllText(compose, "services: {}");
            string Write(object document)
            {
                var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(document));
                return path;
            }

            var valid = RestartableServices.Load(Write(new { services = new Dictionary<string, object>
            {
                ["review-daemon"] = new { composeFile = compose, composeService = "review" },
            } }));
            Assert.Equal("review", valid.Services["review-daemon"].ComposeService);
            // An invalid allowlist offers no restart and is reported; it never stops the agent.
            var warnings = new List<string>();
            Assert.Empty(RestartableServices.Load(Write(new { services = new Dictionary<string, object>
            {
                ["review-daemon"] = new { composeFile = "compose.yaml", composeService = "review" },
            } }), warnings.Add).Services);
            Assert.Empty(RestartableServices.Load(Write(new { services = new Dictionary<string, object>
            {
                ["review daemon; rm"] = new { composeFile = compose, composeService = "review" },
            } }), warnings.Add).Services);
            var torn = Path.Combine(directory, "torn.json");
            File.WriteAllText(torn, "{ \"services\": ");
            Assert.Empty(RestartableServices.Load(torn, warnings.Add).Services);
            Assert.Empty(RestartableServices.Load(Path.Combine(directory, "missing.json"), warnings.Add).Services);
            Assert.Empty(RestartableServices.Load(Write(new { services = new Dictionary<string, object>
            {
                ["review-daemon"] = new { composeFile = (string?)null, composeService = "review" },
            } }), warnings.Add).Services);
            Assert.Equal(5, warnings.Count);
            Assert.All(warnings, warning => Assert.Contains("offers no restarts", warning));
            Assert.Empty(RestartableServices.Load(null).Services);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Outer_cancellation_kills_the_running_tool_instead_of_leaving_it_behind()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping" : "sleep")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in OperatingSystem.IsWindows() ? ["-n", "60", "127.0.0.1"] : new[] { "60" })
            start.ArgumentList.Add(argument);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ServiceRestart.RunAsync(start, TimeSpan.FromMinutes(5), stop.Token));

        // Without the kill the runner would wait for the 60 s tool to exit.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"the tool kept running for {elapsed.Elapsed}");
    }

    [Fact]
    public async Task Process_output_is_bounded_while_both_pipes_continue_to_drain()
    {
        if (OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo("sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("head -c 131072 /dev/zero | tr '\\0' x; head -c 131072 /dev/zero | tr '\\0' y >&2");

        var result = await ServiceRestart.RunAsync(start, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(2048, result.Output.Length);
        Assert.All(result.Output, letter => Assert.Equal('x', letter));
    }

    private static async Task<Fixture> Start()
    {
        var f = await Fixture.Start();
        f.SavePrincipals(f.Principals.Select(principal => principal.Id is "edge" or "agent"
            ? principal with { Scopes = [.. principal.Scopes, "service.restart"] } : principal).ToArray());
        File.WriteAllText(f.ComposeFile, "services: {}");
        return f;
    }

    private static OperationsAgentClient Agent(Fixture f, List<ProcessStartInfo> calls, int[] exitCodes)
    {
        var services = new RestartableServices(new Dictionary<string, RestartableService>
        {
            ["review-daemon"] = new(f.ComposeFile, "review"),
        });
        return new OperationsAgentClient(f.Agent, "host-a", "boot-a", Path.Combine(f.Directory, "spool"), f.Clock,
            services, (start, _, _) =>
            {
                calls.Add(start);
                return Task.FromResult(new ToolRun(exitCodes[calls.Count - 1], string.Empty, false));
            });
    }

    private static OperationCommand Restart(Fixture f, string service)
    {
        var input = JsonSerializer.SerializeToElement(new { service });
        return f.Command("restart-" + service) with
        {
            OperationId = "service.restart", Input = input, InputDigest = OperationsProtocol.Digest(input),
        };
    }
}
