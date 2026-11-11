using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Security;

public sealed record HostCertificateRequest(string PublicKey);
public sealed record HostCertificateReceipt(string Id, long Serial, string HostId, string Actor, string Certificate,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, bool Revoked = false);

/// <summary>Signs only unprivileged, forced-command OpenSSH user certificates.</summary>
public sealed class HostCertificateAuthority(IConfiguration configuration, IHttpClientFactory http, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string Principal = "agent-operations";
    private const string ForcedCommand = "/usr/local/libexec/agent-operations-command";

    private string CaFile => configuration["HostCertificates:CaPrivateKeyFile"]
        ?? throw new InvalidOperationException("Host certificate CA is not configured.");
    private string StateDirectory => configuration["HostCertificates:StateDirectory"]
        ?? throw new InvalidOperationException("Host certificate state is not configured.");

    public static string PublicKeyFingerprint(string publicKey)
    {
        var parts = publicKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "ssh-ed25519")
            throw new InvalidDataException("Host CA public key is invalid.");
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[1]))).TrimEnd('=');
    }

    public static string? FingerprintWarning(IConfiguration configuration)
    {
        var ca = configuration["HostCertificates:CaPrivateKeyFile"];
        var state = configuration["HostCertificates:StateDirectory"];
        if (string.IsNullOrWhiteSpace(ca) || string.IsNullOrWhiteSpace(state)) return null;
        var recorded = Path.Combine(state, "ca-fingerprint");
        if (!File.Exists(recorded))
            return File.Exists(Path.Combine(state, "issued.jsonl"))
                ? "The restored host certificate state has no CA fingerprint. Verify the escrowed host CA before issuing certificates."
                : null;
        try
        {
            var current = PublicKeyFingerprint(File.ReadAllText(ca + ".pub"));
            return string.Equals(File.ReadAllText(recorded).Trim(), current, StringComparison.Ordinal)
                ? null : "The configured host CA does not match the restored host certificate state. Install the escrowed CA before issuing certificates.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            return "The configured host CA public key cannot be read. Restore the escrowed host CA before issuing certificates.";
        }
    }

    private void CheckFingerprint()
    {
        if (FingerprintWarning(configuration) is { } warning) throw new InvalidOperationException(warning);
        var recorded = Path.Combine(StateDirectory, "ca-fingerprint");
        if (!File.Exists(recorded))
            File.WriteAllText(recorded, PublicKeyFingerprint(File.ReadAllText(CaFile + ".pub")) + "\n");
    }

    public async Task<HostCertificateReceipt> IssueAsync(string hostId, string actor, string publicKey, CancellationToken ct,
        DateTimeOffset? notAfter = null)
    {
        if (!ValidHostId(hostId) || !ValidActor(actor) || publicKey is null || !ValidPublicKey(publicKey.Trim()))
            throw new InvalidDataException("Host certificate request is invalid.");
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(StateDirectory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(StateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CheckFingerprint();
            if (ReadVersion() == 0) await InitializeRevocationsAsync(ct);
            var id = Guid.NewGuid().ToString("N");
            var issued = clock.GetUtcNow();
            var serialPath = Path.Combine(StateDirectory, "serial-counter");
            var previous = long.TryParse(File.Exists(serialPath) ? File.ReadAllText(serialPath) : null, out var saved) ? saved : 0;
            var serial = Math.Max(checked(previous + 1), issued.ToUnixTimeMilliseconds());
            File.WriteAllText(serialPath, serial.ToString());
            var keyPath = Path.Combine(StateDirectory, id + ".pub");
            var certificatePath = Path.Combine(StateDirectory, id + "-cert.pub");
            var expires = notAfter is { } limit && limit < issued.AddMinutes(59) ? limit : issued.AddMinutes(59);
            if (expires <= issued) throw new InvalidDataException("Host certificate window has ended.");
            static string UtcCertificateTime(DateTimeOffset value) =>
                value.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
            var validity = UtcCertificateTime(issued.AddMinutes(-1)) + ":" + UtcCertificateTime(expires);
            try
            {
                await File.WriteAllTextAsync(keyPath, publicKey.Trim() + "\n", ct);
                await RunKeygenAsync(["-q", "-s", CaFile, "-I", id, "-z", serial.ToString(), "-n", Principal,
                    "-V", validity, "-O", "clear", "-O", "force-command=" + ForcedCommand, keyPath], ct);
                var certificate = await File.ReadAllTextAsync(certificatePath, ct);
                var receipt = new HostCertificateReceipt(id, serial, hostId, actor, certificate, issued, expires);
                await AppendReceiptAsync(receipt, ct);
                return receipt;
            }
            finally { if (File.Exists(keyPath)) File.Delete(keyPath); }
        }
        finally { gate.Release(); }
    }

    public async Task RevokeAsync(string hostId, string id, CancellationToken ct)
    {
        if (!ValidHostId(hostId) || !Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("Host certificate identity is invalid.");
        await gate.WaitAsync(ct);
        try
        {
            CheckFingerprint();
            var receipts = ReadReceipts();
            if (!receipts.Any(item => item.Id == id && item.HostId == hostId))
                throw new FileNotFoundException("Host certificate was not issued for this host.");
            var revoked = ReadRevoked();
            if (revoked.Add(id))
                await File.AppendAllTextAsync(Path.Combine(StateDirectory, "revoked.jsonl"),
                    JsonSerializer.Serialize(new { id, at = clock.GetUtcNow() }, OperationsProtocol.Json) + "\n", ct);
            var version = ReadVersion() + 1;
            var temporary = Path.Combine(StateDirectory, "revoked-" + Guid.NewGuid().ToString("N") + ".krl");
            try
            {
                var args = new List<string> { "-q", "-k", "-f", temporary, "-z", version.ToString(),
                    "-s", CaFile + ".pub" };
                foreach (var revokedId in revoked.Order(StringComparer.Ordinal))
                    args.Add(Path.Combine(StateDirectory, revokedId + "-cert.pub"));
                await RunKeygenAsync(args, ct);
                var bytes = await File.ReadAllBytesAsync(temporary, ct);
                File.WriteAllText(Path.Combine(StateDirectory, "revocation-version"), version.ToString());
                await PublishAsync(new HostRevocationList(version,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToBase64String(bytes)), ct);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task PublishAsync(HostRevocationList list, CancellationToken ct)
    {
        var endpoint = configuration["Operations:Url"];
        var tokenFile = configuration["Operations:ServiceTokenFile"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(tokenFile))
            throw new IOException("Operations Server revocation channel is unavailable.");
        using var client = http.CreateClient("operations-server");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await File.ReadAllTextAsync(tokenFile, ct)).Trim());
        client.DefaultRequestHeaders.Add(OperationsProtocol.Header, OperationsProtocol.Version.ToString());
        using var response = await client.PutAsJsonAsync(new Uri(uri, "/api/operations/v1/certificates/revocations"), list,
            OperationsProtocol.Json, ct);
        if (!response.IsSuccessStatusCode) throw new IOException("Operations Server refused the revocation list.");
    }

    private async Task InitializeRevocationsAsync(CancellationToken ct)
    {
        var temporary = Path.Combine(StateDirectory, "revoked-" + Guid.NewGuid().ToString("N") + ".krl");
        try
        {
            await RunKeygenAsync(["-q", "-k", "-f", temporary, "-z", "1"], ct);
            var bytes = await File.ReadAllBytesAsync(temporary, ct);
            await PublishAsync(new HostRevocationList(1,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToBase64String(bytes)), ct);
            File.WriteAllText(Path.Combine(StateDirectory, "revocation-version"), "1");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private IReadOnlyList<HostCertificateReceipt> ReadReceipts()
    {
        var path = Path.Combine(StateDirectory, "issued.jsonl");
        if (File.Exists(path) && new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new IOException("Host certificate receipt capacity reached.");
        return File.Exists(path) ? File.ReadAllLines(path).Select(line =>
            JsonSerializer.Deserialize<HostCertificateReceipt>(line, OperationsProtocol.Json)!).ToArray() : [];
    }

    private HashSet<string> ReadRevoked()
    {
        var path = Path.Combine(StateDirectory, "revoked.jsonl");
        if (File.Exists(path) && new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new IOException("Host certificate revocation capacity reached.");
        return File.Exists(path) ? File.ReadAllLines(path).Select(line =>
            JsonDocument.Parse(line).RootElement.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private long ReadVersion() => long.TryParse(File.Exists(Path.Combine(StateDirectory, "revocation-version"))
        ? File.ReadAllText(Path.Combine(StateDirectory, "revocation-version")) : null, out var value) ? value : 0;

    private async Task AppendReceiptAsync(HostCertificateReceipt receipt, CancellationToken ct)
    {
        var path = Path.Combine(StateDirectory, "issued.jsonl");
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(receipt, OperationsProtocol.Json) + "\n"), ct);
        stream.Flush(flushToDisk: true);
    }

    private static async Task RunKeygenAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("OpenSSH key generator is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await output;
            var message = await error;
            if (process.ExitCode != 0) throw new IOException("OpenSSH certificate operation failed: "
                + message[..Math.Min(200, message.Length)]);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static bool ValidHostId(string value) => value is { Length: >= 1 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool ValidActor(string value) => value is { Length: >= 1 and <= 256 } && !value.Any(char.IsControl);
    private static bool ValidPublicKey(string value) => value is { Length: >= 80 and <= 2048 }
        && value.StartsWith("ssh-ed25519 ", StringComparison.Ordinal)
        && !value.Contains('\n') && !value.Contains('\r');
}
