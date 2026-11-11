using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Secrets;
using AgentStudio.Security;

namespace AgentStudio.Management;

/// <summary>
/// Offline backup tools of the API binary, run without starting the host
/// (in the stack: <c>restore-backup.sh</c> runs them in a one-shot API
/// container):
/// <code>
/// --studio-backup key-create &lt;file&gt;
/// --studio-backup key-id &lt;file&gt;
/// --studio-backup inspect --key &lt;file&gt; --archive &lt;file&gt;
/// --studio-backup decrypt --key &lt;file&gt; --archive &lt;file&gt; --output &lt;file.tar.gz&gt;
/// --studio-backup restore --key &lt;file&gt; --archive &lt;file&gt; --target &lt;source&gt;=&lt;dir&gt; ...
///                         [--secret-key &lt;escrowed copy&gt; --secret-key-target &lt;path&gt;] [--restored-by &lt;name&gt;] [--no-clone]
/// </code>
/// A restore writes a receipt into the restored workspace (AGT-3734,
/// <see cref="BackupRestoreReceipts"/>): the Operations page and the
/// management audit show which archive came back, from when, and by whom.
/// <c>decrypt</c> and <c>restore</c> also take <c>--actor &lt;name&gt;</c>
/// (default <c>offline:&lt;account&gt;</c>) and <c>--audit-workspace &lt;dir&gt;</c>,
/// and append their outcome to that workspace's control plane audit chain
/// (AGT-3737). Without the option a successful restore records into the
/// restored <c>workspace</c> target, otherwise into the configured
/// <c>TaskRepository</c> when it exists. A failed restore never writes into
/// one of its targets: they must stay empty for the next attempt.
/// </summary>
public static class StudioBackupCommand
{
    public const string Flag = "--studio-backup";

    public static int Run(string[] args, TextWriter output, TextWriter error, IConfiguration? configuration = null,
        TimeProvider? clock = null)
    {
        try
        {
            return args switch
            {
                ["key-create", var path] => KeyCreate(path, output),
                ["key-id", var path] => Print(output, BackupKey.Load(path).Id),
                ["inspect", .. var rest] => Inspect(Options(rest), output),
                ["decrypt", .. var rest] => Audited(BackupAudit.Decrypt, Options(rest), output, error, configuration, Decrypt),
                ["restore", .. var rest] => Audited(BackupAudit.Restore, Options(rest), output, error, configuration,
                    (options, writer) => Restore(options, writer, clock ?? TimeProvider.System)),
                _ => Usage(error),
            };
        }
        catch (BackupException ex)
        {
            error.WriteLine($"{ex.Code}: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretKeyUnavailableException
                                       or ArgumentException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int KeyCreate(string path, TextWriter output)
    {
        if (File.Exists(path)) throw new BackupException("backup-key-exists", $"{path} already exists; it is never overwritten.");
        BackupKey.Create(path);
        output.WriteLine($"Created backup key {BackupKey.Load(path).Id} at {path}. Escrow a copy off this host.");
        return 0;
    }

    private static int Inspect(Dictionary<string, List<string>> options, TextWriter output)
    {
        var (archive, key) = Open(options);
        using var stream = File.OpenRead(archive);
        var (manifest, contents) = BackupArchive.Read(stream, key, null, null, null);
        output.WriteLine(JsonSerializer.Serialize(new { manifest, files = contents.Files.Count, contents.Consistency },
            BackupArchive.Json));
        return 0;
    }

    private static int Audited(string operation, Dictionary<string, List<string>> options, TextWriter output,
        TextWriter error, IConfiguration? configuration,
        Func<Dictionary<string, List<string>>, TextWriter, string> run)
    {
        var actor = options.GetValueOrDefault("actor") is [var named] && named.Trim().Length > 0
            ? named.Trim()
            : "offline:" + Environment.UserName;
        var archive = options.GetValueOrDefault("archive") is [var path] ? Path.GetFileName(path) : null;
        string summary;
        try { summary = run(options, output); }
        catch (Exception ex)
        {
            Record(operation, options, configuration, error, succeeded: false,
                BackupAudit.OfflineRecord(actor, operation, BackupAudit.Failed, archive,
                    $"Offline {Verb(operation)} failed: {ex.Message}", new { code = (ex as BackupException)?.Code }));
            throw;
        }
        Record(operation, options, configuration, error, succeeded: true,
            BackupAudit.OfflineRecord(actor, operation, BackupAudit.Succeeded, archive, summary));
        return 0;
    }

    private static string Verb(string operation) => operation == BackupAudit.Restore ? "restore" : "decrypt";

    private static void Record(string operation, Dictionary<string, List<string>> options, IConfiguration? configuration,
        TextWriter error, bool succeeded, ControlPlaneAuditRecord record)
    {
        var workspace = AuditWorkspace(operation, options, configuration, succeeded);
        if (workspace is null)
        {
            error.WriteLine($"warning: the offline {Verb(operation)} was not recorded in an audit chain; "
                            + "pass --audit-workspace <dir> to record it.");
            return;
        }
        try { new ControlPlaneAuditLog(Path.Combine(workspace, ".security")).Append(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"warning: the offline {Verb(operation)} could not be recorded in the audit chain: {ex.Message}");
        }
    }

    private static string? AuditWorkspace(string operation, Dictionary<string, List<string>> options,
        IConfiguration? configuration, bool succeeded)
    {
        var targets = operation == BackupAudit.Restore ? Targets(options, strict: false) : [];
        string? workspace;
        if (options.GetValueOrDefault("audit-workspace") is [var named]) workspace = named;
        else if (succeeded && targets.TryGetValue("workspace", out var restored)) workspace = restored;
        else
        {
            workspace = (configuration ?? OperatorKillSwitchCommand.BuildConfiguration([]))["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return null;
        }
        if (!succeeded && targets.Values.Any(target => Inside(workspace, target))) return null;
        return workspace;
    }

    private static bool Inside(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full == parent || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Targets(Dictionary<string, List<string>> options, bool strict)
    {
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in options.GetValueOrDefault("target") ?? [])
        {
            var equals = mapping.IndexOf('=');
            if (equals <= 0)
            {
                if (strict) throw new ArgumentException($"--target expects <source>=<directory>, got '{mapping}'.");
                continue;
            }
            destinations[mapping[..equals]] = mapping[(equals + 1)..];
        }
        return destinations;
    }

    private static string Decrypt(Dictionary<string, List<string>> options, TextWriter output)
    {
        var (archive, key) = Open(options);
        var target = Single(options, "output");
        if (File.Exists(target)) throw new BackupException("decrypt-output-exists", $"{target} already exists.");
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        OwnerOnlyAccess.ApplyCreateMode(fileOptions);
        using (var input = File.OpenRead(archive))
        using (var plain = BackupCipher.Decrypt(input, key))
        using (var file = new FileStream(target, fileOptions))
            plain.CopyTo(file);
        output.WriteLine($"Decrypted to {target} (gzip tar). It holds credential material: delete it when done.");
        return $"Decrypted {Path.GetFileName(archive)} to a plain gzip tar outside the server.";
    }

    private static string Restore(Dictionary<string, List<string>> options, TextWriter output, TimeProvider clock)
    {
        var (archive, key) = Open(options);
        var destinations = Targets(options, strict: true);
        if (destinations.Count == 0) throw new ArgumentException("restore needs at least one --target <source>=<directory>.");

        // Read the manifest first, so a wrong secret key copy is refused before anything is written.
        BackupManifest? manifest = null;
        try
        {
            using var peek = File.OpenRead(archive);
            BackupArchive.Read(peek, key, onManifest: value => { manifest = value; throw new StopAfterManifest(); }, null, null);
        }
        catch (StopAfterManifest ex) { SilentCatch.Note(ex, "Restore read the manifest first"); }
        if (manifest is null) throw new BackupException("backup-corrupt", "The archive has no manifest.");

        byte[]? secretMaterial = null;
        string? secretCopyId = null;
        var secretTarget = options.GetValueOrDefault("secret-key-target")?.SingleOrDefault();
        if (options.GetValueOrDefault("secret-key")?.SingleOrDefault() is { } secretCopy)
        {
            secretMaterial = Convert.FromBase64String(File.ReadAllText(secretCopy).Trim());
            var copyId = secretCopyId = new KeyEncryptionKey(secretMaterial, "restore").Id;
            if (manifest.SecretKeyId is not null && copyId != manifest.SecretKeyId)
                throw new BackupException("restore-secret-key-mismatch",
                    $"The secret store key copy has fingerprint {copyId}; the backup was made with {manifest.SecretKeyId}.");
            if (secretTarget is not null && File.Exists(secretTarget))
                throw new BackupException("restore-target-not-empty", $"{secretTarget} already exists.");
        }
        else if (manifest.SecretKeyId is not null)
        {
            output.WriteLine($"warning: no --secret-key given; sealed secrets need the escrowed key {manifest.SecretKeyId}.");
        }

        var result = BackupRestore.Restore(archive, key,
            new BackupRestorePlan(destinations, CloneCheckouts: !options.ContainsKey("no-clone")), output.WriteLine);
        if (secretMaterial is not null && secretTarget is not null)
        {
            OwnerOnlyAccess.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(secretTarget))!);
            using var stream = OwnerOnlyAccess.CreateNewFile(secretTarget);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(secretMaterial)));
            CryptographicOperations.ZeroMemory(secretMaterial);
            output.WriteLine($"Installed the secret store key at {secretTarget}.");
        }
        var summary = $"Restored backup {result.Manifest.Id}: {result.FilesRestored} files, "
                      + $"{result.CheckoutsCloned} checkouts cloned, skipped sources: "
                      + (result.SkippedSources.Count == 0 ? "none" : string.Join(", ", result.SkippedSources)) + ".";
        output.WriteLine(summary);
        WriteReceipt(options, destinations, archive, key, secretCopyId, result, clock, output);
        return summary;
    }

    /// <summary>
    /// The restore is complete when this runs, so a receipt that cannot be
    /// written is a warning, not a failed restore: the targets stay restored.
    /// </summary>
    private static void WriteReceipt(Dictionary<string, List<string>> options, Dictionary<string, string> destinations,
        string archive, BackupKey key, string? secretCopyId, BackupRestoreResult result, TimeProvider clock, TextWriter output)
    {
        if (!destinations.TryGetValue("workspace", out var workspace))
        {
            output.WriteLine("warning: no workspace target, so no restore receipt was written.");
            return;
        }
        var restoredBy = (options.GetValueOrDefault("restored-by") ?? options.GetValueOrDefault("actor"))?.SingleOrDefault() is { Length: > 0 } named
            ? named.Trim()
            : $"{Environment.UserName}@{Environment.MachineName}";
        var manifest = result.Manifest;
        try
        {
            string sha256;
            using (var stream = File.OpenRead(archive)) sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            var receipt = new BackupRestoreReceipt(manifest.Id, Path.GetFileName(archive), sha256, manifest.CreatedAt, manifest.Host,
                manifest.StudioVersion, clock.GetUtcNow().ToString("O"), restoredBy, key.Id, manifest.SecretKeyId, secretCopyId,
                result.FilesRestored, result.CheckoutsCloned, result.SkippedSources);
            BackupRestoreReceipts.Write(Path.GetFullPath(workspace), receipt);
            output.WriteLine($"Wrote the restore receipt to {BackupRestoreReceipts.ReceiptPath(workspace)}. "
                             + $"Work after {manifest.CreatedAt} is not in this installation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"warning: the restore receipt could not be written: {ex.Message}");
        }
    }

    private static (string Archive, BackupKey Key) Open(Dictionary<string, List<string>> options)
    {
        var archive = Single(options, "archive");
        var key = BackupKey.Load(Single(options, "key"));
        var checksum = archive + ".sha256";
        if (File.Exists(checksum))
        {
            var expected = File.ReadAllText(checksum).Split(' ', 2)[0].Trim();
            using var stream = File.OpenRead(archive);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new BackupException("backup-checksum-mismatch", $"{archive} does not match {checksum}.");
        }
        return (archive, key);
    }

    private static Dictionary<string, List<string>> Options(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            var name = args[i][2..];
            if (name == "no-clone") { options[name] = []; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"--{name} needs a value.");
            if (!options.TryGetValue(name, out var values)) options[name] = values = [];
            values.Add(args[++i]);
        }
        return options;
    }

    private static string Single(Dictionary<string, List<string>> options, string name)
        => options.GetValueOrDefault(name) is [var value] ? value : throw new ArgumentException($"--{name} is required once.");

    private static int Print(TextWriter output, string text)
    {
        output.WriteLine(text);
        return 0;
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("usage: --studio-backup key-create <file> | key-id <file> | inspect --key <file> --archive <file>");
        error.WriteLine("       | decrypt --key <file> --archive <file> --output <file> [--actor <name>] [--audit-workspace <dir>]");
        error.WriteLine("       | restore --key <file> --archive <file> --target <source>=<dir>... [--secret-key <file> --secret-key-target <path>]");
        error.WriteLine("         [--restored-by <name>] [--no-clone] [--actor <name>] [--audit-workspace <dir>]");
        return 2;
    }

    private sealed class StopAfterManifest : Exception;
}
