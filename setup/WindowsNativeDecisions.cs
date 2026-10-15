using System.Diagnostics;
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

    public async Task PrepareProviderAsync(ConsolePrompter prompter, bool unattended)
    {
        if (ProviderCli == "skip")
        {
            Console.WriteLine("Provider login skipped. Open the local Runner host card in Studio and use its provider sign-in action before running coding tasks.");
        }
        else
        {
            var executable = ProviderCli + ".cmd";
            if (!CommandOnPath(executable) && !CommandOnPath(ProviderCli + ".exe"))
            {
                if (unattended || !prompter.Confirm($"Install {ProviderCli} CLI with npm for this Windows account", true))
                    throw new InvalidOperationException($"{ProviderCli} CLI is missing. Install it for the runner account or choose ProviderCli=skip.");
                var package = ProviderCli == "codex" ? "@openai/codex" : "@anthropic-ai/claude-code";
                await new ProcessRunner(false).RequireAsync("cmd.exe",
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
}
