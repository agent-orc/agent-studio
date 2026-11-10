using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Security;

namespace AgentStudio.Management;

/// <summary>One archive found at the off-host target.</summary>
public sealed record BackupTargetFile(string Name, long SizeBytes, string ModifiedAt);

/// <summary>
/// The off-host copy of every backup. A target receives the encrypted archive
/// and its <c>.sha256</c> file; it never needs read, list or delete rights,
/// and retention at the target is configured there.
/// </summary>
public interface IBackupTarget
{
    string Kind { get; }

    /// <summary>Where copies go, without any credential (shown on the Operations page).</summary>
    string Description { get; }

    /// <summary>Uploads <paramref name="localPath"/> as <paramref name="remoteName"/>; completes only when the copy is whole.</summary>
    Task UploadAsync(string localPath, string remoteName, CancellationToken cancellationToken);

    /// <summary>
    /// The archives at the target (AGT-3734), or null when this host may not
    /// list it: SFTP and S3 targets are write-only for the host by design.
    /// </summary>
    IReadOnlyList<BackupTargetFile>? ListArchives() => null;
}

/// <summary>Runs a non-Git helper process (sftp) with a bounded time.</summary>
public interface IBackupProcessRunner
{
    Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start, string? standardInput, TimeSpan timeout,
        CancellationToken cancellationToken);
}

public static class BackupTargets
{
    public static IBackupTarget? Create(BackupTargetOptions options, Func<HttpClient> http,
        IBackupProcessRunner processes, TimeProvider clock)
        => options.Kind switch
        {
            "none" or "" => null,
            "directory" => new DirectoryBackupTarget(options.Directory
                ?? throw new BackupException("backup-target-invalid", "Backup:Target:Directory is required for a directory target.")),
            "sftp" => new SftpBackupTarget(options, processes),
            "s3" => new S3BackupTarget(options, http, clock),
            _ => throw new BackupException("backup-target-invalid", $"Unknown backup target kind '{options.Kind}'."),
        };

    internal static void ValidateRemoteName(string name)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new BackupException("backup-target-invalid", $"Backup name '{name}' cannot be uploaded.");
    }

    internal static string ReadSecretFile(string? path, string setting)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new BackupException("backup-target-credentials-missing", $"{setting} must name an existing file.");
        var value = File.ReadAllText(path).Trim();
        if (value.Length == 0) throw new BackupException("backup-target-credentials-missing", $"{setting} is empty.");
        CredentialRedactor.RegisterLiteral(value, pinned: true);
        return value;
    }
}

/// <summary>
/// A mounted remote file system (NFS, SMB, sshfs) or another disk. The
/// directory must exist: a missing mount point must not silently become a
/// local copy. Files are written under a temporary name and renamed.
/// </summary>
public sealed class DirectoryBackupTarget(string directory) : IBackupTarget
{
    public string Kind => "directory";
    public string Description => directory;

    public async Task UploadAsync(string localPath, string remoteName, CancellationToken cancellationToken)
    {
        BackupTargets.ValidateRemoteName(remoteName);
        if (!Directory.Exists(directory))
            throw new BackupException("backup-target-unreachable", $"The backup target directory {directory} does not exist.");
        var final = Path.Combine(directory, remoteName);
        var partial = final + ".partial";
        await using (var from = File.OpenRead(localPath))
        await using (var to = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
            await to.FlushAsync(cancellationToken).ConfigureAwait(false);
            to.Flush(flushToDisk: true);
        }
        File.Move(partial, final, overwrite: true);
    }

    /// <summary>A mount the host writes to can also be read: lists complete archives, not <c>.partial</c> leftovers.</summary>
    public IReadOnlyList<BackupTargetFile>? ListArchives()
    {
        if (!Directory.Exists(directory))
            throw new BackupException("backup-target-unreachable", $"The backup target directory {directory} does not exist.");
        return new DirectoryInfo(directory).EnumerateFiles("studio-backup-*" + BackupCatalog.ArchiveSuffix)
            .Select(file => new BackupTargetFile(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToString("O")))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// SFTP or SSH host through the OpenSSH <c>sftp</c> client in batch mode. The
/// host key must be pinned in a known-hosts file; there is no trust on first
/// use. The account needs only write and rename rights in its directory.
/// </summary>
public sealed class SftpBackupTarget(BackupTargetOptions options, IBackupProcessRunner processes) : IBackupTarget
{
    public static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    public string Kind => "sftp";

    public string Description => $"sftp://{options.User}@{options.Host}:{options.Port}/{(options.RemotePath ?? "").TrimStart('/')}";

    public async Task UploadAsync(string localPath, string remoteName, CancellationToken cancellationToken)
    {
        BackupTargets.ValidateRemoteName(remoteName);
        var start = BuildStartInfo();
        var (exitCode, output) = await processes.RunAsync(start, BatchScript(localPath, remoteName), Timeout, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0)
            throw new BackupException("backup-target-upload-failed",
                $"sftp exited with {exitCode}: {CredentialRedactor.Redact(Tail(output))}");
    }

    public ProcessStartInfo BuildStartInfo()
    {
        if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.User))
            throw new BackupException("backup-target-invalid", "Backup:Target:Host and Backup:Target:User are required for an sftp target.");
        if (options.IdentityFile is null || !File.Exists(options.IdentityFile))
            throw new BackupException("backup-target-credentials-missing", "Backup:Target:IdentityFile must name an existing SSH key.");
        if (options.KnownHostsFile is null || !File.Exists(options.KnownHostsFile))
            throw new BackupException("backup-target-credentials-missing",
                "Backup:Target:KnownHostsFile must name a file with the pinned host key.");
        var start = new ProcessStartInfo("sftp")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-b", "-",
                     "-o", "BatchMode=yes",
                     "-o", "StrictHostKeyChecking=yes",
                     "-o", "UserKnownHostsFile=" + options.KnownHostsFile,
                     "-o", "IdentitiesOnly=yes",
                     "-o", "ConnectTimeout=30",
                     "-o", "ServerAliveInterval=30",
                     "-i", options.IdentityFile,
                     "-P", options.Port.ToString(CultureInfo.InvariantCulture),
                     $"{options.User}@{options.Host}",
                 })
            start.ArgumentList.Add(argument);
        return start;
    }

    public string BatchScript(string localPath, string remoteName)
    {
        if (localPath.IndexOfAny(['"', '\n', '\r']) >= 0)
            throw new BackupException("backup-target-invalid", "The local backup path cannot be passed to sftp.");
        var directory = (options.RemotePath ?? "").Trim().TrimEnd('/');
        if (directory.IndexOfAny(['"', '\n', '\r']) >= 0)
            throw new BackupException("backup-target-invalid", "Backup:Target:Path cannot be passed to sftp.");
        var remote = directory.Length == 0 ? remoteName : directory + "/" + remoteName;
        // "-rm" ignores a missing leftover of an earlier, interrupted upload.
        return $"-rm \"{remote}.partial\"\nput \"{localPath}\" \"{remote}.partial\"\nrename \"{remote}.partial\" \"{remote}\"\n";
    }

    private static string Tail(string text) => text.Length <= 600 ? text.Trim() : text[^600..].Trim();
}

/// <summary>
/// An S3-compatible bucket through a single signed PUT (AWS Signature
/// Version 4). The access key needs only <c>s3:PutObject</c> on the prefix;
/// enable versioning or object lock at the bucket so this host cannot remove
/// older copies. A single PUT carries up to 5 GiB.
/// </summary>
public sealed class S3BackupTarget(BackupTargetOptions options, Func<HttpClient> http, TimeProvider clock) : IBackupTarget
{
    public const long MaxSinglePut = 5L * 1024 * 1024 * 1024;

    public string Kind => "s3";

    public string Description => $"s3://{options.Bucket}/{Prefix()} at {options.Endpoint}";

    public async Task UploadAsync(string localPath, string remoteName, CancellationToken cancellationToken)
    {
        BackupTargets.ValidateRemoteName(remoteName);
        var length = new FileInfo(localPath).Length;
        if (length > MaxSinglePut)
            throw new BackupException("backup-target-upload-failed", "The archive exceeds the 5 GiB single upload limit of S3.");
        string payloadHash;
        await using (var hashed = File.OpenRead(localPath))
            payloadHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashed, cancellationToken).ConfigureAwait(false));
        var accessKey = BackupTargets.ReadSecretFile(options.AccessKeyIdFile, "Backup:Target:AccessKeyIdFile");
        var secret = BackupTargets.ReadSecretFile(options.SecretAccessKeyFile, "Backup:Target:SecretAccessKeyFile");

        await using var body = File.OpenRead(localPath);
        using var request = BuildRequest(remoteName, payloadHash, accessKey, secret, clock.GetUtcNow(), new StreamContent(body));
        request.Content!.Headers.ContentLength = length;
        using var response = await http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new BackupException("backup-target-upload-failed",
                $"S3 PUT answered {(int)response.StatusCode}: {CredentialRedactor.Redact(text.Length > 400 ? text[..400] : text)}");
        }
    }

    /// <summary>Builds the signed PUT. Public for the signature tests.</summary>
    public HttpRequestMessage BuildRequest(string remoteName, string payloadHash, string accessKey, string secret,
        DateTimeOffset now, HttpContent content)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Bucket))
            throw new BackupException("backup-target-invalid", "Backup:Target:Endpoint and Backup:Target:Bucket are required for an s3 target.");
        var endpoint = new Uri(options.Endpoint.TrimEnd('/'));
        var key = Prefix().Length == 0 ? remoteName : Prefix() + "/" + remoteName;
        var encodedKey = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        var hostName = options.PathStyle ? endpoint.Host : options.Bucket + "." + endpoint.Host;
        var host = endpoint.IsDefaultPort ? hostName : $"{hostName}:{endpoint.Port}";
        var canonicalUri = options.PathStyle ? $"/{Uri.EscapeDataString(options.Bucket)}/{encodedKey}" : "/" + encodedKey;
        var uri = new Uri($"{endpoint.Scheme}://{host}{canonicalUri}");

        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = amzDate[..8];
        var region = options.Region ?? "us-east-1";
        var scope = $"{day}/{region}/s3/aws4_request";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = string.Join('\n',
            "PUT", canonicalUri, "",
            $"host:{host}", $"x-amz-content-sha256:{payloadHash}", $"x-amz-date:{amzDate}", "",
            signedHeaders, payloadHash);
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256", amzDate, scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), day), region), "s3"), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(signingKey, stringToSign));

        var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            $"Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private string Prefix() => (options.Prefix ?? "").Trim('/');

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}

/// <summary>Starts the helper with a bounded time and kills its tree on timeout or cancellation.</summary>
public sealed class BackupProcessRunner : IBackupProcessRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start, string? standardInput,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        start.CreateNoWindow = true;
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => { if (line.Data is not null) lock (output) output.AppendLine(line.Data); };
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (output) output.AppendLine(line.Data); };
        if (!process.Start()) throw new BackupException("backup-target-upload-failed", $"{start.FileName} could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (start.RedirectStandardInput)
        {
            if (standardInput is not null) await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try { await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException ex) { SilentCatch.Note(ex, "Backup helper exited before the kill"); }
            cancellationToken.ThrowIfCancellationRequested();
            throw new BackupException("backup-target-upload-failed", $"{start.FileName} did not finish within {timeout}.");
        }
        lock (output) return (process.ExitCode, output.ToString());
    }
}
