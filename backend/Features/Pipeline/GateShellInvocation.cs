namespace AgentStudio.Pipeline;

/// <summary>Builds the child process invocation without passing quoted commands through cmd.exe.</summary>
internal static class GateShellInvocation
{
    internal static (string FileName, IReadOnlyList<string> Arguments) Build(
        string command,
        VerifyCommandShell shell,
        bool isWindows,
        string bashPath,
        bool bashExists,
        string? npmCliPath = null)
    {
        if (!isWindows)
            return shell == VerifyCommandShell.Bash
                ? (bashPath, ["-lc", command])
                : ("/bin/sh", ["-c", command]);

        if (shell == VerifyCommandShell.Bash || bashExists)
            return (bashPath, ["-lc", command]);

        // Platform commands come from the convention planner: a single dotnet
        // or npm invocation. Preserve each quoted value as one OS argument.
        var parts = SplitArguments(command);
        if (parts.Count == 0 || parts[0] is not ("dotnet" or "npm"))
            throw new InvalidOperationException("A platform verify command requires Git Bash or a direct dotnet/npm invocation.");
        if (parts[0] == "npm")
        {
            // npm.cmd is a batch file and ProcessStartInfo with UseShellExecute=false
            // cannot start it. Run its JavaScript entry point with native node.exe.
            var npmCli = npmCliPath ?? FindNpmCli(Environment.GetEnvironmentVariable("PATH"));
            if (npmCli is null)
                throw new InvalidOperationException("The npm CLI was not found on PATH; a Windows npm gate requires Git Bash or npm-cli.js.");
            return ("node.exe", new[] { npmCli }.Concat(parts.Skip(1)).ToArray());
        }
        return (parts[0], parts.Skip(1).ToArray());
    }

    internal static string? FindNpmCli(string? path)
    {
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var root = directory.Trim().Trim('"');
            if (!File.Exists(Path.Combine(root, "npm.cmd"))) continue;
            var cli = Path.Combine(root, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(cli)) return cli;
        }
        return null;
    }

    private static IReadOnlyList<string> SplitArguments(string command)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        var started = false;
        foreach (var character in command)
        {
            if (character is '\'' or '"')
            {
                if (quote == '\0') { quote = character; started = true; continue; }
                if (quote == character) { quote = '\0'; continue; }
            }
            if (char.IsWhiteSpace(character) && quote == '\0')
            {
                if (started) { parts.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }
            if (quote == '\0' && character is ';' or '&' or '|' or '>' or '<')
                throw new InvalidOperationException("A composed platform command requires Git Bash.");
            current.Append(character);
            started = true;
        }
        if (quote != '\0') throw new InvalidOperationException("The platform command has an unmatched quote.");
        if (started) parts.Add(current.ToString());
        return parts;
    }
}
