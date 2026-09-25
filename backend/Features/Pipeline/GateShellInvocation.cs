namespace AgentStudio.Pipeline;

/// <summary>Builds the child process invocation without passing quoted commands through cmd.exe.</summary>
internal static class GateShellInvocation
{
    internal static (string FileName, IReadOnlyList<string> Arguments) Build(
        string command,
        VerifyCommandShell shell,
        bool isWindows,
        string bashPath,
        bool bashExists)
    {
        if (!isWindows)
            return shell == VerifyCommandShell.Bash
                ? (bashPath, ["-lc", command])
                : ("/bin/sh", ["-c", command]);

        if (shell == VerifyCommandShell.Bash || bashExists)
            return (bashPath, ["-lc", command]);

        // Platform commands come from the convention planner: a single dotnet
        // or npm invocation. Preserve each quoted logger/filter value as one OS
        // argument when Git Bash is not installed.
        var parts = SplitArguments(command);
        if (parts.Count == 0 || parts[0] is not ("dotnet" or "npm"))
            throw new InvalidOperationException("A platform verify command requires Git Bash or a direct dotnet/npm invocation.");
        return (parts[0], parts.Skip(1).ToArray());
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
