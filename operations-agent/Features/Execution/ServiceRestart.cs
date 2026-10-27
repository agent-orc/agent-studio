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
    /// Names, absolute paths and compose service names are validated. An
    /// unreadable or invalid file advertises nothing and is reported through
    /// <paramref name="warn"/>; it never stops the agent, which still serves its other operations.
    /// </summary>
    public static RestartableServices Load(string? path, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        try
        {
            return Parse(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or NotSupportedException)
        {
            warn?.Invoke($"operations-agent: restartable services file {path} is invalid and offers no restarts: {exception.Message}");
            return Empty;
        }
    }

    private static RestartableServices Parse(string path)
    {
        var document = JsonSerializer.Deserialize<ServicesDocument>(File.ReadAllText(path), OperationsProtocol.Json)
            ?? throw new InvalidDataException("The restartable services file is empty.");
        var services = new Dictionary<string, RestartableService>(StringComparer.Ordinal);
        foreach (var (name, target) in document.Services ?? [])
        {
            if (!OperationCatalogue.ValidServiceName(name) || target is null
                || string.IsNullOrWhiteSpace(target.ComposeFile)
                || !Path.IsPathFullyQualified(target.ComposeFile) || !File.Exists(target.ComposeFile)
                || string.IsNullOrWhiteSpace(target.ComposeService)
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

    /// <summary>The exit code <see cref="RunAsync"/> reports when the tool could not be started at all.</summary>
    public const int ToolUnavailable = 127;

    /// <summary>
    /// Why the agent refuses this restart before any side effect, or null to
    /// run it: the service is not in the local allowlist, or the lease left
    /// cannot cover a bounded validate and recreate. A refusal is a definite
    /// failure that the agent reports; it never stops the agent.
    /// </summary>
    public static string? Refusal(OperationAttempt attempt, RestartableServices services, DateTimeOffset now)
    {
        if (!attempt.Command.Input.TryGetProperty("service", out var name) || name.ValueKind != JsonValueKind.String
            || !services.Services.ContainsKey(name.GetString()!)) return "service-not-allowlisted";
        return attempt.LeaseUntil is { } lease && lease - now < ValidateTimeout + RecreateTimeout
            ? "insufficient-lease" : null;
    }

    public static async Task<OperationResult> ExecuteAsync(OperationAttempt attempt, string agentId, string bootId,
        RestartableServices services, Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ToolRun>> run,
        TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (attempt.AgentId != agentId || attempt.BootId != bootId || attempt.Fence < 1
            || attempt.State is not ("running" or "cancelling") || attempt.LeaseUntil is null || attempt.LeaseUntil <= now
            || attempt.Command.Deadline <= now || attempt.Command.OperationId != OperationCatalogue.ServiceRestart.Id
            || attempt.Command.Version != OperationCatalogue.ServiceRestart.Version
            || OperationsProtocol.Digest(attempt.Command.Input) != attempt.Command.InputDigest)
            throw new InvalidOperationException("The assignment is outside the agent capability or lease.");
        if (attempt.State == "cancelling")
            return Result(attempt, "cancelled", "cancelled-before-execution", Empty(), now, clock.GetUtcNow());
        if (Refusal(attempt, services, now) is { } refusal)
            return Result(attempt, "failed", refusal, Empty(), now, clock.GetUtcNow());
        var name = attempt.Command.Input.GetProperty("service").GetString()!;
        var target = services.Services[name];

        var validate = await run(Docker("compose", "-f", target.ComposeFile, "config", "--quiet"), ValidateTimeout, ct);
        if (validate.ExitCode != 0 || validate.TimedOut)
            return Result(attempt, "failed", validate.TimedOut ? "configuration-check-timeout"
                    : validate.ExitCode == ToolUnavailable ? "docker-unavailable" : "configuration-invalid",
                Output(name, restarted: false, configurationValid: false), now, clock.GetUtcNow());

        var recreate = await run(Docker("compose", "-f", target.ComposeFile, "up", "-d", "--force-recreate", "--no-deps",
            target.ComposeService), RecreateTimeout, ct);
        var succeeded = recreate.ExitCode == 0 && !recreate.TimedOut;
        return Result(attempt, succeeded ? "succeeded" : "failed",
            succeeded ? "completed" : recreate.TimedOut ? "restart-timeout" : "restart-failed",
            Output(name, restarted: succeeded, configurationValid: true), now, clock.GetUtcNow());
    }

    /// <summary>
    /// The process runner the agent uses; it hides the console window and kills
    /// the process tree on timeout and on the caller's cancellation, so a
    /// stopping agent never leaves a docker call running.
    /// </summary>
    public static async Task<ToolRun> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // The tool is missing: a definite failure, nothing ran.
            return new ToolRun(ToolUnavailable, $"{start.FileName} could not start: {exception.Message}", false);
        }
        var stdout = ReadBoundedAsync(process.StandardOutput, ct);
        var stderr = ReadBoundedAsync(process.StandardError, ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            await Task.WhenAll(stdout, stderr);
            return new ToolRun(-1, "timeout", true);
        }
        var output = (await stdout) + (await stderr);
        return new ToolRun(process.ExitCode, output.Length <= 2048 ? output : output[..2048], false);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var captured = new System.Text.StringBuilder(2048);
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (captured.Length < 2048)
                captured.Append(buffer, 0, Math.Min(count, 2048 - captured.Length));
        }
        return captured.ToString();
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
