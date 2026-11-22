using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Executes one frozen Remote Review agent aspect. The workspace calls it from
/// several aspects at once (AGT-3424), so an implementation keeps no state per
/// call and stops the aspect when its token is cancelled.
/// </summary>
internal interface IReviewAgentCommandRunner
{
    string PlannedExecutable(ReviewCommandDto command);

    Task<CommandExecution> RunAsync(ReviewCommandDto command, CancellationToken ct);

    /// <summary>
    /// Whether this runner puts the shared review prefix of <paramref name="command"/>
    /// at a provider cache breakpoint, so a sibling started after the lead's first
    /// streamed usage reads it from the cache (AGT-3971).
    /// </summary>
    bool SharesReviewPrefix(ReviewCommandDto command) => false;

    Task<CommandExecution> RunAsync(ReviewCommandDto command, ReviewAspectLaunch launch, CancellationToken ct)
        => RunAsync(command, ct);
}

/// <summary>
/// CAR-backed, read-only coding-agent execution for a frozen Remote Review
/// aspect. Keeping the agent invocation adapter separate from repository tool
/// commands prevents the generic review workspace from becoming a second CLI
/// process-launch layer.
/// </summary>
internal sealed class RemoteReviewAgentCommandRunner : IReviewAgentCommandRunner
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

    public Task<CommandExecution> RunAsync(ReviewCommandDto command, CancellationToken ct)
        => RunAsync(command, ReviewAspectLaunch.Plain, ct);

    public bool SharesReviewPrefix(ReviewCommandDto command)
    {
        // The flags reach the CLI through the runner's Linux process spawner.
        if (!OperatingSystem.IsLinux()
            || !ReviewCommandKinds.IsAgent(command.ExecutionKind)
            || string.IsNullOrWhiteSpace(command.CliType)
            || string.IsNullOrWhiteSpace(command.Model))
            return false;
        try
        {
            var invocation = Invocation(command);
            return string.Equals(invocation.CliType, CliSelection.ClaudeCli, StringComparison.OrdinalIgnoreCase)
                   && ClaudeSharedPrefixLaunch.IsSupported(invocation.FileName);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<CommandExecution> RunAsync(
        ReviewCommandDto command,
        ReviewAspectLaunch launch,
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
            var stdin = command.Prompt;
            IReadOnlyList<string>? extraArguments = null;
            if (launch.SharedPrefixLength > 0 && SharesReviewPrefix(command))
            {
                var shared = ClaudeSharedPrefixLaunch.Compose(command.Prompt, launch.SharedPrefixLength, workerDirectory);
                stdin = shared.Stdin;
                extraArguments = shared.Arguments;
                _log($"review-aspect-shared-prefix step={command.StepId} sharedCharacters={launch.SharedPrefixLength} " +
                     $"aspectCharacters={stdin.Length}");
            }
            var firstUsage = launch.OnFirstUsage;
            var spec = new DetachedJobSpec(
                invocation.FileName,
                [],
                _repositoryPath,
                stdin,
                _artifactPath,
                Math.Clamp(command.TimeoutSeconds, 1, 7200),
                invocation.CliType,
                invocation.Model,
                invocation.ThinkingLevel,
                CodingAgentRunner.Model.CliPermissionModes.ReadOnly,
                CodingAgentRunner.Model.CliContextModes.Clean,
                runId,
                CleanContextKey: runId);
            var (raw, timedOut, _) = await CarWorkerExecution.RunAsync(
                spec,
                workerDirectory,
                (stream, line) =>
                {
                    _log($"review-agent step={command.StepId} stream={stream}");
                    if (firstUsage is not null && stream == "stdout"
                        && ClaudeSharedPrefixLaunch.TryReadFirstUsage(line, command.Model, out var usage)
                        && Interlocked.Exchange(ref firstUsage, null) is { } notify)
                        notify(usage);
                },
                cleanContextRoot: _homePath,
                extraCliArguments: extraArguments,
                stopToken: ct);
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

    private CliSelection.Selection Invocation(ReviewCommandDto command)
        => CliSelection.Resolve(
            _options,
            new RunSpecDto(
                command.CliType,
                command.Model,
                command.ThinkingLevel,
                CodingAgentRunner.Model.CliPermissionModes.ReadOnly,
                CodingAgentRunner.Model.CliContextModes.Clean));

    internal static RemoteAgentUsage ParseUsage(string stdout, string model, string cliType)
    {
        var result = new RemoteAgentUsage(model, 0, 0, 0, 0, null);
        var messages = new Dictionary<string, RemoteAgentUsage>(StringComparer.Ordinal);
        var terminalSeen = false;
        var messageIndex = 0;
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var assistant = TryJsonProperty(root, "type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "assistant";
                var envelope = assistant && TryJsonProperty(root, "message", out var message) ? message : root;
                if (!TryJsonProperty(envelope, "usage", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                    continue;
                if (assistant && terminalSeen) continue;
                var cached = Math.Max(JsonLong(usage, "cached_input_tokens"),
                    JsonLong(usage, "cache_read_input_tokens"));
                if (string.Equals(cliType, CliSelection.CodexCli, StringComparison.OrdinalIgnoreCase))
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
                if (assistant)
                {
                    // A non-string id (number, object, null) is not a message identity;
                    // GetString would throw InvalidOperationException, which the
                    // JsonException handler below does not catch.
                    var key = TryJsonProperty(envelope, "id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null;
                    messages[key ?? "frame:" + messageIndex++] = result;
                }
                else terminalSeen = true;
            }
            catch (JsonException)
            {
                // Non-protocol output remains review evidence but has no token facts.
            }
        }
        if (!terminalSeen && messages.Count > 0)
            result = new RemoteAgentUsage(model, messages.Values.Sum(x => x.InputTokens),
                messages.Values.Sum(x => x.OutputTokens), messages.Values.Sum(x => x.CacheReadTokens),
                messages.Values.Sum(x => x.CacheCreationTokens), false);
        return result;
    }

    private static long JsonLong(JsonElement element, string name)
        => TryJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
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
