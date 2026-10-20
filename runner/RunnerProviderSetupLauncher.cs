using System.Diagnostics;
using System.Threading.Channels;

namespace AgentRunner;

/// <summary>
/// Bounded setup-only process boundary for interactive provider sign-in. Card
/// prompts and provider work still run through CodingAgentRunner. This launcher
/// accepts only the two sign-in commands and their status checks; it never
/// records child output or puts a credential on a command line.
/// </summary>
internal sealed class RunnerProviderSetupLauncher(string codexBinary, string claudeBinary)
{
    internal async Task<bool> RunAsync(string provider, bool status,
        Func<string, Task> onLine, CancellationToken ct)
    {
        var (fileName, arguments) = (provider, status) switch
        {
            ("codex", false) => (codexBinary, new[] { "login", "--device-auth" }),
            ("codex", true) => (codexBinary, new[] { "login", "status" }),
            ("claude", false) => (claudeBinary, new[] { "setup-token" }),
            ("claude", true) => (claudeBinary, new[] { "auth", "status", "--text" }),
            _ => throw new ArgumentException("Unsupported provider setup command.", nameof(provider)),
        };
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["NO_BROWSER"] = "1";
        start.Environment["BROWSER"] = "false";

        using var process = new Process { StartInfo = start };
        if (!process.Start()) return false;
        using var cancellation = ct.Register(() => Kill(process));
        var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var stdout = PumpAsync(process.StandardOutput, lines.Writer, ct);
        var stderr = PumpAsync(process.StandardError, lines.Writer, ct);
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in lines.Reader.ReadAllAsync(ct))
                    await onLine(line);
            }
            catch
            {
                Kill(process);
                throw;
            }
        }, ct);
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            lines.Writer.TryComplete();
            await reader;
            return process.ExitCode == 0;
        }
        finally
        {
            Kill(process);
            lines.Writer.TryComplete();
        }
    }

    private static async Task PumpAsync(StreamReader stream, ChannelWriter<string> writer, CancellationToken ct)
    {
        while (await stream.ReadLineAsync(ct) is { } line)
            writer.TryWrite(line);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return; }
    }
}
