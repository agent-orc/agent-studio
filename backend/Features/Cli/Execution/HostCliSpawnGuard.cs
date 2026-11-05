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

    public static void ValidateInteractive(bool networked)
    {
        if (networked)
            throw new InvalidOperationException("Host interactive CLI execution is disabled in the networked profile.");
    }

    public static void Validate(ProcessStartInfo startInfo, bool networked, string? provider = null)
    {
        if (!networked) return;
        var executable = (provider ?? Path.GetFileNameWithoutExtension(
            startInfo.FileName.Replace('\\', '/'))).ToLowerInvariant();
        if (executable is not ("claude" or "codex" or "gemini")) return;
        throw new InvalidOperationException(
            $"Host {executable} execution is disabled in the networked profile; use a runner job.");
    }
}
