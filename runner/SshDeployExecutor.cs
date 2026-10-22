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
    public static readonly TimeSpan RemoteCommandBudget = TimeSpan.FromSeconds(240);
    public static readonly TimeSpan StepCap = TimeSpan.FromMinutes(5);
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
        limit.CancelAfter(StepCap);
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
                        await Remote(host, new { action = "prepare", path = staging }, limit.Token, log);
                        await Command("scp", ["-r", .. SshOptions(), source + "/.", host + ":" + staging], null,
                            limit.Token, log);
                        await Remote(host, new { action = "switch", path = step.Directory, staging, version }, limit.Token, log);
                    }
                    catch
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        await Remote(host, new { action = "cleanup", path = staging }, cleanup.Token, log);
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
                    || IsReservedVariable(item.Key)
                    || item.Value.Contains('\n') || item.Value.Contains('\r')))
                    throw new InvalidOperationException("Invalid deploy variable.");
                Digest(artifact);
                await Remote(host, new { action = "compose", file = step.ComposeFile, service = step.Service,
                    image = artifact, variables }, limit.Token, log, variables);
            }
            await log($"Completed {step.Step} on {host}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("SSH deploy step timed out.");
        }
    }

    private static bool IsReservedVariable(string name)
        => name is "PATH" or "HOME" or "TMPDIR" or "BASH_ENV" or "ENV" or "IFS"
            || name.StartsWith("COMPOSE_", StringComparison.Ordinal)
            || name.StartsWith("DOCKER_", StringComparison.Ordinal)
            || name.StartsWith("LD_", StringComparison.Ordinal)
            || name.StartsWith("XDG_", StringComparison.Ordinal);

    private async Task Remote(string host, object payload, CancellationToken ct, Func<string, Task> log,
        IReadOnlyDictionary<string, string>? secrets = null)
    {
        // The base64 script is constant. SSH invokes a remote shell, but no value from the repository or job
        // appears in its command. The helper parses one JSON line and calls OS programs with argument arrays.
        var script = Convert.ToBase64String(Encoding.UTF8.GetBytes(RemoteScript));
        var command = "python3 -c \"exec(__import__('base64').b64decode('" + script + "'))\"";
        await Command("ssh", [.. SshOptions(), host, command], JsonSerializer.Serialize(payload) + "\n", ct,
            log, secrets);
    }

    private static string[] SshOptions() => ["-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=10", "-o", "ForwardAgent=no"];

    private async Task Command(string file, IReadOnlyList<string> args, string? input, CancellationToken ct,
        Func<string, Task> log,
        IReadOnlyDictionary<string, string>? secrets = null)
    {
        var result = await _run(file, args, input, ct);
        var tail = result.StdErr.Length > 400 ? result.StdErr[^400..] : result.StdErr;
        foreach (var secret in secrets?.Values ?? [])
            if (!string.IsNullOrEmpty(secret)) tail = tail.Replace(secret, "[masked]", StringComparison.Ordinal);
        tail = new string(tail.Where(ch => !char.IsControl(ch)).ToArray());
        if (tail.Length > 0) await log($"{file} stderr: {tail}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed with exit code {result.ExitCode}: {tail}");
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
        import fcntl, hashlib, json, os, shutil, subprocess, sys, tempfile, time, uuid
        p = json.loads(sys.stdin.readline())
        action = p['action']
        def locked(key):
            digest = hashlib.sha256(key.encode('utf-8')).hexdigest()
            handle = open('/tmp/agent-deploy-' + digest + '.lock', 'a+')
            fcntl.flock(handle, fcntl.LOCK_EX)
            return handle
        if action == 'prepare':
            os.makedirs(p['path'], exist_ok=True)
        elif action == 'cleanup':
            shutil.rmtree(p['path'], ignore_errors=True)
        elif action == 'switch':
            target = p['path']
            with locked('static:' + target):
                if os.path.exists(p['version']):
                    shutil.rmtree(p['staging'])
                else:
                    os.rename(p['staging'], p['version'])
                current = target + '/current'
                temporary = target + '/.current-' + uuid.uuid4().hex
                previous = None
                os.symlink(p['version'], temporary)
                if os.path.isdir(current) and not os.path.islink(current):
                    previous = target + '/.old-current-' + uuid.uuid4().hex
                    os.rename(current, previous)
                try:
                    os.replace(temporary, current)
                except Exception:
                    if previous is not None:
                        os.rename(previous, current)
                    raise
                if previous is not None:
                    shutil.rmtree(previous)
                releases = target + '/releases'
                keep = {os.path.realpath(current)}
                entries = sorted((entry for entry in os.scandir(releases) if entry.is_dir(follow_symlinks=False)
                    and '.tmp-' not in entry.name), key=lambda entry: entry.stat().st_mtime, reverse=True)
                keep.update(entry.path for entry in entries[:3])
                for entry in entries:
                    if entry.path not in keep:
                        shutil.rmtree(entry.path)
                cutoff = time.time() - 3600
                for directory in (target, releases):
                    for entry in os.scandir(directory):
                        if (entry.name.startswith('.current-') or entry.name.startswith('.old-current-')
                            or '.tmp-' in entry.name) and entry.stat(follow_symlinks=False).st_mtime < cutoff:
                            if entry.is_dir(follow_symlinks=False):
                                shutil.rmtree(entry.path)
                            else:
                                os.unlink(entry.path)
        elif action == 'compose':
            variables = p['variables']
            environment = {'PATH': '/usr/bin:/bin', 'HOME': os.path.expanduser('~'), 'LANG': 'C.UTF-8'}
            environment.update(variables)
            with locked('compose:' + p['file'] + ':' + p['service']):
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
