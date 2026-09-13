using System.Diagnostics;
using System.Text;

namespace AgentStudio.Setup;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal sealed class ProcessRunner(bool dryRun)
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? input = null,
        bool printOutput = true,
        CancellationToken cancellationToken = default)
    {
        var args = arguments.ToArray();
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] {fileName} {string.Join(' ', args.Select(QuoteForDisplay))}");
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        }

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var result = new ProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
        if (printOutput)
        {
            if (result.Output.Length > 0)
                Console.Write(result.Output);
            if (result.Error.Length > 0)
                Console.Error.Write(result.Error);
        }
        return result;
    }

    public async Task RequireAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? input = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            fileName,
            arguments,
            environment,
            input,
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} exited with code {result.ExitCode}.");
    }

    public async Task<string?> FindCommandAsync(
        string command,
        string? user = null,
        string? homeDirectory = null)
    {
        var shellCommand = $"command -v {ShellName(command)}";
        var inspectionRunner = dryRun ? new ProcessRunner(dryRun: false) : this;
        ProcessResult result;
        if (string.IsNullOrWhiteSpace(user))
        {
            result = await inspectionRunner.RunAsync(
                "/bin/sh",
                ["-lc", shellCommand],
                printOutput: false);
        }
        else
        {
            var inspectCurrentUserDirectly =
                Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1"
                && string.Equals(user, Environment.UserName, StringComparison.Ordinal);
            var userArguments = new List<string>();
            if (!inspectCurrentUserDirectly)
                userArguments.AddRange(["-u", user, "--"]);
            if (!string.IsNullOrWhiteSpace(homeDirectory))
            {
                userArguments.Add("env");
                userArguments.Add($"HOME={homeDirectory}");
            }
            userArguments.Add("/bin/sh");
            userArguments.Add("-lc");
            userArguments.Add(shellCommand);
            result = await inspectionRunner.RunAsync(
                inspectCurrentUserDirectly ? userArguments[0] : "runuser",
                inspectCurrentUserDirectly ? userArguments.Skip(1) : userArguments,
                printOutput: false);
        }
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private static string ShellName(string value)
    {
        if (value.Length == 0 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Command name contains unsupported characters.");
        return value;
    }

    private static string QuoteForDisplay(string value)
        => value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '/' or '.' or '_' or '-' or ':' or '=')
            ? value
            : $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
