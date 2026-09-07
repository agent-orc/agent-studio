using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Prompts;

/// <summary>
/// One-shot prompt enhancer for the "Enhance" button on the Create-task
/// dialog. Hands a free-text task description to Claude Haiku and returns
/// a refined prompt, a one-line intent, and a short list of topical tags.
/// No on-disk side effects: the caller (the dialog) shows the result as a
/// preview the user can apply or discard.
///
/// <para>Same Haiku stdin pattern as <see cref="TitleGenerationService"/>
/// and <see cref="SummaryGenerationService"/> (long inputs would otherwise
/// overrun Windows' CreateProcess command-line cap). The model call is
/// exposed as a virtual hook so tests can stub it without billing tokens.</para>
/// </summary>
public class PromptEnhancementService
{
    public const string TemplateName = "prompt-enhance.md";

    private const int MaxInputChars = 8_000;
    private const int MaxRefinedChars = 4_000;
    private const int MaxIntentChars = 200;
    private const int MaxTagChars = 40;
    private const int MaxTags = 5;
    private const int HaikuTimeoutSeconds = 45;

    private readonly ILogger<PromptEnhancementService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RuntimePromptService _prompts;
    private readonly AdHocUsageRecorder? _usage;

    private readonly CliOneShotRegistry? _oneShotRegistry;

    public PromptEnhancementService(
        ILogger<PromptEnhancementService> logger,
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

    public record EnhanceResult(string RefinedPrompt, string Intent, IReadOnlyList<string> Tags);

    /// <summary>
    /// Returns a refined prompt, a one-line intent, and topical tags for
    /// <paramref name="input"/>. Empty / whitespace input short-circuits to
    /// an empty result without spawning Haiku.
    /// </summary>
    public virtual async Task<EnhanceResult> EnhanceAsync(string input, CancellationToken ct = default)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0)
            return new EnhanceResult("", "", Array.Empty<string>());

        var bounded = trimmed.Length > MaxInputChars
            ? trimmed[..MaxInputChars]
            : trimmed;

        var fallbackModel = _configuration["PromptEnhancement:Model"]
                            ?? _configuration["TitleGeneration:Model"]
                            ?? _configuration["ClaudeCli:SummaryModel"]
                            ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);
        var prompt = _prompts.Render(TemplateName,
            new Dictionary<string, string?> { ["input"] = bounded },
            new PromptCallContext(Step: "prompt-enhancement", Model: fallbackModel));

        var sw = AdHocClaudeInvoker.StartTiming();
        var (ok, raw, error) = await InvokeAsync(prompt, ct);
        sw.Stop();

        var (text, usage) = AdHocClaudeInvoker.ParseOrFallback(raw, fallbackModel);
        AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.PromptEnhancement, fallbackModel, usage, sw.ElapsedMilliseconds, ok);

        if (!ok || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(error ?? "Prompt enhancer returned empty response");

        return Parse(text);
    }

    /// <summary>
    /// Parse Haiku's raw stdout into a sanitised result. Public for tests.
    /// Tolerates ``` fences and surrounding whitespace; rejects anything
    /// that does not contain a JSON object with the three expected fields.
    /// </summary>
    public static EnhanceResult Parse(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            throw new InvalidOperationException("Prompt enhancer returned empty response");

        // Drop wrapping ``` / ```json fences when Haiku ignores the contract.
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }

        // Some responses prefix prose before the JSON. Slice from the first
        // '{' to the matching last '}'.
        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
            throw new InvalidOperationException("Prompt enhancer response did not contain a JSON object");
        s = s[firstBrace..(lastBrace + 1)];

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var refined = ReadString(root, "refinedPrompt");
            var intent = ReadString(root, "intent");
            var tags = ReadTags(root);

            refined = ClampString(refined, MaxRefinedChars);
            intent = ClampString(SingleLine(intent), MaxIntentChars).TrimEnd('.').Trim();

            return new EnhanceResult(refined, intent, tags);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Prompt enhancer returned invalid JSON: {ex.Message}");
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return "";
        if (prop.ValueKind == JsonValueKind.String) return prop.GetString() ?? "";
        return prop.ToString();
    }

    private static IReadOnlyList<string> ReadTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var prop) || prop.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (result.Count >= MaxTags) break;
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            var slug = NormaliseTag(s);
            if (slug.Length == 0) continue;
            if (slug.Length > MaxTagChars) slug = slug[..MaxTagChars];
            if (seen.Add(slug)) result.Add(slug);
        }
        return result;
    }

    private static string NormaliseTag(string raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return "";

        var sb = new System.Text.StringBuilder(s.Length);
        var lastWasDash = false;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (ch == '-' || ch == '_' || char.IsWhiteSpace(ch))
            {
                if (sb.Length == 0 || lastWasDash) continue;
                sb.Append('-');
                lastWasDash = true;
            }
            // Drop everything else (commas, slashes, parens, quotes).
        }
        var slug = sb.ToString().Trim('-');
        return slug;
    }

    private static string SingleLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var first = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return first.Length > 0 ? first[0] : s.Trim();
    }

    private static string ClampString(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > max ? s[..max].TrimEnd() : s;
    }

    /// <summary>
    /// Spawn the Haiku subprocess and return its stdout. Override in tests
    /// to substitute a deterministic response without billing tokens.
    /// </summary>
    protected virtual async Task<(bool Ok, string? Raw, string? Error)> InvokeAsync(
        string prompt, CancellationToken ct)
    {
        var model = _configuration["PromptEnhancement:Model"]
                    ?? _configuration["TitleGeneration:Model"]
                    ?? _configuration["ClaudeCli:SummaryModel"]
                    ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);

        var oneShot = _oneShotRegistry?.Get("claude");
        if (oneShot != null)
        {
            var r = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: "claude", Model: model, Prompt: prompt)
            {
                Timeout = TimeSpan.FromSeconds(HaikuTimeoutSeconds),
                Source = AdHocUsageSources.PromptEnhancement,
            }, ct).ConfigureAwait(false);
            if (!r.Ok) return (false, null, r.Error);
            return (true, r.Stdout, null);
        }

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
            {
                _logger.LogWarning("Prompt enhancer exited {ExitCode} after {Elapsed}ms: {Stderr}",
                    p.ExitCode, sw.ElapsedMilliseconds, stderr.Trim());
                return (false, null, $"claude exited {p.ExitCode}: {stderr.Trim()}");
            }

            _logger.LogInformation("Prompt enhanced in {Elapsed}ms ({Bytes} bytes)",
                sw.ElapsedMilliseconds, stdout.Length);
            return (true, stdout, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, $"Prompt enhancer timed out after {HaikuTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt enhancer invocation failed");
            return (false, null, ex.Message);
        }
    }
}
