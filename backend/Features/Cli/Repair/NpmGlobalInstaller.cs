using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded process boundary for installing or relinking one global npm package.
/// Package selection and repair policy live in
/// <see cref="LocalCliRepairService"/>; this class only executes npm.
/// </summary>
public class NpmGlobalInstaller
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<NpmExecutableResolution>> _resolveExecutable;

    public NpmGlobalInstaller()
    {
        _resolveExecutable = ResolveNpmExecutableAsync;
    }

    internal NpmGlobalInstaller(
        Func<CancellationToken, Task<NpmExecutableResolution>> resolveExecutable)
    {
        _resolveExecutable = resolveExecutable;
    }

    public virtual async Task<NpmGlobalInstallResult> InstallAsync(
        string packageName,
        NpmGlobalInstallMode mode,
        CancellationToken ct)
    {
        try
        {
            var resolution = await _resolveExecutable(ct);
            if (resolution.Command is null)
            {
                return new NpmGlobalInstallResult(
                    NpmGlobalInstallOutcome.NpmUnavailable,
                    null,
                    "",
                    resolution.Detail);
            }

            var arguments = resolution.Command.PrefixArguments
                .Concat(BuildArguments(packageName, mode))
                .ToArray();
            var execution = await ExecuteAsync(
                resolution.Command.ExecutablePath,
                resolution.Command.WorkingDirectory,
                arguments,
                DefaultTimeout,
                ct);
            return new NpmGlobalInstallResult(
                execution.ExitCode == 0
                    ? NpmGlobalInstallOutcome.Succeeded
                    : NpmGlobalInstallOutcome.Failed,
                execution.ExitCode,
                execution.StandardOutput,
                execution.StandardError);
        }
        catch (Exception ex)
        {
            return new NpmGlobalInstallResult(
                NpmGlobalInstallOutcome.Failed,
                null,
                "",
                ex.Message);
        }
    }

    /// <summary>
    /// Re-runs the installed package's own postinstall script with the active
    /// node. Launcher packages (Claude Code since 2.1.263) ship a placeholder
    /// binary and let that script hard-link or copy the real platform binary
    /// from the optional native dependency; when npm skipped the step the
    /// package looks healthy but the launcher is still the placeholder.
    /// </summary>
    public virtual async Task<NpmGlobalInstallResult> RunPackageInstallScriptAsync(
        string packageDirectory,
        string scriptFileName,
        CancellationToken ct)
    {
        try
        {
            var node = await ResolveNodeExecutableAsync(ct);
            if (node is null)
            {
                return new NpmGlobalInstallResult(
                    NpmGlobalInstallOutcome.NodeUnavailable,
                    null,
                    "",
                    "node unavailable: no candidate beside the active Node installation or on PATH passed 'node --version'.");
            }

            var execution = await ExecuteAsync(
                node,
                packageDirectory,
                [scriptFileName],
                DefaultTimeout,
                ct);
            return new NpmGlobalInstallResult(
                execution.ExitCode == 0
                    ? NpmGlobalInstallOutcome.Succeeded
                    : NpmGlobalInstallOutcome.Failed,
                execution.ExitCode,
                execution.StandardOutput,
                execution.StandardError);
        }
        catch (Exception ex)
        {
            return new NpmGlobalInstallResult(
                NpmGlobalInstallOutcome.Failed,
                null,
                "",
                ex.Message);
        }
    }

    internal static async Task<string?> ResolveNodeExecutableAsync(CancellationToken ct)
    {
        var candidates = NodeExecutableCandidates(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
        foreach (var candidate in candidates)
        {
            var result = await ExecuteAsync(
                candidate,
                Path.GetDirectoryName(candidate)!,
                ["--version"],
                PreflightTimeout,
                ct);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                return candidate;
        }
        return null;
    }

    internal static IReadOnlyList<string> NodeExecutableCandidates(
        bool isWindows,
        string? pathValue,
        string? programFiles,
        string? programFilesX86)
    {
        var fileName = isWindows ? "node.exe" : "node";
        var separator = isWindows ? ';' : Path.PathSeparator;
        var candidates = FindOnPath(pathValue, fileName, separator).ToList();
        if (isWindows)
        {
            foreach (var root in new[] { programFiles, programFilesX86 }
                         .Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                var node = Path.Combine(root!, "nodejs", fileName);
                if (File.Exists(node)) candidates.Add(node);
            }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<string> BuildArguments(
        string packageName,
        NpmGlobalInstallMode mode)
    {
        var arguments = new List<string> { "install", "--global", packageName };
        if (mode == NpmGlobalInstallMode.ForceRelink) arguments.Add("--force");
        return arguments;
    }

    internal static async Task<NpmExecutableResolution> ResolveNpmExecutableAsync(
        CancellationToken ct)
    {
        var candidates = NpmExecutableCandidates(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("APPDATA"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            ResolveWindowsCommandInterpreter());
        var checkedCandidates = new List<string>();
        foreach (var candidate in candidates)
        {
            checkedCandidates.Add(candidate.DisplayPath);
            var result = await ExecuteAsync(
                candidate.ExecutablePath,
                candidate.WorkingDirectory,
                candidate.PrefixArguments.Concat(["--version"]).ToArray(),
                PreflightTimeout,
                ct);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return new NpmExecutableResolution(
                    candidate with { Version = result.StandardOutput.Trim() },
                    "");
            }
        }

        var checkedDetail = checkedCandidates.Count == 0
            ? "No npm candidates were found beside the active Node installation, in APPDATA, or on PATH."
            : $"No npm candidate passed 'npm --version'. Checked: {string.Join(", ", checkedCandidates)}.";
        return new NpmExecutableResolution(
            null,
            $"npm unavailable: {checkedDetail}");
    }

    internal static IReadOnlyList<NpmExecutable> NpmExecutableCandidates(
        bool isWindows,
        string? pathValue,
        string? appData,
        string? programFiles,
        string? programFilesX86,
        string? commandInterpreter)
    {
        var candidates = new List<NpmExecutable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isWindows)
        {
            var nodeExecutables = FindOnPath(pathValue, "node.exe", ';').ToList();
            foreach (var root in new[] { programFiles, programFilesX86 }
                         .Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                var node = Path.Combine(root!, "nodejs", "node.exe");
                if (File.Exists(node)) nodeExecutables.Add(node);
            }

            foreach (var nodeExecutable in nodeExecutables.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var nodeDirectory = Path.GetDirectoryName(nodeExecutable)!;
                var npmCli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(npmCli))
                {
                    Add(new NpmExecutable(
                        nodeExecutable,
                        nodeDirectory,
                        [npmCli],
                        "",
                        npmCli));
                }

                AddCommand(Path.Combine(nodeDirectory, "npm.cmd"));
            }

            if (!string.IsNullOrWhiteSpace(appData))
                AddCommand(Path.Combine(appData, "npm", "npm.cmd"));
            foreach (var command in FindOnPath(pathValue, "npm.cmd", ';')) AddCommand(command);
            foreach (var command in FindOnPath(pathValue, "npm.exe", ';')) AddCommand(command);
        }
        else
        {
            foreach (var command in FindOnPath(pathValue, "npm", Path.PathSeparator)) AddCommand(command);
        }

        return candidates;

        void AddCommand(string command)
        {
            if (!File.Exists(command)) return;
            var isCommandScript = isWindows
                                  && Path.GetExtension(command) is ".cmd" or ".bat";
            if (isCommandScript)
            {
                if (string.IsNullOrWhiteSpace(commandInterpreter)
                    || !File.Exists(commandInterpreter)) return;
                Add(new NpmExecutable(
                    commandInterpreter,
                    Path.GetDirectoryName(command)!,
                    ["/d", "/c", command],
                    "",
                    command));
                return;
            }
            Add(new NpmExecutable(
                command,
                Path.GetDirectoryName(command)!,
                [],
                "",
                command));
        }

        void Add(NpmExecutable candidate)
        {
            var key = candidate.ExecutablePath + "\0" + string.Join("\0", candidate.PrefixArguments);
            if (seen.Add(key)) candidates.Add(candidate);
        }
    }

    private static string? ResolveWindowsCommandInterpreter()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var configured = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory)) return null;
        var commandInterpreter = Path.Combine(systemDirectory, "cmd.exe");
        return File.Exists(commandInterpreter) ? commandInterpreter : null;
    }

    private static IEnumerable<string> FindOnPath(
        string? pathValue,
        string fileName,
        char separator)
    {
        if (string.IsNullOrWhiteSpace(pathValue)) yield break;
        foreach (var rawDirectory in pathValue.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(directory, fileName)); }
            catch { continue; }
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    private static async Task<NpmProcessResult> ExecuteAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeoutValue,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

            if (!process.Start()) return new NpmProcessResult(null, "", "process did not start");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutValue);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "NpmGlobalInstaller: timeout kill best-effort"); }
                return new NpmProcessResult(
                    null,
                    await SafeOutputAsync(stdout),
                    $"process timed out after {timeoutValue.TotalSeconds:0} seconds");
            }

            return new NpmProcessResult(
                process.ExitCode,
                await SafeOutputAsync(stdout),
                await SafeOutputAsync(stderr));
        }
        catch (Exception ex)
        {
            return new NpmProcessResult(null, "", ex.Message);
        }
    }

    private static async Task<string> SafeOutputAsync(Task<string> output)
    {
        try { return await output; }
        catch { return ""; }
    }
}

internal sealed record NpmExecutable(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> PrefixArguments,
    string Version,
    string DisplayPath);

internal sealed record NpmExecutableResolution(
    NpmExecutable? Command,
    string Detail);

internal sealed record NpmProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError);

public enum NpmGlobalInstallMode
{
    Install,
    ForceRelink,
}

public enum NpmGlobalInstallOutcome
{
    Succeeded,
    Failed,
    NpmUnavailable,
    /// <summary>No usable node was found for a package postinstall re-run.</summary>
    NodeUnavailable,
}

public sealed record NpmGlobalInstallResult(
    NpmGlobalInstallOutcome Outcome,
    int? ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => Outcome == NpmGlobalInstallOutcome.Succeeded;
}
