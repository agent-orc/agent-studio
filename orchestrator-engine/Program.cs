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

        Configuration is read from engine.env through the process environment:
          SERVER_URL
          CLIENT_ID
          CLIENT_CREDENTIAL
          REVIEW_CONCURRENCY
          COUNCIL_CONCURRENCY
          POST_PROCESSING_CONCURRENCY
          GATE_DISPATCH_CONCURRENCY
          COMPLETION_JUDGE_CONCURRENCY
          POLL_SECONDS
          LEASE_SECONDS
          HEALTH_PORT

        --health-check   Connect to the loopback health port and exit
                          (0 live, 4 unreachable). No Task Server call.
        """);
    return 0;
}

if (args is ["--health-check"])
{
    var port = EngineHealthProbe.ResolvePort(Environment.GetEnvironmentVariable);
    var healthy = await EngineHealthProbe.IsReachableAsync(port, TimeSpan.FromSeconds(3));
    if (!healthy)
        Console.Error.WriteLine($"orchestrator-engine health port unreachable: 127.0.0.1:{port}");
    return healthy ? 0 : 4;
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
    builder.Services.AddHostedService<EngineHealthServer>();
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
