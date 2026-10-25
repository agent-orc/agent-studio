using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>Last boundary before a provider CLI starts in the Studio API process.</summary>
public static class HostCliSpawnGuard
{
    public static Process? Start(ProcessStartInfo startInfo, bool networked, string? provider = null)
    {
        Validate(startInfo, networked, provider);
        return Process.Start(startInfo);
    }

    public static void Validate(ProcessStartInfo startInfo, bool networked, string? provider = null)
    {
        if (!networked) return;
        var executable = (provider ?? Path.GetFileNameWithoutExtension(
            startInfo.FileName.Replace('\\', '/'))).ToLowerInvariant();
        if (executable is not ("claude" or "codex" or "gemini")) return;

        var args = startInfo.ArgumentList.Count > 0
            ? startInfo.ArgumentList.ToArray()
            : startInfo.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 1 && args[0] == "--version") return;
        if (executable != "claude")
            throw new InvalidOperationException($"Host {executable} execution is disabled in the networked profile.");

        var toolsIndex = Array.IndexOf(args, "--tools");
        var mcpIndex = Array.IndexOf(args, "--mcp-config");
        var isolated = toolsIndex >= 0 && toolsIndex + 1 < args.Length && args[toolsIndex + 1] == ""
            && args.Count(arg => arg == "--tools") == 1
            && args.Contains("--strict-mcp-config", StringComparer.Ordinal)
            && args.Count(arg => arg == "--strict-mcp-config") == 1
            && mcpIndex >= 0 && mcpIndex + 1 < args.Length && args[mcpIndex + 1] == "{\"mcpServers\":{}}"
            && args.Count(arg => arg == "--mcp-config") == 1
            && startInfo.CreateNoWindow
            && !string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            && Path.GetFileName(startInfo.WorkingDirectory)
                .StartsWith("studio-one-shot-", StringComparison.Ordinal);
        var unsafeFlags = args.Any(arg => arg is "--dangerously-skip-permissions"
            or "--allowedTools" or "--allowed-tools" or "--permission-mode" or "--add-dir"
            or "--resume" or "-r" or "--sandbox" or "danger-full-access"
            or "bypassPermissions" || arg.StartsWith("--tools=", StringComparison.Ordinal)
            || arg.StartsWith("--mcp-config=", StringComparison.Ordinal)
            || arg.Contains("dangerously-skip-permissions", StringComparison.Ordinal)
            || arg.Contains("bypassPermissions", StringComparison.Ordinal)
            || arg.Contains("danger-full-access", StringComparison.Ordinal));
        if (!isolated || unsafeFlags)
            throw new InvalidOperationException("Host Claude execution requires an isolated, tool-free one-shot in the networked profile.");
    }
}
