using System.Diagnostics;

namespace AgentStudio.Tasks;

/// <summary>
/// Generates a short imperative English title from a free-text task
/// description by handing the input to a one-shot Claude Haiku
/// subprocess. The "Generate" button on the Create-task dialog calls
/// this; nothing in the request touches a job folder, so the call has
/// no on-disk side effects.
///
/// <para>The Haiku invocation reuses the stdin pattern from
/// <see cref="SummaryGenerationService"/> so long inputs do not overrun
/// Windows' CreateProcess command-line cap.</para>
///
/// <para>The model call is exposed as a virtual hook
/// (<see cref="InvokeAsync"/>) so tests can stub it out and exercise
/// the parsing / endpoint contract without billing tokens.</para>
/// </summary>
public class TitleGenerationService
{
    public const string TemplateName = "title-generate.md";

    private const int MaxInputChars = 8_000;
    private const int MaxTitleChars = 80;
    private const int HaikuTimeoutSeconds = 30;

    private readonly ILogger<TitleGenerationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RuntimePromptService _prompts;
    private readonly AdHocUsageRecorder? _usage;
    private readonly CliOneShotRegistry? _oneShotRegistry;

    public TitleGenerationService(
        ILogger<TitleGenerationService> logger,
        IConfiguration configuration,
        RuntimePromptService prompts,
        AdHocUsageRecorder? usage = null,
        CliOneShotRegistry? oneShotRegistry = null)
    {
        _logger = logger;
        _configuration = configuration;
        _prompts = prompts;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
    }

    /// <summary>
    /// Returns a sanitised single-line title for <paramref name="input"/>.
    /// Empty / whitespace input short-circuits to "Untitled task" without
    /// spawning Haiku.
    /// </summary>
    public virtual async Task<string> GenerateAsync(string input, CancellationToken ct = default)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0) return "Untitled task";

        var bounded = trimmed.Length > MaxInputChars
            ? trimmed[..MaxInputChars]
            : trimmed;

        var fallbackModel = _configuration["TitleGeneration:Model"]
                            ?? _configuration["ClaudeCli:SummaryModel"]
                            ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);
        var prompt = _prompts.Render(TemplateName,
            new Dictionary<string, string?> { ["input"] = bounded },
            new PromptCallContext(Step: "title-generation", Model: fallbackModel));

        var sw = AdHocClaudeInvoker.StartTiming();
        var (ok, raw, error) = await InvokeAsync(prompt, ct);
        sw.Stop();

        var (text, usage) = AdHocClaudeInvoker.ParseOrFallback(raw, fallbackModel);
        AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.TitleGeneration, fallbackModel, usage, sw.ElapsedMilliseconds, ok);

        if (!ok || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(error ?? "Title generator returned empty response");

        return SanitizeTitle(text);
    }

    /// <summary>
    /// Strip wrapping fences / quotes / leading prefixes Haiku occasionally
    /// adds despite the prompt, collapse to a single line, and clamp length.
    /// Public for tests.
    /// </summary>
    public static string SanitizeTitle(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return "Untitled task";

        // Drop wrapping ``` fences.
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }

        // Take only the first non-empty line.
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        s = lines.Length > 0 ? lines[0] : s;

        // Strip wrapping single or double quotes.
        if (s.Length >= 2)
        {
            char first = s[0], last = s[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                s = s[1..^1].Trim();
        }

        // Strip common leading prefixes the model occasionally adds.
        foreach (var prefix in new[] { "Title:", "Task:", "TODO:", "TASK:", "TITLE:" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].TrimStart();
                break;
            }
        }

        // Drop a trailing period (single).
        if (s.EndsWith('.') && !s.EndsWith("..")) s = s[..^1];

        if (s.Length > MaxTitleChars) s = s[..MaxTitleChars].TrimEnd();
        return s.Length == 0 ? "Untitled task" : s;
    }

    /// <summary>
    /// Spawn the Haiku subprocess and return its stdout. Override in tests
    /// to substitute a deterministic response without billing tokens.
    /// </summary>
    protected virtual async Task<(bool Ok, string? Raw, string? Error)> InvokeAsync(
        string prompt, CancellationToken ct)
    {
        var model = _configuration["TitleGeneration:Model"]
                    ?? _configuration["ClaudeCli:SummaryModel"]
                    ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);

        var oneShot = _oneShotRegistry?.Get("claude");
        if (oneShot != null)
        {
            var r = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: "claude", Model: model, Prompt: prompt)
            {
                Timeout = TimeSpan.FromSeconds(HaikuTimeoutSeconds),
                Source = AdHocUsageSources.TitleGeneration,
            }, ct).ConfigureAwait(false);
            if (!r.Ok) return (false, null, r.Error);
            _logger.LogInformation("Title generated in {Elapsed}ms ({Bytes} bytes)",
                (long)r.Duration.TotalMilliseconds, r.Stdout.Length);
            return (true, r.Stdout, null);
        }

        // Fallback for tests that build the service without DI. Still
        // stdin-piped; the OneShot path is just the production wiring.
        var claudePath = _configuration["ClaudeCli:Path"] ?? "claude";
        var psi = new ProcessStartInfo
        {
            FileName = GenericCliExecutionService.ResolveExecutable(claudePath),
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in AdHocClaudeInvoker.BuildArgs(model)) psi.ArgumentList.Add(arg);

        var sw = Stopwatch.StartNew();
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, null, "Process.Start returned null");
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(HaikuTimeoutSeconds));
            await p.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();
            if (p.ExitCode != 0)
                return (false, null, $"claude exited {p.ExitCode}: {stderr.Trim()}");
            return (true, stdout, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, $"Title generator timed out after {HaikuTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
