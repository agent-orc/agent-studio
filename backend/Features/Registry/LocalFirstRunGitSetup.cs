using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Registry;

public sealed record LocalGitSetupRequest(string ProjectId, string Method, string? Token);
public sealed record LocalGitSetupStatus(string Id, string State, string? PublicKey,
    bool FetchVerified, bool PushVerified);

/// <summary>Local-profile GitHub credentials live under the backend service account.</summary>
public sealed class LocalFirstRunGitSetup
{
    private readonly ProjectRegistry _projects;
    private readonly LocalFirstRunRepositoryProbe _probe;
    private readonly string _root;
    private readonly string _config;
    private readonly ConcurrentDictionary<string, LocalGitSetupStatus> _setups = new(StringComparer.Ordinal);

    public LocalFirstRunGitSetup(ProjectRegistry projects, LocalFirstRunRepositoryProbe probe)
        : this(projects, probe, Path.Combine(ApplicationDataRoot(), "AgentStudio", "first-run-git"),
            GitConfigPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh", "config")) { }

    public LocalFirstRunGitSetup(ProjectRegistry projects, LocalFirstRunRepositoryProbe probe,
        string root, string config, string? sshConfig = null)
    {
        _projects = projects;
        _probe = probe;
        _root = root;
        _config = config;
    }

    public LocalGitSetupStatus? Get(string id) => _setups.GetValueOrDefault(id);

    public async Task<LocalGitSetupStatus> StartAsync(LocalGitSetupRequest request, CancellationToken ct)
    {
        var project = _projects.FindById(request.ProjectId)
            ?? throw new ArgumentException("Project is unavailable.");
        var origin = project.Urls.FirstOrDefault(url => url.Id == "repo")?.Url;
        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !ValidRepositoryPath(uri.AbsolutePath)
            || request.Method is not ("token" or "deploy-key")
            || request.Method == "token" && (request.Token is not { Length: >= 20 and <= 512 }
                || request.Token.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')))
            || request.Method == "deploy-key" && request.Token is not null)
            throw new ArgumentException("Choose a registered GitHub repository and valid access method.");

        var id = "local-git-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(_root, id);
        Directory.CreateDirectory(root);
        LocalGitSetupStatus status;
        if (request.Method == "token")
        {
            var config = _config;
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            var tokenFile = Path.Combine(root, "token");
            if (tokenFile.Contains('\'')) throw new ArgumentException("Credential path contains a quote.");
            await WriteSecretAsync(tokenFile, request.Token!, ct);
            var script = Path.Combine(root, "git-helper.sh");
            await File.WriteAllTextAsync(script,
                CredentialHelperScript(tokenFile, uri.AbsolutePath.TrimStart('/')), ct);
            Protect(script);
            await File.AppendAllTextAsync(config,
                $"\n[credential \"{origin}\"]\n\thelper =\n\thelper = !sh '{script.Replace('\\', '/')}'\n\tuseHttpPath = true\n", ct);
            Protect(config);
            _probe.SetDeployKey(project.Id, origin, null);
            status = new(id, "awaiting-verify", null, false, false);
        }
        else
        {
            var key = Path.Combine(root, "deploy-key");
            if (key.IndexOfAny(['\'', '"', '\r', '\n']) >= 0)
                throw new ArgumentException("Key path contains an unsupported character.");
            if (!await RunAsync("ssh-keygen", root,
                    ["-q", "-t", "ed25519", "-N", "", "-C", "agent-studio-deploy", "-f", key], ct))
                throw new InvalidOperationException("Deploy key generation failed.");
            Protect(key);
            var publicKey = (await File.ReadAllTextAsync(key + ".pub", ct)).Trim();
            _probe.SetDeployKey(project.Id, origin, key);
            status = new(id, "awaiting-github", publicKey, false, false);
        }
        _setups[id] = status;
        await File.WriteAllTextAsync(Path.Combine(root, "project"), request.ProjectId, ct);
        return status;
    }

    public async Task<LocalGitSetupStatus?> VerifyAsync(string id, CancellationToken ct)
    {
        if (!_setups.TryGetValue(id, out var existing)) return null;
        var projectPath = Path.Combine(_root, id, "project");
        var projectId = await File.ReadAllTextAsync(projectPath, ct);
        var project = _projects.FindById(projectId);
        if (project is null) return null;
        var keyPath = existing.PublicKey is null ? null : Path.Combine(_root, id, "deploy-key");
        var result = await _probe.ReadAsync(project, "main", ct, force: true, keyPath: keyPath);
        if (result is null) return null;
        var json = JsonSerializer.SerializeToElement(result);
        var receipt = json.GetProperty("probes")[0];
        var verified = receipt.GetProperty("admitted").GetBoolean();
        var verdict = receipt.GetProperty("verdict").GetString();
        var status = existing with
        {
            State = verified ? "verified" : "denied",
            FetchVerified = verdict is not ("read-denied" or "origin-mismatch"),
            PushVerified = verified,
        };
        _setups[id] = status;
        return status;
    }

    private static async Task<bool> RunAsync(string executable, string directory,
        IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) return false;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode == 0;
    }

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

    private static string GitConfigPath()
    {
        var configured = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        return string.IsNullOrWhiteSpace(configured) || configured is "/dev/null" or "NUL"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig")
            : configured;
    }

    private static string ApplicationDataRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (Path.IsPathFullyQualified(appData)) return appData;
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
            ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
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

    private static bool ValidRepositoryPath(string absolutePath)
    {
        var path = absolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length is > 0 and <= 100
            && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    }
}

public static class LocalFirstRunGitSetupEndpoints
{
    public static void MapLocalFirstRunGitSetupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/first-run/git-setups", async (LocalGitSetupRequest request,
            LocalFirstRunGitSetup setup, CancellationToken ct) =>
        {
            try { return Results.Ok(await setup.StartAsync(request, ct)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapGet("/api/first-run/git-setups/{id}", (string id, LocalFirstRunGitSetup setup)
            => setup.Get(id) is { } status ? Results.Ok(status) : Results.NotFound());
        app.MapPost("/api/first-run/git-setups/{id}/verify", async (
            string id, LocalFirstRunGitSetup setup, CancellationToken ct)
            => await setup.VerifyAsync(id, ct) is { } status ? Results.Ok(status) : Results.NotFound());
    }
}
