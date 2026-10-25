namespace AgentStudio.Cli;

/// <summary>Shared-profile host Claude calls have no project filesystem context or tools.</summary>
public static class HostOneShotIsolation
{
    // Claude expands file mentions before tool restrictions take effect, including
    // mentions after punctuation and zero-width characters. Remove every sigil.
    public static string PreparePrompt(string prompt, bool isolated = true) => isolated
        ? prompt.Replace('@', '＠')
        : prompt;

    public sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "studio-one-shot-" + Guid.NewGuid().ToString("N"));

        public Workspace() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException ex) { SilentCatch.Note(ex, "HostOneShotIsolation: CLI may still be exiting"); }
            catch (UnauthorizedAccessException ex) { SilentCatch.Note(ex, "HostOneShotIsolation: temporary workspace cleanup"); }
        }
    }

    public static void AddClaudeArguments(ICollection<string> arguments, string model,
        IReadOnlyList<string>? extraArgs = null, bool networked = true)
    {
        arguments.Add("-p");
        arguments.Add("--output-format");
        arguments.Add("json");
        arguments.Add("--model");
        arguments.Add(model);
        if (!networked)
        {
            arguments.Add("--dangerously-skip-permissions");
            if (extraArgs is not null)
                foreach (var arg in extraArgs) arguments.Add(arg);
            return;
        }
        if (extraArgs is not null)
        {
            for (var i = 0; i < extraArgs.Count; i++)
            {
                var arg = extraArgs[i];
                if (arg == "--tools" && i + 1 < extraArgs.Count && extraArgs[i + 1] == "")
                {
                    i++;
                    continue;
                }
                if (arg is not ("-r" or "--max-budget-usd") || i + 1 >= extraArgs.Count)
                    throw new ArgumentException($"Unsupported host Claude one-shot argument '{arg}'.", nameof(extraArgs));
                var value = extraArgs[++i];
                if (arg == "-r" && !Guid.TryParse(value, out _)
                    || arg == "--max-budget-usd" && (!decimal.TryParse(value,
                        System.Globalization.NumberStyles.AllowDecimalPoint,
                        System.Globalization.CultureInfo.InvariantCulture, out var budget) || budget <= 0))
                    throw new ArgumentException($"Invalid value for host Claude one-shot argument '{arg}'.", nameof(extraArgs));
                arguments.Add(arg);
                arguments.Add(value);
            }
        }
        arguments.Add("--tools");
        arguments.Add("");
        arguments.Add("--strict-mcp-config");
        arguments.Add("--mcp-config");
        arguments.Add("{\"mcpServers\":{}}");
    }
}
