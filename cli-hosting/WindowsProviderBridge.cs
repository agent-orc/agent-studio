using System.Diagnostics;

namespace AgentStudio.CliHosting;

/// <summary>
/// Runs the packaged Windows provider entry through node.exe without a command shell.
/// CodingAgentRunner starts this bridge as its selected CLI executable; the
/// bridge only forwards argv and the child's exit code.
/// </summary>
public static class WindowsProviderBridge
{
    public static bool IsBridgeProcess(string? processPath, out string provider, bool? windows = null)
    {
        provider = Path.GetFileNameWithoutExtension(processPath?.Replace('\\', '/')) switch
        {
            "provider-codex" => "codex",
            "provider-claude" => "claude",
            _ => string.Empty,
        };
        return (windows ?? OperatingSystem.IsWindows()) && provider.Length > 0;
    }

    public static async Task<int> RunAsync(string provider, string[] arguments)
    {
        var node = Environment.GetEnvironmentVariable("AGENT_STUDIO_NODE_EXE");
        var script = Environment.GetEnvironmentVariable(
            provider == "codex" ? "AGENT_STUDIO_CODEX_JS" : "AGENT_STUDIO_CLAUDE_JS");
        if (string.IsNullOrWhiteSpace(node) || !File.Exists(node)
            || string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            Console.Error.WriteLine($"{provider} installation is missing. Repair the Windows Studio provider setup.");
            return 2;
        }
        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {provider} through node.exe.");
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
}
