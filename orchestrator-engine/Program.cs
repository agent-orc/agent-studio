using AgentStudio.OrchestratorEngine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args is ["--version"])
{
    Console.WriteLine(EngineVersion.Display);
    return 0;
}

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        orchestrator-engine - API-only flow execution service

        Verbs:
          --version       Print release and stamped Git SHA.
          --health-check  Probe the configured Task Server /healthz and exit
                          (0 reachable, 4 unreachable). This is the container
                          HEALTHCHECK; the Engine itself serves no HTTP port.

        Configuration is read from engine.env through the process environment:
          SERVER_URL
          CLIENT_ID
          CLIENT_CREDENTIAL
          ALLOW_INSECURE_HTTP
          REVIEW_CONCURRENCY
          COUNCIL_CONCURRENCY
          POST_PROCESSING_CONCURRENCY
          GATE_DISPATCH_CONCURRENCY
          COMPLETION_JUDGE_CONCURRENCY
          POLL_SECONDS
          LEASE_SECONDS
        """);
    return 0;
}

// The Engine is a headless worker with no listener of its own, so its liveness
// contract is "configuration parses and the control plane answers". Same shape
// and same exit codes as `agent-host --health-check`.
if (args is ["--health-check"])
{
    try
    {
        var probeOptions = EngineOptions.FromEnvironment();
        using var probeClient = new EngineTaskServerClient(probeOptions);
        var reason = await probeClient.ProbeHealthAsync(CancellationToken.None);
        if (reason is null)
        {
            Console.WriteLine($"health-check ok: task server reachable at {probeOptions.ServerUrl}");
            return 0;
        }
        Console.Error.WriteLine(
            $"health-check failed: cannot reach the task server at {probeOptions.ServerUrl} ({reason}).");
        return 4;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine($"orchestrator-engine configuration error: {exception.Message}");
        return 2;
    }
}

try
{
    var options = EngineOptions.FromEnvironment();
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<EngineTaskServerClient>();
    builder.Services.AddSingleton<IOrchestrationStageHandler, ReviewDecisionOrchestratorLoop>();
    builder.Services.AddSingleton<IOrchestrationStageHandler, CouncilLoop>();
    builder.Services.AddSingleton<IOrchestrationStageHandler, PostProcessingLoop>();
    builder.Services.AddSingleton<IOrchestrationStageHandler, GateDispatchLoop>();
    builder.Services.AddSingleton<IOrchestrationStageHandler, CompletionJudgeLoop>();
    builder.Services.AddHostedService<OrchestratorEngineService>();
    await builder.Build().RunAsync();
    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"orchestrator-engine configuration error: {exception.Message}");
    return 2;
}
catch (EngineProtocolException exception)
{
    Console.Error.WriteLine($"orchestrator-engine protocol handshake failed: {exception.Message}");
    return 4;
}
