using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Runs the SSH steps stored on a claimed release job. Values travel on stdin, never in a remote command.</summary>
public sealed class SshDeployExecutor
{
    private static readonly Regex RemotePath = new(ProjectDeploymentVocabulary.RemotePathPattern, RegexOptions.CultureInvariant);
    private static readonly Regex Host = new(ProjectDeploymentVocabulary.IdentifierPattern, RegexOptions.CultureInvariant);
    private readonly Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> _run;

    public SshDeployExecutor(Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>>? run = null)
        => _run = run ?? ((file, args, input, ct) => ProcessRunner.RunAsync(file, args, stdin: input, ct: ct));

    public async Task ExecuteAsync(string host, ProjectDeployStep step, string artifact, string artifactStore,
        IReadOnlyDictionary<string, string> variables, Func<string, Task> log, CancellationToken ct,
        string? bundlePath = null)
    {
        if (!Host.IsMatch(host) || step.Target != host || step.Step is not ("static-site" or "container"))
            throw new InvalidOperationException("Invalid SSH deploy target or step.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        await log($"Starting {step.Step} on {host}.");
        try
        {
            if (step.Step == "static-site")
            {
                if (!RemotePath.IsMatch(step.Directory ?? "")) throw new InvalidOperationException("Invalid static target directory.");
                var digest = Digest(artifact);
                var version = step.Directory + "/releases/" + digest;
                var staging = version + ".tmp-" + Guid.NewGuid().ToString("N");
                var bundle = await ExtractBundle(artifact, artifactStore, limit.Token);
                try
                {
                    var relative = bundlePath ?? ".";
                    if (!Regex.IsMatch(relative, ProjectDeploymentVocabulary.ArtifactPathPattern))
                        throw new InvalidDataException("Invalid static artifact path.");
                    var source = Path.Combine(bundle, relative);
                    if (!Directory.Exists(source)) throw new InvalidDataException("Static bundle is missing from release package.");
                    try
                    {
                        await Remote(host, new { action = "prepare", path = staging }, limit.Token);
                        await Command("scp", ["-r", .. SshOptions(), source + "/.", host + ":" + staging], null, limit.Token);
                        await Remote(host, new { action = "switch", path = step.Directory, staging, version }, limit.Token);
                    }
                    catch
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        try { await Remote(host, new { action = "cleanup", path = staging }, cleanup.Token); }
                        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
                        {
                            Console.Error.WriteLine($"SSH staging cleanup failed: {ex.GetType().Name}.");
                        }
                        throw;
                    }
                }
                finally
                {
                    if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
                }
            }
            else
            {
                if (!RemotePath.IsMatch(step.ComposeFile ?? "") || step.Service is null || !Host.IsMatch(step.Service))
                    throw new InvalidOperationException("Invalid compose file or service.");
                if (variables.Any(item => !Regex.IsMatch(item.Key, "^[A-Za-z_][A-Za-z0-9_]*$")
                    || item.Value.Contains('\n') || item.Value.Contains('\r')))
                    throw new InvalidOperationException("Invalid deploy variable.");
                Digest(artifact);
                await Remote(host, new { action = "compose", file = step.ComposeFile, service = step.Service,
                    image = artifact, variables }, limit.Token);
            }
            await log($"Completed {step.Step} on {host}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("SSH deploy step timed out.");
        }
    }

    private async Task Remote(string host, object payload, CancellationToken ct)
    {
        // The base64 script is constant. SSH invokes a remote shell, but no value from the repository or job
        // appears in its command. The helper parses one JSON line and calls OS programs with argument arrays.
        var script = Convert.ToBase64String(Encoding.UTF8.GetBytes(RemoteScript));
        var command = "python3 -c \"exec(__import__('base64').b64decode('" + script + "'))\"";
        await Command("ssh", [.. SshOptions(), host, command], JsonSerializer.Serialize(payload) + "\n", ct);
    }

    private static string[] SshOptions() => ["-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=10", "-o", "ForwardAgent=no"];

    private async Task Command(string file, IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        var result = await _run(file, args, input, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed with exit code {result.ExitCode}.");
    }

    private static string Digest(string artifact)
    {
        var digest = artifact.Split("@sha256:", StringSplitOptions.None).Last();
        if (digest.Length != 64 || digest.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException("Invalid artifact digest.");
        return digest.ToLowerInvariant();
    }

    private static async Task<string> ExtractBundle(string artifact, string artifactStore, CancellationToken ct)
    {
        var digest = Digest(artifact);
        var source = Path.Combine(artifactStore, digest);
        await using (var file = File.OpenRead(source))
        {
            var measured = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            if (measured != digest) throw new InvalidDataException("Artifact digest mismatch.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "deploy-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var archive = File.OpenRead(source);
            using var gzip = artifact.StartsWith("release-package@sha256:", StringComparison.Ordinal)
                ? new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true) : null;
            using var reader = new TarReader((Stream?)gzip ?? archive);
            while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
            {
                var relative = entry.Name.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Split('/').Contains("..", StringComparer.Ordinal)
                    || relative.Split('/').SkipLast(1).Any(part => part.Length == 0))
                    throw new InvalidDataException("Unsafe artifact path.");
                var target = Path.Combine(directory, relative);
                if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(target);
                else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var output = File.Create(target);
                    if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, ct);
                }
                else throw new InvalidDataException("Unsupported artifact entry.");
            }
            return directory;
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    private const string RemoteScript = """
        import json, os, shutil, subprocess, sys, tempfile, uuid
        p = json.loads(sys.stdin.readline())
        action = p['action']
        if action == 'prepare':
            os.makedirs(p['path'], exist_ok=True)
        elif action == 'cleanup':
            shutil.rmtree(p['path'], ignore_errors=True)
        elif action == 'switch':
            target = p['path']
            if os.path.exists(p['version']):
                shutil.rmtree(p['staging'])
            else:
                os.rename(p['staging'], p['version'])
            temporary = target + '/.current-' + uuid.uuid4().hex
            os.symlink(p['version'], temporary)
            os.replace(temporary, target + '/current')
        elif action == 'compose':
            variables = p['variables']
            environment = dict(os.environ)
            environment.update(variables)
            with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', suffix='.yml', delete=False) as override:
                os.chmod(override.name, 0o600)
                override.write('services:\n  ' + json.dumps(p['service']) + ':\n    image: ' + json.dumps(p['image']) + '\n')
                overridefile = override.name
            try:
                base = ['docker', 'compose', '-f', p['file'], '-f', overridefile]
                subprocess.run(base + ['pull', p['service']], check=True, timeout=120, env=environment)
                subprocess.run(base + ['up', '-d', '--no-deps', p['service']], check=True, timeout=120, env=environment)
            finally:
                os.unlink(overridefile)
        else:
            raise ValueError('Unknown deploy action')
        """;
}
