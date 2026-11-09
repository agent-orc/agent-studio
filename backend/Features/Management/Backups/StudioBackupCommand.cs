using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Secrets;

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
///                         [--secret-key &lt;escrowed copy&gt; --secret-key-target &lt;path&gt;] [--no-clone]
/// </code>
/// </summary>
public static class StudioBackupCommand
{
    public const string Flag = "--studio-backup";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            return args switch
            {
                ["key-create", var path] => KeyCreate(path, output),
                ["key-id", var path] => Print(output, BackupKey.Load(path).Id),
                ["inspect", .. var rest] => Inspect(Options(rest), output),
                ["decrypt", .. var rest] => Decrypt(Options(rest), output),
                ["restore", .. var rest] => Restore(Options(rest), output),
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

    private static int Decrypt(Dictionary<string, List<string>> options, TextWriter output)
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
        return 0;
    }

    private static int Restore(Dictionary<string, List<string>> options, TextWriter output)
    {
        var (archive, key) = Open(options);
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in options.GetValueOrDefault("target") ?? [])
        {
            var equals = mapping.IndexOf('=');
            if (equals <= 0) throw new ArgumentException($"--target expects <source>=<directory>, got '{mapping}'.");
            destinations[mapping[..equals]] = mapping[(equals + 1)..];
        }
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
        var secretTarget = options.GetValueOrDefault("secret-key-target")?.SingleOrDefault();
        if (options.GetValueOrDefault("secret-key")?.SingleOrDefault() is { } secretCopy)
        {
            secretMaterial = Convert.FromBase64String(File.ReadAllText(secretCopy).Trim());
            var copyId = new KeyEncryptionKey(secretMaterial, "restore").Id;
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
        output.WriteLine($"Restored backup {result.Manifest.Id}: {result.FilesRestored} files, "
                         + $"{result.CheckoutsCloned} checkouts cloned, skipped sources: "
                         + (result.SkippedSources.Count == 0 ? "none" : string.Join(", ", result.SkippedSources)) + ".");
        return 0;
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
        error.WriteLine("       | decrypt --key <file> --archive <file> --output <file>");
        error.WriteLine("       | restore --key <file> --archive <file> --target <source>=<dir>... [--secret-key <file> --secret-key-target <path>] [--no-clone]");
        return 2;
    }

    private sealed class StopAfterManifest : Exception;
}
