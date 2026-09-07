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
          ALLOW_INSECURE_HTTP (1 only for a contained Compose network)
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
