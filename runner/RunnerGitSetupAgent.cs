using AgentStudio.TaskServer.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace AgentRunner;

/// <summary>Creates host-local credentials and proves repository access without returning secrets.</summary>
public sealed class RunnerGitSetupAgent(RunnerOptions options, TaskServerClient client, Action<string> log)
{
    public async Task PollAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (await client.ClaimGitSetupAsync(options.RunnerId, shutdown) is { } work)
                    await ExecuteAsync(work, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                log($"git setup deferred type={exception.GetType().Name}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        }
    }

    private async Task ExecuteAsync(RunnerGitSetupWork work, CancellationToken ct)
    {
        var root = Path.Combine(options.StateDir, "first-run-git", work.Id);
        Directory.CreateDirectory(root);
        var tokenFile = Path.Combine(root, "token");
        var keyPath = Path.Combine(root, "deploy-key");
        if (work.Action == "configure")
        {
            if (work.Method == "token" && work.Token is { } token)
            {
                try
                {
                    await WriteSecretAsync(tokenFile, token, ct);
                    await ConfigureGitAsync(work.RepositoryUrl, tokenFile, null, ct);
                    await File.WriteAllTextAsync(Path.Combine(root, "origin"), work.RepositoryUrl, ct);
                    await client.ReportGitSetupProofAsync(options.RunnerId, work.Id,
                        "configured", true, false, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    log($"git token setup failed type={exception.GetType().Name}");
                    await client.ReportGitSetupProofAsync(options.RunnerId, work.Id,
                        "configured", false, false, ct);
                }
            }
            else if (work.Method == "deploy-key")
            {
                try
                {
                    var result = await ProcessRunner.RunAsync("ssh-keygen",
                        ["-q", "-t", "ed25519", "-N", "", "-C", "agent-studio-deploy", "-f", keyPath],
                        root, ct: ct);
                    if (!result.Success) throw new InvalidOperationException("Deploy key generation failed.");
                    Protect(keyPath);
                    var publicKey = (await File.ReadAllTextAsync(keyPath + ".pub", ct)).Trim();
                    await ConfigureGitAsync(work.RepositoryUrl, null, keyPath, ct);
                    await File.WriteAllTextAsync(Path.Combine(root, "origin"), work.RepositoryUrl, ct);
                    await client.ReportGitSetupKeyAsync(options.RunnerId, work.Id, publicKey, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    log($"git deploy key setup failed type={exception.GetType().Name}");
                    await client.ReportGitSetupProofAsync(options.RunnerId, work.Id,
                        "configured", false, false, ct);
                }
            }
            return;
        }
        if (work.Action != "verify") return;
        var key = work.Method == "deploy-key" ? keyPath : null;
        var store = work.Method == "token" ? tokenFile : null;
        if (key is not null && !File.Exists(key) || store is not null && !File.Exists(store))
        {
            await client.ReportGitSetupProofAsync(options.RunnerId, work.Id, "proof", false, false, ct);
            return;
        }
        RepositoryAccessProof proof;
        try
        {
            var checker = new PlatformRepositoryGitProof(_ => key, options.StateDir);
            proof = await checker.ProveAsync(
                new RepositoryAccessBinding("workspace", work.RepositoryUrl, RequiresPush: true),
                key is null ? null : work.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            log($"git access proof failed type={exception.GetType().Name}");
            proof = new RepositoryAccessProof("workspace", work.RepositoryUrl, false, false, true, "probe-failed");
        }
        await client.ReportGitSetupProofAsync(options.RunnerId, work.Id, "proof",
            proof.FetchVerified, proof.PushVerified, ct);
    }

    internal static async Task ConfigureGitAsync(string origin, string? tokenFile,
        string? keyPath, CancellationToken ct, string? configPath = null, string? sshConfigPath = null)
    {
        var configured = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var config = configPath ?? (string.IsNullOrWhiteSpace(configured)
            || configured is "/dev/null" or "NUL"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig")
            : configured);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        var lines = new List<string>();
        if (File.Exists(config)) lines.Add(await File.ReadAllTextAsync(config, ct));
        if (tokenFile is not null)
        {
            if (tokenFile.Contains('\'')) throw new ArgumentException("Credential path contains a quote.");
            var script = Path.Combine(Path.GetDirectoryName(tokenFile)!, "git-helper.sh");
            var path = new Uri(origin).AbsolutePath.TrimStart('/');
            await File.WriteAllTextAsync(script, CredentialHelperScript(tokenFile, path), ct);
            Protect(script);
            lines.Add($"[credential \"{origin}\"]\n\thelper =\n\thelper = !sh '{script.Replace('\\', '/')}'\n\tuseHttpPath = true\n");
        }
        if (keyPath is not null)
        {
            if (keyPath.IndexOfAny(['\'', '"', '\r', '\n']) >= 0)
                throw new ArgumentException("Key path contains an unsupported character.");
            var path = origin.StartsWith("git@github.com:", StringComparison.Ordinal)
                ? origin["git@github.com:".Length..] : new Uri(origin).AbsolutePath.TrimStart('/');
            var alias = "agent-studio-first-run-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(origin))).ToLowerInvariant()[..32];
            await ConfigureSshAliasAsync(sshConfigPath ?? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), ".ssh", "config"), alias, keyPath, ct);
            var rewrite = $"[url \"git@{alias}:{path}\"]\n\tinsteadOf = {origin}\n";
            if (!lines.Any(line => line.Contains(rewrite, StringComparison.Ordinal)))
                lines.Add(rewrite);
        }
        await File.WriteAllTextAsync(config, string.Join("\n", lines), ct);
        Protect(config);
    }

    private static async Task ConfigureSshAliasAsync(string config, string alias, string keyPath,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(config)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        var marker = "# agent-studio-first-run " + alias;
        var block = $"{marker}\nHost {alias}\n  HostName github.com\n  User git\n"
            + $"  IdentityFile \"{keyPath.Replace('\\', '/')}\"\n  IdentitiesOnly yes\n"
            + "  IdentityAgent none\n  BatchMode yes\n  StrictHostKeyChecking accept-new\n"
            + $"  UserKnownHostsFile \"{Path.Combine(Path.GetDirectoryName(keyPath)!, "known_hosts").Replace('\\', '/')}\"\n"
            + $"Host *\n{marker} end\n";
        var existing = File.Exists(config) ? await File.ReadAllTextAsync(config, ct) : "";
        var start = existing.IndexOf(marker + "\n", StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = existing.IndexOf(marker + " end\n", start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("Managed SSH configuration is incomplete.");
            existing = existing.Remove(start, end + marker.Length + " end\n".Length - start);
        }
        await File.WriteAllTextAsync(config, block + existing, ct);
        Protect(config);
    }

    private static string CredentialHelperScript(string tokenFile, string repositoryPath)
        => "#!/bin/sh\n"
            + "[ \"$1\" = get ] || exit 0\n"
            + "protocol= host= path=\n"
            + "while IFS= read -r line && [ -n \"$line\" ]; do\n"
            + "  case \"$line\" in protocol=*) protocol=${line#protocol=};; host=*) host=${line#host=};; path=*) path=${line#path=};; esac\n"
            + "done\n"
            + $"[ \"$protocol\" = https ] && [ \"$host\" = github.com ] && [ \"$path\" = '{repositoryPath}' ] || exit 0\n"
            + $"IFS= read -r token < '{tokenFile.Replace('\\', '/')}'\n"
            + "printf 'username=x-access-token\\npassword=%s\\n' \"$token\"\n";

    private static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task WriteSecretAsync(string path, string value, CancellationToken ct)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                Protect(temporary);
                await using var writer = new StreamWriter(stream);
                await writer.WriteLineAsync(value.AsMemory(), ct);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
