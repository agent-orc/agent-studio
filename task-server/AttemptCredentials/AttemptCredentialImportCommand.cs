using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.AttemptCredentials;

/// <summary>Offline root import; the input is a protected tmpfs file, never argv or environment material.</summary>
public static class AttemptCredentialImportCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            if (args.Length != 9 || args[0] != "import-github-app")
                throw new ArgumentException("Usage: task-server attempt-credentials import-github-app --store-dir <dir> --wrapping-key-file <file> --repository-key <id> --root-json-file <tmpfs-file>");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index += 2)
                options.Add(args[index], args[index + 1]);
            var storeDirectory = Path.GetFullPath(options["--store-dir"]);
            var keyFile = Path.GetFullPath(options["--wrapping-key-file"]);
            var input = Path.GetFullPath(options["--root-json-file"]);
            var repositoryKey = options["--repository-key"];
            if (!OperatingSystem.IsWindows() && !input.StartsWith("/dev/shm/", StringComparison.Ordinal))
                throw new ArgumentException("GitHub App root import requires a protected /dev/shm input file on Linux.");
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(input) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new ArgumentException("GitHub App root import file must be mode 0600 or stricter.");
            var json = await File.ReadAllTextAsync(input, ct);
            var root = JsonSerializer.Deserialize<GitHubAppRoot>(json)
                ?? throw new InvalidDataException("GitHub App root is empty.");
            if (root.AppId <= 0 || root.InstallationId <= 0 || root.RepositoryId <= 0
                || !root.RepositoryUrl.StartsWith("https://github.com/", StringComparison.Ordinal)
                || !root.RepositoryUrl.EndsWith(".git", StringComparison.Ordinal))
                throw new InvalidDataException("GitHub App root has invalid scope.");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(root.PrivateKeyPem);
            var name = "github-app-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(repositoryKey))).ToLowerInvariant();
            var store = new EncryptedAttemptRootSecretStore(
                Path.Combine(storeDirectory, "attempt-root-secrets"), keyFile);
            await store.WriteAsync(name, json, ct);
            Console.WriteLine($"Imported GitHub App root for repository key {repositoryKey}.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or CryptographicException)
        {
            Console.Error.WriteLine($"Attempt credential import failed: {exception.Message}");
            return 1;
        }
    }
}
