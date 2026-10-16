using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Setup;

internal sealed record WindowsNativeDecisions(string ProviderCli, string GitAccess,
    string? GitTokenFile, string? GitRemote)
{
    public static WindowsNativeDecisions Resolve(ConsolePrompter prompter, string? provider, string? git,
        string? gitTokenFile, string? gitRemote)
    {
        Console.WriteLine($"Runner account: {Environment.UserDomainName}\\{Environment.UserName}");
        var codex = CommandOnPath("codex.cmd") || CommandOnPath("codex.exe");
        var claude = CommandOnPath("claude.cmd") || CommandOnPath("claude.exe");
        Console.WriteLine($"Provider CLIs: Codex {(codex ? "found" : "missing")}; Claude {(claude ? "found" : "missing")}.");
        provider ??= prompter.Ask("Runner provider (codex, claude, skip)",
            codex ? "codex" : claude ? "claude" : "skip");
        git ??= prompter.Ask("GitHub access (gcm, token, deploy-key, skip)", "skip");
        provider = provider.Trim().ToLowerInvariant();
        git = git.Trim().ToLowerInvariant();
        if (provider is not ("codex" or "claude" or "skip"))
            throw new ArgumentException("ProviderCli must be codex, claude, or skip.");
        if (git is not ("gcm" or "token" or "deploy-key" or "skip"))
            throw new ArgumentException("GitAccess must be gcm, token, deploy-key, or skip.");
        if (git is "gcm" or "token" && gitRemote is not null
            && !gitRemote.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GitRemote must be a GitHub HTTPS URL for gcm or token access.");
        if (git == "token")
            gitTokenFile ??= prompter.Ask("Owner-only file containing the GitHub fine-grained token");
        if (git == "deploy-key")
            gitRemote ??= prompter.Ask("Project GitHub SSH remote (git@github.com:OWNER/REPO.git)");
        if (gitTokenFile is not null && git == "token")
            SetupSecrets.RequireProtected(Path.GetFullPath(gitTokenFile), "GitHub token file");
        return new WindowsNativeDecisions(provider, git,
            gitTokenFile is null ? null : Path.GetFullPath(gitTokenFile), gitRemote);
    }

    public IReadOnlyDictionary<string, string> ToRecord(string? configRoot = null)
    {
        var record = new Dictionary<string, string>
        {
            ["providerCli"] = ProviderCli,
            ["gitAccess"] = GitAccess,
            ["gitRemote"] = GitRemote ?? "",
        };
        var keyRoot = configRoot is null ? null : Path.Combine(configRoot, "deploy-keys");
        if (keyRoot is not null && Directory.Exists(keyRoot))
            record["gitProjects"] = JsonSerializer.Serialize(Directory.GetFiles(keyRoot, "*.remote")
                .Select(File.ReadAllText).Select(remote => remote.Trim()).OrderBy(remote => remote).ToArray());
        return record;
    }

    private static bool CommandOnPath(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory.Trim('"'), name)));

    internal bool RequiresRunnerPassword => ProviderCli != "skip" || GitAccess != "skip";

    internal static bool InstallMissingProvider(bool unattended, ConsolePrompter prompter, string provider)
        => unattended || prompter.Confirm($"Install {provider} CLI with npm for this Windows account", true);

    public async Task PrepareProviderAsync(ConsolePrompter prompter, bool unattended,
        Func<string, bool>? commandOnPath = null,
        Func<string, IReadOnlyList<string>, Task>? install = null,
        Func<Task>? installNode = null)
    {
        commandOnPath ??= CommandOnPath;
        install ??= (file, arguments) => new ProcessRunner(false).RequireAsync(file, arguments);
        installNode ??= InstallNodeAsync;
        if (ProviderCli == "skip")
        {
            Console.WriteLine("Provider login skipped. Open the local Runner host card in Studio and use its provider sign-in action before running coding tasks.");
        }
        else
        {
            var executable = ProviderCli + ".cmd";
            if (!commandOnPath(executable) && !commandOnPath(ProviderCli + ".exe"))
            {
                if (!InstallMissingProvider(unattended, prompter, ProviderCli))
                    throw new InvalidOperationException($"{ProviderCli} CLI is missing. Install it for the runner account or choose ProviderCli=skip.");
                if (!commandOnPath("node.exe") || !commandOnPath("npm.cmd"))
                {
                    Console.WriteLine("Node.js and npm are needed for the provider CLI. Installing Node.js for this machine.");
                    await installNode();
                    if (!commandOnPath("node.exe") || !commandOnPath("npm.cmd"))
                        throw new InvalidOperationException("Node.js installation completed, but node.exe or npm.cmd is unavailable. Reopen setup after checking the machine PATH.");
                }
                var package = ProviderCli == "codex" ? "@openai/codex" : "@anthropic-ai/claude-code";
                await install("cmd.exe",
                    ["/d", "/s", "/c", $"npm install --global {package}"]);
            }
            if (!unattended && prompter.Confirm($"Run {ProviderCli} login for the runner account now", true))
            {
                var start = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/s");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add(ProviderCli == "codex" ? "codex login" : "claude");
                using var login = Process.Start(start)
                    ?? throw new InvalidOperationException($"Could not start {ProviderCli} login.");
                await login.WaitForExitAsync();
                if (login.ExitCode != 0)
                    throw new InvalidOperationException($"{ProviderCli} login exited with code {login.ExitCode}.");
            }
        }
        if (GitAccess == "gcm" && !unattended
            && prompter.Confirm("Sign in to GitHub through Git Credential Manager now", true))
        {
            var start = new ProcessStartInfo("git.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("credential-manager");
            start.ArgumentList.Add("github");
            start.ArgumentList.Add("login");
            using var login = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start Git Credential Manager login.");
            await login.WaitForExitAsync();
            if (login.ExitCode != 0)
                throw new InvalidOperationException($"Git Credential Manager login exited with code {login.ExitCode}.");
        }
    }

    private static async Task InstallNodeAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await InstallNodeAsync(client,
            async (file, arguments) => (await new ProcessRunner(false).RunAsync(
                file, arguments, printOutput: false)).ExitCode,
            Environment.GetEnvironmentVariable,
            path => Environment.SetEnvironmentVariable("PATH", path));
    }

    internal static async Task InstallNodeAsync(HttpClient client,
        Func<string, IReadOnlyList<string>, Task<int>> runInstaller,
        Func<string, EnvironmentVariableTarget, string?> getEnvironmentVariable,
        Action<string> setProcessPath)
    {
        // Match the repository's Windows release toolchain. The published setup
        // is self-contained, so the pinned version is embedded here as well.
        const string version = "22.23.1";
        var fileName = $"node-v{version}-x64.msi";
        var release = $"https://nodejs.org/download/release/v{version}/";
        var checksums = await client.GetStringAsync(release + "SHASUMS256.txt");
        var checksum = checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1] == fileName)
            .Select(parts => parts[0]).SingleOrDefault()
            ?? throw new InvalidDataException("Node.js release checksum is missing for the Windows x64 installer.");
        var temporary = Directory.CreateTempSubdirectory("agent-studio-node-");
        try
        {
            var installer = Path.Combine(temporary.FullName, fileName);
            await using (var source = await client.GetStreamAsync(release + fileName))
            await using (var destination = File.Create(installer))
                await source.CopyToAsync(destination);
            await using (var stream = File.OpenRead(installer))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Node.js installer checksum did not match its release manifest.");
            }
            var exitCode = await runInstaller("msiexec.exe",
                ["/i", installer, "/qn", "/norestart", "ALLUSERS=1"]);
            if (exitCode is not (0 or 3010))
                throw new InvalidOperationException($"Node.js installation exited with code {exitCode}.");
            var machinePath = getEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
            var userPath = getEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            setProcessPath(string.Join(Path.PathSeparator,
                new[] { machinePath, userPath }.Where(path => !string.IsNullOrWhiteSpace(path))));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    internal async Task VerifyDeployKeyAsync(ConsolePrompter prompter, string configRoot,
        Func<IReadOnlyDictionary<string, string?>, Task<bool>> verify)
    {
        if (GitAccess != "deploy-key" || GitRemote is null) return;
        var environment = File.ReadAllLines(Path.Combine(configRoot, "runner.env"))
            .Where(line => line.StartsWith("GIT_", StringComparison.Ordinal) && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => (string?)parts[1], StringComparer.Ordinal);
        environment["GIT_TERMINAL_PROMPT"] = "0";
        while (prompter.Confirm($"Add the public key shown above to {GitRemote}, then verify access now", true))
        {
            if (await verify(environment))
            {
                Console.WriteLine($"GitHub access verified for {GitRemote}.");
                return;
            }
            Console.WriteLine("GitHub access is not ready. Check the repository deploy key and its permissions.");
        }
        Console.WriteLine($"GitHub access remains unverified. Add the displayed deploy key to {GitRemote}, then rerun guided setup with the same deploy-key choice to verify it.");
    }
}
