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
            var finished = DateTime.UtcNow;
            // Command output can quote diagnostics from the checkout. Attribute
            // the failure to this host only if the runner's own probe confirms it.
            var checkoutReadFailure = invocation.CliType == CliSelection.CodexCli
                                      && CodexReviewCheckoutFailure.HasFailureHint(raw)
                ? (await CodexReviewSandboxProbe.RunAsync(_options, ct, log: _log)).NamespaceFailure
                : null;
            return new CommandExecution(
                new ProcessResult(raw.ExitCode, response ?? string.Empty, raw.StdErr),
                started,
                finished,
                timedOut ? "timeout" : null,
                ParseUsage(raw.StdOut, command.Model, command.CliType))
            {
                CheckoutReadFailure = checkoutReadFailure,
            };
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
        var messages = new Dictionary<string, IReadOnlyList<ReviewModelUsageDto>>(StringComparer.Ordinal);
        var results = new Dictionary<string, IReadOnlyList<ReviewModelUsageDto>>(StringComparer.Ordinal);
        var messageIndex = 0;
        var codex = string.Equals(cliType, CliSelection.CodexCli, StringComparison.OrdinalIgnoreCase);
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var assistant = JsonString(root, "type") == "assistant";
                if (assistant && results.Count > 0) continue;
                var envelope = assistant && TryJsonProperty(root, "message", out var message) ? message : root;
                var rows = ParseModelUsage(envelope, model, codex);
                if (rows.Count == 0) continue;
                if (assistant)
                {
                    var key = JsonString(envelope, "id") ?? "frame:" + messageIndex++;
                    messages[key] = rows;
                }
                else
                {
                    // Like ClaudeUsageParser.ParseAll, modelUsage is a running
                    // per-session snapshot. A repeated result replaces its scope.
                    // A legacy result without a session id remains the command's
                    // final snapshot, preserving the existing remote contract.
                    var session = JsonString(root, "session_id");
                    var scope = string.IsNullOrWhiteSpace(session) ? "command" : "session:" + session.Trim();
                    results[scope] = rows;
                }
            }
            catch (JsonException)
            {
                // Non-protocol output remains review evidence but has no token facts.
            }
        }
        var snapshots = results.Count > 0 ? results.Values : messages.Values;
        var perModel = snapshots.SelectMany(rows => rows)
            .GroupBy(row => new { row.Model, row.ModelSource, row.InputIncludesCached })
            .Select(group => group.First() with
            {
                InputTokens = group.Sum(row => row.InputTokens),
                OutputTokens = group.Sum(row => row.OutputTokens),
                CacheReadTokens = group.Sum(row => row.CacheReadTokens),
                CacheCreationTokens = group.Sum(row => row.CacheCreationTokens),
            })
            .ToArray();
        var models = perModel.Select(row => row.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sources = perModel.Select(row => row.ModelSource).Distinct(StringComparer.Ordinal).ToArray();
        return new RemoteAgentUsage(models.Length == 1 ? models[0] : null,
            perModel.Sum(row => row.InputTokens), perModel.Sum(row => row.OutputTokens),
            perModel.Sum(row => row.CacheReadTokens), perModel.Sum(row => row.CacheCreationTokens),
            perModel.Length > 0 && perModel.All(row => row.InputIncludesCached == perModel[0].InputIncludesCached)
                ? perModel[0].InputIncludesCached : null)
        {
            PinnedModel = model,
            ModelMismatch = perModel.Any(row => row.ModelMismatch),
            ModelSource = sources.Length == 1 && models.Length == 1 ? sources[0]
                : perModel.Length > 1 ? "per-model" : "requested-fallback",
            ModelUsage = perModel,
        };
    }

    private static IReadOnlyList<ReviewModelUsageDto> ParseModelUsage(JsonElement frame, string pinnedModel, bool codex)
    {
        var observedModel = JsonString(frame, "model");
        // Mirror the canonical Claude ParseAll semantics: modelUsage takes
        // precedence over aggregate usage, including its camel/snake aliases.
        if (!codex && TryJsonProperty(frame, "modelUsage", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            var rows = new List<ReviewModelUsageDto>();
            foreach (var property in models.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var usage = property.Value;
                var row = ModelRow(property.Name.Trim(), pinnedModel,
                    JsonLongEither(usage, "inputTokens", "input_tokens"),
                    JsonLongEither(usage, "outputTokens", "output_tokens"),
                    JsonLongEither(usage, "cacheReadInputTokens", "cache_read_input_tokens"),
                    JsonLongEither(usage, "cacheCreationInputTokens", "cache_creation_input_tokens"), false);
                if (row.InputTokens + row.OutputTokens + row.CacheReadTokens + row.CacheCreationTokens > 0)
                    rows.Add(row);
            }
            if (rows.Count > 0) return rows;
            var names = models.EnumerateObject().Select(property => property.Name.Trim())
                .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 1) observedModel = names[0];
        }
        if (!TryJsonProperty(frame, "usage", out var counters) || counters.ValueKind != JsonValueKind.Object) return [];
        var input = JsonLong(counters, "input_tokens");
        var cache = Math.Max(JsonLong(counters, "cached_input_tokens"), JsonLong(counters, "cache_read_input_tokens"));
        bool? inputIncludesCached = false;
        if (codex)
        {
            // A numeric zero is evidence. Missing, invalid, or impossible cache
            // counts leave the provider total intact and its split unknown.
            var reportedCache = JsonNumber(counters, "cached_input_tokens");
            if (reportedCache is { } knownCache && knownCache <= input)
            {
                var normalized = ProviderUsageNormalization.OpenAi(input, knownCache);
                input = normalized.InputTokens;
                cache = normalized.CacheReadTokens;
                inputIncludesCached = normalized.InputIncludesCached;
            }
            else
            {
                cache = 0;
                inputIncludesCached = null;
            }
        }
        return [ModelRow(observedModel, pinnedModel, input,
            JsonLong(counters, "output_tokens"), cache,
            JsonLong(counters, "cache_creation_input_tokens"), inputIncludesCached)];
    }

    private static ReviewModelUsageDto ModelRow(string? observed, string pinned, long input, long output,
        long cacheRead, long cacheWrite, bool? inputIncludesCached)
    {
        var model = string.IsNullOrWhiteSpace(observed) ? null : observed.Trim();
        return new ReviewModelUsageDto(model, input, output, cacheRead, cacheWrite, inputIncludesCached,
            pinned, model is not null && !ExecutionModelIdentity.Equivalent(pinned, model),
            model is null ? "requested-fallback" : "observed");
    }

    private static string? JsonString(JsonElement element, string name)
        => TryJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? JsonNumber(JsonElement element, string name)
        => TryJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed) && parsed >= 0 ? parsed : null;

    private static long JsonLong(JsonElement element, string name) => JsonNumber(element, name) ?? 0;

    private static long JsonLongEither(JsonElement element, string first, string second)
        => JsonLong(element, first) is var value && value > 0 ? value : JsonLong(element, second);

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
