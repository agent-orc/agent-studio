using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Agent.Features.Execution;

/// <summary>One allowlisted compose target. Both values come from the agent host, never from a command.</summary>
public sealed record RestartableService(string ComposeFile, string ComposeService);

/// <summary>The agent-local allowlist (<c>OPERATIONS_SERVICES_FILE</c>).</summary>
public sealed record RestartableServices(IReadOnlyDictionary<string, RestartableService> Services)
{
    public static RestartableServices Empty { get; } = new(new Dictionary<string, RestartableService>());

    /// <summary>
    /// Reads <c>{ "services": { "review-daemon": { "composeFile": "/abs/compose.yaml", "composeService": "review" } } }</c>.
    /// Names, absolute paths and compose service names are validated; an invalid file advertises nothing.
    /// </summary>
    public static RestartableServices Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        var document = JsonSerializer.Deserialize<ServicesDocument>(File.ReadAllText(path), OperationsProtocol.Json)
            ?? throw new InvalidDataException("The restartable services file is empty.");
        var services = new Dictionary<string, RestartableService>(StringComparer.Ordinal);
        foreach (var (name, target) in document.Services ?? [])
        {
            if (!OperationCatalogue.ValidServiceName(name) || target is null
                || !Path.IsPathFullyQualified(target.ComposeFile) || !File.Exists(target.ComposeFile)
                || !OperationCatalogue.ValidServiceName(target.ComposeService))
                throw new InvalidDataException($"The restartable service '{name}' is invalid.");
            services[name] = target;
        }
        if (services.Count > 16) throw new InvalidDataException("At most 16 restartable services are allowed.");
        return new RestartableServices(services);
    }

    private sealed record ServicesDocument(Dictionary<string, RestartableService>? Services);
}

/// <summary>Exit code and bounded output of one tool call.</summary>
public sealed record ToolRun(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// AGT-3207: executes <c>service.restart</c>. The agent validates the compose
/// configuration first (<c>docker compose config --quiet</c>); an invalid
/// configuration fails the attempt without touching the container. Otherwise
/// it recreates exactly the allowlisted compose service. One attempt runs at a
/// time (agent capacity 1), so restarts never overlap.
/// </summary>
public static class ServiceRestart
{
    public static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RecreateTimeout = TimeSpan.FromSeconds(120);

    public static async Task<OperationResult> ExecuteAsync(OperationAttempt attempt, string agentId, string bootId,
        RestartableServices services, Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ToolRun>> run,
        TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (attempt.AgentId != agentId || attempt.BootId != bootId || attempt.Fence < 1
            || attempt.State is not ("running" or "cancelling") || attempt.LeaseUntil is null || attempt.LeaseUntil <= now
            || attempt.Command.Deadline <= now || attempt.Command.OperationId != OperationCatalogue.ServiceRestart.Id
            || attempt.Command.Version != OperationCatalogue.ServiceRestart.Version
            || OperationsProtocol.Digest(attempt.Command.Input) != attempt.Command.InputDigest
            || !attempt.Command.Input.TryGetProperty("service", out var nameElement)
            || nameElement.GetString() is not { } name || !services.Services.TryGetValue(name, out var target))
            throw new InvalidOperationException("The assignment is outside the agent capability or lease.");
        if (attempt.State == "cancelling")
            return Result(attempt, "cancelled", "cancelled-before-execution", Empty(), now, clock.GetUtcNow());

        var validate = await run(Docker("compose", "-f", target.ComposeFile, "config", "--quiet"), ValidateTimeout, ct);
        if (validate.ExitCode != 0 || validate.TimedOut)
            return Result(attempt, "failed", validate.TimedOut ? "configuration-check-timeout" : "configuration-invalid",
                Output(name, restarted: false, configurationValid: false), now, clock.GetUtcNow());

        var recreate = await run(Docker("compose", "-f", target.ComposeFile, "up", "-d", "--force-recreate", "--no-deps",
            target.ComposeService), RecreateTimeout, ct);
        var succeeded = recreate.ExitCode == 0 && !recreate.TimedOut;
        return Result(attempt, succeeded ? "succeeded" : "failed",
            succeeded ? "completed" : recreate.TimedOut ? "restart-timeout" : "restart-failed",
            Output(name, restarted: succeeded, configurationValid: true), now, clock.GetUtcNow());
    }

    /// <summary>The process runner the agent uses; it hides the console window and kills the tree on timeout.</summary>
    public static async Task<ToolRun> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            return new ToolRun(-1, "timeout", true);
        }
        var text = (await stdout) + (await stderr);
        return new ToolRun(process.ExitCode, text.Length <= 2048 ? text : text[..2048], false);
    }

    public static ProcessStartInfo Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static JsonElement Output(string service, bool restarted, bool configurationValid)
        => JsonSerializer.SerializeToElement(new { service, restarted, configurationValid });

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });

    private static OperationResult Result(OperationAttempt attempt, string outcome, string classification,
        JsonElement output, DateTimeOffset started, DateTimeOffset finished)
        => new(outcome, attempt.Command.InputDigest, classification, output, [], true, started, finished);
}
