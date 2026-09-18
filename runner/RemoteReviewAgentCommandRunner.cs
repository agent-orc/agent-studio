using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// CAR-backed, read-only coding-agent execution for a frozen Remote Review
/// aspect. Keeping the agent invocation adapter separate from repository tool
/// commands prevents the generic review workspace from becoming a second CLI
/// process-launch layer.
/// </summary>
internal sealed class RemoteReviewAgentCommandRunner
{
    private readonly RunnerOptions _options;
    private readonly ReviewLeaseDto _lease;
    private readonly string _repositoryPath;
    private readonly string _artifactPath;
    private readonly string _homePath;
    private readonly Action<string> _log;

    public RemoteReviewAgentCommandRunner(
        RunnerOptions options,
        ReviewLeaseDto lease,
        string repositoryPath,
        string artifactPath,
        string homePath,
        Action<string> log)
    {
        _options = options;
        _lease = lease;
        _repositoryPath = repositoryPath;
        _artifactPath = artifactPath;
        _homePath = homePath;
        _log = log;
    }

    public string PlannedExecutable(ReviewCommandDto command)
    {
        if (!ReviewCommandKinds.IsAgent(command.ExecutionKind)
            || string.IsNullOrWhiteSpace(command.CliType)
            || string.IsNullOrWhiteSpace(command.Model))
            return command.FileName;
        try
        {
            return Invocation(command).FileName;
        }
        catch (ArgumentException)
        {
            return command.FileName;
        }
    }

    public async Task<CommandExecution> RunAsync(
        ReviewCommandDto command,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(command.Prompt)
            || string.IsNullOrWhiteSpace(command.CliType)
            || string.IsNullOrWhiteSpace(command.Model))
        {
            return new CommandExecution(
                new ProcessResult(125, string.Empty,
                    $"Agent aspect '{command.StepId}' has no frozen prompt, CLI type, or model."),
                started,
                DateTime.UtcNow,
                null);
        }

        try
        {
            var invocation = Invocation(command);
            var runId = RemoteReviewWorkspace.SafeSegment(
                $"{_lease.AttemptId}-{command.StepId}");
            var workerDirectory = Path.Combine(
                _artifactPath,
                $"agent-{RemoteReviewWorkspace.SafeSegment(command.StepId)}");
            Directory.CreateDirectory(workerDirectory);
            var spec = new DetachedJobSpec(
                invocation.FileName,
                invocation.Arguments,
                _repositoryPath,
                command.Prompt,
                _artifactPath,
                Math.Clamp(command.TimeoutSeconds, 1, 7200),
                invocation.CliType,
                invocation.Model,
                invocation.ThinkingLevel,
                CodingAgentRunner.Model.CliPermissionModes.ReadOnly,
                CodingAgentRunner.Model.CliContextModes.Clean,
                RunnerOptions.ExecEngineCar,
                runId,
                CleanContextKey: runId);
            var (raw, timedOut, _) = await CarWorkerExecution.RunAsync(
                spec,
                workerDirectory,
                (stream, line) =>
                    _log($"review-agent step={command.StepId} stream={stream} {line}"),
                cleanContextRoot: _homePath);
            ct.ThrowIfCancellationRequested();
            var provider = ProviderOutputEvidenceExtractor.Extract(raw.StdOut);
            var response = string.IsNullOrWhiteSpace(provider.FinalAssistantOutput)
                ? raw.StdOut
                : provider.FinalAssistantOutput;
            return new CommandExecution(
                new ProcessResult(raw.ExitCode, response ?? string.Empty, raw.StdErr),
                started,
                DateTime.UtcNow,
                timedOut ? "timeout" : null,
                ParseUsage(raw.StdOut, command.Model, command.CliType));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CommandExecution(
                new ProcessResult(
                    127,
                    string.Empty,
                    $"Agent aspect '{command.StepId}' could not start: {exception.Message}"),
                started,
                DateTime.UtcNow,
                null);
        }
    }

    private AgentCliProcess.CliInvocation Invocation(ReviewCommandDto command)
        => AgentCliProcess.Resolve(
            _options,
            new RunSpecDto(
                command.CliType,
                command.Model,
                command.ThinkingLevel,
                CodingAgentRunner.Model.CliPermissionModes.ReadOnly,
                CodingAgentRunner.Model.CliContextModes.Clean));

    private static RemoteAgentUsage ParseUsage(string stdout, string model, string cliType)
    {
        var result = new RemoteAgentUsage(model, 0, 0, 0, 0, null);
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!TryJsonProperty(document.RootElement, "usage", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                    continue;
                var cached = Math.Max(JsonLong(usage, "cached_input_tokens"),
                    JsonLong(usage, "cache_read_input_tokens"));
                if (string.Equals(cliType, AgentCliProcess.CodexCli, StringComparison.OrdinalIgnoreCase))
                {
                    var normalized = ProviderUsageNormalization.OpenAi(
                        JsonLong(usage, "input_tokens"), cached);
                    result = new RemoteAgentUsage(
                        model,
                        normalized.InputTokens,
                        JsonLong(usage, "output_tokens"),
                        normalized.CacheReadTokens,
                        JsonLong(usage, "cache_creation_input_tokens"),
                        normalized.InputIncludesCached);
                }
                else
                {
                    result = new RemoteAgentUsage(
                        model,
                        JsonLong(usage, "input_tokens"),
                        JsonLong(usage, "output_tokens"),
                        cached,
                        JsonLong(usage, "cache_creation_input_tokens"),
                        false);
                }
            }
            catch (JsonException)
            {
                // Non-protocol output remains review evidence but has no token facts.
            }
        }
        return result;
    }

    private static long JsonLong(JsonElement element, string name)
        => TryJsonProperty(element, name, out var value) && value.TryGetInt64(out var parsed)
            ? Math.Max(0, parsed)
            : 0;

    private static bool TryJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
