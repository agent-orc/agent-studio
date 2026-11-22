using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// How the workspace launches one agent aspect. <paramref name="SharedPrefixLength"/>
/// is the length of the prompt part every aspect of the review shares byte for byte
/// (see <see cref="ReviewAspectInputBudget.Result"/>); <paramref name="OnFirstUsage"/>
/// fires once, on the first provider usage the CLI streams.
/// </summary>
internal sealed record ReviewAspectLaunch(int SharedPrefixLength, Action<RemoteAgentUsage>? OnFirstUsage = null)
{
    public static readonly ReviewAspectLaunch Plain = new(0);
}

/// <summary>
/// Puts the review material that the aspects of one review share at a Claude
/// prompt-cache breakpoint (AGT-3971).
/// <para>
/// Claude Code sets its cache breakpoints at the end of the system prompt and at
/// the end of the conversation. A shared prefix inside the user message is never a
/// breakpoint, so a sibling aspect cannot read what the lead wrote; and the default
/// system prompt names the memory directory below the per-aspect
/// <c>CLAUDE_CONFIG_DIR</c>, so even the system prompt differed between aspects.
/// The shared part therefore goes into the system prompt through
/// <c>--append-system-prompt-file</c>, and
/// <c>--exclude-dynamic-system-prompt-sections</c> moves the per-machine sections
/// (cwd, memory path, git status) into the first user message. The system prompt
/// is then byte-identical for every aspect of the review and ends at a breakpoint;
/// only the aspect's own question goes to stdin.
/// </para>
/// </summary>
internal static class ClaudeSharedPrefixLaunch
{
    internal const string AppendSystemPromptFileFlag = "--append-system-prompt-file";
    internal const string ExcludeDynamicSectionsFlag = "--exclude-dynamic-system-prompt-sections";
    internal const string SharedContextFileName = "shared-review-context.md";

    /// <summary>
    /// Longest wait for the lead aspect's first streamed usage before its siblings
    /// start anyway. Bounds the wall-time cost of the lead.
    /// </summary>
    internal static readonly TimeSpan MaximumLead = TimeSpan.FromSeconds(120);

    private static readonly ConcurrentDictionary<string, bool> Support = new(StringComparer.Ordinal);

    internal sealed record Launch(string Stdin, IReadOnlyList<string> Arguments, string SharedContextPath);

    /// <summary>Writes the shared part next to the aspect's worker files and returns the CLI input.</summary>
    internal static Launch Compose(string prompt, int sharedPrefixLength, string workerDirectory)
    {
        if (sharedPrefixLength <= 0 || sharedPrefixLength > prompt.Length)
            throw new ArgumentOutOfRangeException(nameof(sharedPrefixLength));
        var path = Path.Combine(workerDirectory, SharedContextFileName);
        File.WriteAllText(path, prompt[..sharedPrefixLength]);
        var question = prompt[sharedPrefixLength..].TrimStart('\r', '\n');
        return new Launch(
            question.Length == 0 ? "Review the aspect described above." : question,
            [AppendSystemPromptFileFlag, path, ExcludeDynamicSectionsFlag],
            path);
    }

    /// <summary>
    /// Whether <paramref name="executable"/> accepts both flags; an older CLI would
    /// reject the call as an unknown option. Probed once per executable and process.
    /// </summary>
    internal static bool IsSupported(string executable, Func<string, string>? help = null)
        => Support.GetOrAdd(executable, path => Supports((help ?? ReadHelp)(path)));

    internal static bool Supports(string helpText)
        => helpText.Contains(ExcludeDynamicSectionsFlag, StringComparison.Ordinal)
           && helpText.Contains("--append-system-prompt", StringComparison.Ordinal);

    private static string ReadHelp(string executable)
    {
        try
        {
            // Once per executable and process, before the first aspect starts.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = ProcessRunner.RunAsync(executable, ["--help"], Environment.CurrentDirectory, ct: timeout.Token)
                .GetAwaiter().GetResult();
            return result.StdOut + result.StdErr;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads the usage of the first assistant message in Claude's stream-json
    /// output. By then the provider has started streaming the response, which is
    /// when a cache entry becomes readable for the siblings.
    /// </summary>
    internal static bool TryReadFirstUsage(string line, string model, out RemoteAgentUsage usage)
    {
        usage = null!;
        if (!line.Contains("\"usage\"", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var values)
                || values.ValueKind != JsonValueKind.Object)
                return false;
            usage = new RemoteAgentUsage(
                model,
                Long(values, "input_tokens"),
                Long(values, "output_tokens"),
                Long(values, "cache_read_input_tokens"),
                Long(values, "cache_creation_input_tokens"),
                false);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long Long(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? Math.Max(0, parsed) : 0;
}
